using System.Net.Http.Json;
using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure;
using Eaap.Infrastructure.Opa;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class QualityGateTests(EaapApiFactory factory)
{
    [Fact]
    public async Task CleanScan_GatePasses_JobSucceeded()
    {
        var (jobId, runId) = await SeedJobAsync();
        var sarifPath = $"jobs/{jobId}/{runId}/megalinter.sarif";
        using (var s3 = factory.CreateS3Client())
        {
            await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
            {
                BucketName = EaapApiFactory.Bucket,
                Key = sarifPath,
                FilePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "megalinter-clean.sarif")
            });
        }

        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath));

        var job = await IngestionTests.WaitForJobFinishedAsync(factory.CreateClient(), jobId);
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        Assert.True(job.GetProperty("gateEvaluation").GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task MaxWarningsZero_WarningsOnly_GateFails()
    {
        // Direct OPA evaluation with the strict threshold from M6 acceptance criteria (b).
        using var httpClient = new HttpClient { BaseAddress = new Uri(factory.OpaBaseUrl) };
        var gate = new OpaQualityGate(httpClient, Options.Create(new OpaOptions
        {
            BaseUrl = factory.OpaBaseUrl,
            MaxWarnings = 0
        }));

        var result = await gate.EvaluateAsync(new GateSummary(0, 2, new Dictionary<string, int> { ["semi"] = 2 }));

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("warningCount=2 > max 0"));
    }

    [Fact]
    public async Task DefaultThresholds_FewWarnings_GatePasses()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(factory.OpaBaseUrl) };
        var gate = new OpaQualityGate(httpClient, Options.Create(new OpaOptions
        {
            BaseUrl = factory.OpaBaseUrl,
            MaxWarnings = 100
        }));

        var result = await gate.EvaluateAsync(new GateSummary(0, 12, new Dictionary<string, int> { ["semi"] = 12 }));

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    private async Task<(Guid JobId, Guid RunId)> SeedJobAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();

        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/clean.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            Branch = "main",
            CommitSha = Guid.NewGuid().ToString("N") + "11111111",
            StoragePath = "snapshots/clean.tar.gz",
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
}
