using System.Net.Http.Json;
using System.Text;
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
public class DebtTests(EaapApiFactory factory)
{
    [Fact]
    public async Task Debt_IsSummedFromFindings_AndExposedViaApi()
    {
        var repositoryId = await SeedRepositoryAsync();

        // Both findings come from a security scanner, so the second (error, no CVSS) is mapped to
        // High: critical (120) + high (60) = 180 minutes.
        var jobId = await RunTrivyJobAsync(repositoryId, "debt1");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var trend = await db.TrendPoints.SingleAsync(t => t.JobId == jobId);
            Assert.Equal(180, trend.DebtTotalMinutes);
        }

        var client = factory.CreateClient();
        var debt = await client.GetFromJsonAsync<JsonElement>($"/api/v1/repositories/{repositoryId}/debt");
        Assert.Equal(180, debt.GetProperty("currentTotalMinutes").GetInt32());
        Assert.Equal(3.0, debt.GetProperty("currentTotalHours").GetDouble());
        Assert.Equal(1, debt.GetProperty("trend").GetArrayLength());
    }

    private async Task<Guid> RunTrivyJobAsync(Guid repositoryId, string tag)
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
                RequestedAnalyzers = ["trivy"],
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
                AnalyzerRuns =
                [
                    new AnalyzerRun { Id = Guid.NewGuid(), AnalyzerId = "trivy", Status = AnalyzerRunStatus.Running, StartedAt = DateTimeOffset.UtcNow }
                ]
            };
            db.AddRange(snapshot, job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            runId = job.AnalyzerRuns[0].Id;
        }

        var sarifPath = $"jobs/{jobId}/{runId}/trivy.sarif";
        await UploadAsync(sarifPath, DebtSarif());
        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath));

        await IngestionTests.WaitForJobFinishedAsync(factory.CreateClient(), jobId);
        return jobId;
    }

    /// <summary>A CVSS 9.8 critical (120 min) and a plain error-level lint (30 min).</summary>
    private static byte[] DebtSarif()
    {
        var log = new
        {
            version = "2.1.0",
            runs = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = "Trivy",
                            rules = new object[]
                            {
                                new { id = "CVE-2021-44228", properties = new { security_severity = "9.8", tags = new[] { "CWE-502" } } }
                            }
                        }
                    },
                    results = new object[]
                    {
                        new
                        {
                            ruleId = "CVE-2021-44228",
                            ruleIndex = 0,
                            level = "error",
                            message = new { text = "log4j critical" },
                            locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = "pom.xml" }, region = new { startLine = 1 } } } }
                        },
                        new
                        {
                            ruleId = "no-eval",
                            level = "error",
                            message = new { text = "eval is forbidden" },
                            locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = "src/a.js" }, region = new { startLine = 2 } } } }
                        }
                    }
                }
            }
        };
        var json = JsonSerializer.Serialize(log).Replace("security_severity", "security-severity");
        return Encoding.UTF8.GetBytes(json);
    }

    private async Task<Guid> SeedRepositoryAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/debt.git",
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
