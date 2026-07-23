using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class MetricsIngestionTests(EaapApiFactory factory)
{
    [Fact]
    public async Task AnalyzerRunFinished_WithMetricsJson_StoresMetricSet()
    {
        var (jobId, runId) = await SeedJobAsync();
        var sarifPath = $"jobs/{jobId}/{runId}/coverage.sarif";
        var metricsPath = $"jobs/{jobId}/{runId}/metrics.json";

        await UploadFixtureAsync("megalinter-clean.sarif", sarifPath);
        await UploadFixtureAsync("metrics-valid.json", metricsPath);

        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath, metricsPath));

        var client = factory.CreateClient();
        var job = await IngestionTests.WaitForJobFinishedAsync(client, jobId);

        // Clean SARIF: nothing for the gate to complain about.
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();

        var metricSet = await db.MetricSets.SingleAsync(m => m.JobId == jobId);
        Assert.Equal(runId, metricSet.AnalyzerRunId);
        Assert.Equal(7, metricSet.Metrics.Count);
        Assert.Equal(82.5, metricSet.Metrics["coverage.line"]);
        Assert.Equal(240, metricSet.Metrics["tests.total"]);
        Assert.Equal(2, metricSet.Metrics["tests.failed"]);
    }

    [Fact]
    public async Task AnalyzerRunFinished_WithoutMetricsJson_SucceedsWithNoMetricSet()
    {
        // Phase 1 adapters emit no metrics.json at all; the run must still succeed untouched.
        var (jobId, runId) = await SeedJobAsync();
        var sarifPath = $"jobs/{jobId}/{runId}/megalinter.sarif";
        var metricsPath = $"jobs/{jobId}/{runId}/metrics.json";

        await UploadFixtureAsync("megalinter-clean.sarif", sarifPath);

        // The orchestrator always points at a metrics key; the object simply is not there.
        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath, metricsPath));

        var client = factory.CreateClient();
        var job = await IngestionTests.WaitForJobFinishedAsync(client, jobId);
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();

        Assert.False(await db.MetricSets.AnyAsync(m => m.JobId == jobId));
        var run = await db.AnalyzerRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal(AnalyzerRunStatus.Succeeded, run.Status);
    }

    private async Task UploadFixtureAsync(string fixtureName, string key)
    {
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName));
        using var s3 = factory.CreateS3Client();
        using var content = new MemoryStream(bytes);
        await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = EaapApiFactory.Bucket,
            Key = key,
            InputStream = content
        });
    }

    private async Task<(Guid JobId, Guid RunId)> SeedJobAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();

        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/metrics-fixture.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            Branch = "main",
            CommitSha = Guid.NewGuid().ToString("N") + "00000000",
            StoragePath = "snapshots/metrics-fixture.tar.gz",
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
        db.AddRange(repository, snapshot, job);
        await db.SaveChangesAsync();
        return (job.Id, job.AnalyzerRuns[0].Id);
    }
}
