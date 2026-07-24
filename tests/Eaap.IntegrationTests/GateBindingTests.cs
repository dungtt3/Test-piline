using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class GateBindingTests(EaapApiFactory factory)
{
    [Fact]
    public async Task PutAndGetBinding_RoundTripsThresholds()
    {
        var repositoryId = await SeedRepositoryAsync();
        var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"/api/v1/repositories/{repositoryId}/gate", new
        {
            thresholds = new Dictionary<string, double> { ["minCoverageLine"] = 90, ["maxNewWarnings"] = 0 }
        });
        put.EnsureSuccessStatusCode();

        var get = await client.GetFromJsonAsync<JsonElement>($"/api/v1/repositories/{repositoryId}/gate");
        var thresholds = get.GetProperty("thresholds");
        Assert.Equal(90, thresholds.GetProperty("minCoverageLine").GetDouble());
        Assert.Equal(0, thresholds.GetProperty("maxNewWarnings").GetDouble());
    }

    [Fact]
    public async Task GetBinding_UnknownRepository_Returns404()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/v1/repositories/{Guid.NewGuid()}/gate");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Binding_MinCoverageLine_TurnsAPassingCoverageIntoAGateFailure()
    {
        // Coverage 82.5% passes the lenient default, but a per-repo floor of 90 must fail it.
        var repositoryId = await SeedRepositoryAsync();
        var client = factory.CreateClient();

        var passing = await RunCoverageJobAsync(repositoryId, "cov-pass", coverageLine: 82.5);
        Assert.Equal("Succeeded", passing.GetProperty("status").GetString());

        var put = await client.PutAsJsonAsync($"/api/v1/repositories/{repositoryId}/gate", new
        {
            thresholds = new Dictionary<string, double> { ["minCoverageLine"] = 90 }
        });
        put.EnsureSuccessStatusCode();

        var failing = await RunCoverageJobAsync(repositoryId, "cov-fail", coverageLine: 82.5);
        Assert.Equal("GateFailed", failing.GetProperty("status").GetString());
        Assert.Contains(
            failing.GetProperty("gateEvaluation").GetProperty("violations").EnumerateArray().Select(v => v.GetString()),
            v => v!.Contains("coverage.line") && v.Contains("min 90"));
    }

    /// <summary>Runs a job whose analyzer emits only a coverage metric (no warnings).</summary>
    private async Task<JsonElement> RunCoverageJobAsync(Guid repositoryId, string tag, double coverageLine)
    {
        Guid jobId, runId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var snapshot = new Snapshot
            {
                Id = Guid.NewGuid(),
                RepositoryId = repositoryId,
                Branch = "main",
                CommitSha = (Convert.ToHexString(Encoding.UTF8.GetBytes(tag)) + new string('0', 40))[..40].ToLowerInvariant(),
                StoragePath = $"snapshots/{tag}.tar.gz",
                SizeBytes = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var job = new AnalysisJob
            {
                Id = Guid.NewGuid(),
                SnapshotId = snapshot.Id,
                Status = JobStatus.Running,
                RequestedAnalyzers = ["coverage"],
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
                AnalyzerRuns =
                [
                    new AnalyzerRun
                    {
                        Id = Guid.NewGuid(),
                        AnalyzerId = "coverage",
                        Status = AnalyzerRunStatus.Running,
                        StartedAt = DateTimeOffset.UtcNow
                    }
                ]
            };
            db.AddRange(snapshot, job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            runId = job.AnalyzerRuns[0].Id;
        }

        var basePath = $"jobs/{jobId}/{runId}";
        await UploadAsync($"{basePath}/coverage.sarif", EmptySarif());
        var metricsJson = "{\"metrics\":{\"coverage.line\":"
            + coverageLine.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
        await UploadAsync($"{basePath}/metrics.json", Encoding.UTF8.GetBytes(metricsJson));

        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded",
                $"{basePath}/coverage.sarif", $"{basePath}/metrics.json"));

        return await IngestionTests.WaitForJobFinishedAsync(factory.CreateClient(), jobId);
    }

    private static byte[] EmptySarif() => Encoding.UTF8.GetBytes("""
        {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"EaapCoverage","version":"1.0.0"}},"results":[]}]}
        """);

    private async Task UploadAsync(string key, byte[] bytes)
    {
        using var s3 = factory.CreateS3Client();
        using var content = new MemoryStream(bytes);
        await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = EaapApiFactory.Bucket,
            Key = key,
            InputStream = content
        });
    }

    private async Task<Guid> SeedRepositoryAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/gate-binding.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return repository.Id;
    }
}
