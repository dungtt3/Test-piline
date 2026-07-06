using System.Net.Http.Json;
using System.Text.Json;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class IngestionTests(EaapApiFactory factory)
{
    [Fact]
    public async Task AnalyzerRunFinished_IngestsSarifFromMinio_WarningsQueryableViaApi()
    {
        // Seed a job with one pending analyzer run.
        var (jobId, runId) = await SeedJobAsync();

        // Put the SARIF fixture where the workflow would have uploaded it.
        var sarifPath = $"jobs/{jobId}/{runId}/megalinter.sarif";
        var fixtureBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "megalinter-valid.sarif"));
        using (var s3 = factory.CreateS3Client())
        {
            using var content = new MemoryStream(fixtureBytes);
            await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
            {
                BucketName = EaapApiFactory.Bucket,
                Key = sarifPath,
                InputStream = content
            });
        }

        // Publish the event the orchestrator would publish.
        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath));

        var client = factory.CreateClient();
        var job = await WaitForJobFinishedAsync(client, jobId);

        // Fixture contains 1 error result -> quality gate must fail the job.
        Assert.Equal("GateFailed", job.GetProperty("status").GetString());
        var gate = job.GetProperty("gateEvaluation");
        Assert.False(gate.GetProperty("passed").GetBoolean());
        Assert.Contains(gate.GetProperty("violations").EnumerateArray().Select(v => v.GetString()),
            v => v!.Contains("errorCount=1"));

        // 4 SARIF results, 1 exact duplicate -> 3 warnings.
        var warnings = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{jobId}/warnings");
        Assert.Equal(3, warnings.GetProperty("totalCount").GetInt32());

        var errorsOnly = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{jobId}/warnings?level=Error");
        Assert.Equal(1, errorsOnly.GetProperty("totalCount").GetInt32());

        var byRule = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{jobId}/warnings?ruleId=semi");
        Assert.Equal(1, byRule.GetProperty("totalCount").GetInt32());

        // The duplicate bumped properties.duplicateCount on the kept warning.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var semi = await db.Warnings.SingleAsync(w => w.JobId == jobId && w.RuleId == "semi");
            using var raw = JsonDocument.Parse(semi.SarifRaw);
            Assert.Equal(2, raw.RootElement.GetProperty("properties").GetProperty("duplicateCount").GetInt32());

            var run = await db.AnalyzerRuns.SingleAsync(r => r.Id == runId);
            Assert.Equal(AnalyzerRunStatus.Succeeded, run.Status);
            Assert.Equal(3, run.WarningCount);
        }

        // Merged SARIF is served with the SARIF media type and contains both runs.
        var sarifResponse = await client.GetAsync($"/api/v1/jobs/{jobId}/sarif");
        sarifResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/sarif+json", sarifResponse.Content.Headers.ContentType!.MediaType);
        using var merged = JsonDocument.Parse(await sarifResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, merged.RootElement.GetProperty("runs").GetArrayLength());
    }

    private async Task<(Guid JobId, Guid RunId)> SeedJobAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();

        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/fixture.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            Branch = "main",
            CommitSha = Guid.NewGuid().ToString("N") + "00000000",
            StoragePath = "snapshots/fixture.tar.gz",
            SizeBytes = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SnapshotId = snapshot.Id,
            Status = JobStatus.Running,
            RequestedAnalyzers = ["megalinter"],
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
            AnalyzerRuns =
            [
                new AnalyzerRun
                {
                    Id = Guid.NewGuid(),
                    AnalyzerId = "megalinter",
                    Status = AnalyzerRunStatus.Running,
                    StartedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        db.AddRange(repository, snapshot, job);
        await db.SaveChangesAsync();
        return (job.Id, job.AnalyzerRuns[0].Id);
    }

    internal static async Task<JsonElement> WaitForJobFinishedAsync(HttpClient client, Guid jobId, int timeoutSeconds = 60)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{jobId}");
            var status = job.GetProperty("status").GetString();
            if (status is not ("Pending" or "Running"))
            {
                return job;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Job {jobId} did not finish within {timeoutSeconds}s.");
    }
}
