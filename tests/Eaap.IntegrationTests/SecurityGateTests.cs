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
public class SecurityGateTests(EaapApiFactory factory)
{
    [Fact]
    public async Task CriticalFinding_FailsGate_ThenSuppressing_It_Passes()
    {
        var repositoryId = await SeedRepositoryAsync();
        var client = factory.CreateClient();

        // Job 1: one critical security finding -> strict default gate fails on it.
        var job1 = await RunTrivyJobAsync(repositoryId, "secgate1");
        Assert.Equal("GateFailed", job1.Status.ToString());
        Assert.Contains(job1.Violations, v => v.Contains("security.critical=1 > max 0"));

        var fingerprint = await SingleFingerprintAsync(job1.JobId);
        var create = await client.PostAsJsonAsync($"/api/v1/repositories/{repositoryId}/suppressions", new
        {
            fingerprint,
            reason = "Accepted for the demo: this critical is a known false positive."
        });
        create.EnsureSuccessStatusCode();

        // Job 2: the critical is suppressed, so the security summary is clean -> gate passes.
        var job2 = await RunTrivyJobAsync(repositoryId, "secgate2");
        Assert.Equal("Succeeded", job2.Status.ToString());
    }

    private async Task<string> SingleFingerprintAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        return await db.Warnings.Where(w => w.JobId == jobId).Select(w => w.Fingerprint).SingleAsync();
    }

    private async Task<(Guid JobId, JobStatus Status, string[] Violations)> RunTrivyJobAsync(Guid repositoryId, string tag)
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
        await UploadAsync(sarifPath, CriticalSarif());
        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath));

        var job2 = await IngestionTests.WaitForJobFinishedAsync(factory.CreateClient(), jobId);
        var violations = job2.GetProperty("gateEvaluation").GetProperty("violations")
            .EnumerateArray().Select(v => v.GetString()!).ToArray();
        return (jobId, Enum.Parse<JobStatus>(job2.GetProperty("status").GetString()!), violations);
    }

    /// <summary>One CVSS 9.8 (critical) finding, stable across jobs.</summary>
    private static byte[] CriticalSarif()
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
                            message = new { text = "log4j CVE-2021-44228 remote code execution" },
                            locations = new[]
                            {
                                new { physicalLocation = new { artifactLocation = new { uri = "pom.xml" }, region = new { startLine = 42 } } }
                            }
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
            CloneUrl = "https://example.invalid/secgate.git",
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
