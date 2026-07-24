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
public class CrossLifecycleGateTests(EaapApiFactory factory)
{
    [Fact]
    public async Task OneGate_SpansCoverageAndRuntime_AcrossTwoAnalyzerRunsOfOneJob()
    {
        var repositoryId = await SeedRepositoryAsync();
        var client = factory.CreateClient();

        // Tighten coverage so the coverage run's 40% will fail.
        var put = await client.PutAsJsonAsync($"/api/v1/repositories/{repositoryId}/gate", new
        {
            thresholds = new Dictionary<string, double> { ["minCoverageLine"] = 80 }
        });
        put.EnsureSuccessStatusCode();

        Guid jobId, coverageRunId, sloRunId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var snapshot = new Snapshot
            {
                Id = Guid.NewGuid(),
                RepositoryId = repositoryId,
                Branch = "main",
                CommitSha = Guid.NewGuid().ToString("N") + "44444444",
                StoragePath = "snapshots/xlc.tar.gz",
                SizeBytes = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var coverageRun = new AnalyzerRun
            {
                Id = Guid.NewGuid(),
                AnalyzerId = "coverage",
                // Already finished, with its metric pre-recorded — so a single event closes the job
                // and the gate aggregates both runs (race-free).
                Status = AnalyzerRunStatus.Succeeded,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow
            };
            var sloRun = new AnalyzerRun
            {
                Id = Guid.NewGuid(),
                AnalyzerId = "prometheus-slo",
                Status = AnalyzerRunStatus.Running,
                StartedAt = DateTimeOffset.UtcNow
            };
            var job = new AnalysisJob
            {
                Id = Guid.NewGuid(),
                SnapshotId = snapshot.Id,
                Status = JobStatus.Running,
                RequestedAnalyzers = ["coverage", "prometheus-slo"],
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
                AnalyzerRuns = [coverageRun, sloRun]
            };
            db.AddRange(snapshot, job);
            db.MetricSets.Add(new MetricSet
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                AnalyzerRunId = coverageRun.Id,
                Metrics = new Dictionary<string, double> { ["coverage.line"] = 40 },
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            jobId = job.Id;
            coverageRunId = coverageRun.Id;
            sloRunId = sloRun.Id;
        }
        _ = coverageRunId;

        // The prometheus-slo run reports one SLO violation.
        var sarifPath = $"jobs/{jobId}/{sloRunId}/prometheus-slo.sarif";
        await UploadAsync(sarifPath, SloSarif());
        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(sloRunId, jobId, "Succeeded", sarifPath));

        var job2 = await IngestionTests.WaitForJobFinishedAsync(client, jobId);
        Assert.Equal("GateFailed", job2.GetProperty("status").GetString());

        // A single gate evaluation carries violations from both the coverage and runtime dimensions.
        var violations = job2.GetProperty("gateEvaluation").GetProperty("violations")
            .EnumerateArray().Select(v => v.GetString()!).ToArray();
        Assert.Contains(violations, v => v.Contains("coverage.line=40 < min 80"));
        Assert.Contains(violations, v => v.Contains("runtime.sloViolations=1 > max 0"));
    }

    private static byte[] SloSarif()
    {
        var log = new
        {
            version = "2.1.0",
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { name = "EaapPrometheusSlo", version = "1.0.0" } },
                    results = new object[]
                    {
                        new
                        {
                            ruleId = "slo.latency-p95",
                            level = "error",
                            message = new { text = "SLO 'latency-p95' violated: observed 0.42 is not < 0.3" },
                            properties = new { fingerprintKey = "latency-p95", observedValue = 0.42, threshold = 0.3 }
                        }
                    }
                }
            }
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(log));
    }

    private async Task<Guid> SeedRepositoryAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/xlc.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return repository.Id;
    }

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
}
