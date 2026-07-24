using System.Net;
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
public class SuppressionTests(EaapApiFactory factory)
{
    [Fact]
    public async Task Suppress_ExcludesFindingFromGateAndTrend_UntilItExpires()
    {
        var repositoryId = await SeedRepositoryAsync();
        var client = factory.CreateClient();

        // Job 1: one error-level finding X. The gate fails on the error.
        var job1 = await RunJobAsync(repositoryId, "sup1");
        Assert.Equal("GateFailed", job1.Status.ToString());

        var fingerprint = await SingleFingerprintAsync(job1.JobId);

        // Suppress X with a valid reason.
        var create = await client.PostAsJsonAsync($"/api/v1/repositories/{repositoryId}/suppressions", new
        {
            fingerprint,
            reason = "Accepted risk: false positive confirmed by the security team."
        });
        create.EnsureSuccessStatusCode();

        // Job 2: same finding, now suppressed. Gate no longer counts it -> Succeeded.
        var job2 = await RunJobAsync(repositoryId, "sup2");
        Assert.Equal("Succeeded", job2.Status.ToString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var warning = await db.Warnings.SingleAsync(w => w.JobId == job2.JobId);
            Assert.True(warning.IsSuppressed);

            var trend = await db.TrendPoints.SingleAsync(t => t.JobId == job2.JobId);
            Assert.Equal(0, trend.WarningTotal);
            Assert.Equal(1, trend.WarningSuppressed);
        }

        // Default warnings view hides it; includeSuppressed shows it.
        var hidden = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{job2.JobId}/warnings");
        Assert.Equal(0, hidden.GetProperty("totalCount").GetInt32());
        var shown = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{job2.JobId}/warnings?includeSuppressed=true");
        Assert.Equal(1, shown.GetProperty("totalCount").GetInt32());

        // Expire the suppression, then job 3 counts the finding normally again.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var suppression = await db.Suppressions.SingleAsync(s => s.RepositoryId == repositoryId);
            suppression.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var job3 = await RunJobAsync(repositoryId, "sup3");
        Assert.Equal("GateFailed", job3.Status.ToString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            Assert.False((await db.Warnings.SingleAsync(w => w.JobId == job3.JobId)).IsSuppressed);
        }
    }

    [Fact]
    public async Task CreateSuppression_RejectsShortReasonAndUnknownFingerprint()
    {
        var repositoryId = await SeedRepositoryAsync();
        var client = factory.CreateClient();

        var shortReason = await client.PostAsJsonAsync($"/api/v1/repositories/{repositoryId}/suppressions", new
        {
            fingerprint = new string('a', 64),
            reason = "too short"
        });
        Assert.Equal(HttpStatusCode.BadRequest, shortReason.StatusCode);

        var unknown = await client.PostAsJsonAsync($"/api/v1/repositories/{repositoryId}/suppressions", new
        {
            fingerprint = new string('b', 64),
            reason = "This fingerprint does not exist in the repository at all."
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    private async Task<string> SingleFingerprintAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        return await db.Warnings.Where(w => w.JobId == jobId).Select(w => w.Fingerprint).SingleAsync();
    }

    private async Task<(Guid JobId, JobStatus Status)> RunJobAsyncCore(Guid repositoryId, string tag)
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
                RequestedAnalyzers = ["megalinter"],
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
                AnalyzerRuns =
                [
                    new AnalyzerRun { Id = Guid.NewGuid(), AnalyzerId = "megalinter", Status = AnalyzerRunStatus.Running, StartedAt = DateTimeOffset.UtcNow }
                ]
            };
            db.AddRange(snapshot, job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            runId = job.AnalyzerRuns[0].Id;
        }

        var sarifPath = $"jobs/{jobId}/{runId}/megalinter.sarif";
        await UploadAsync(sarifPath, ErrorFindingSarif());
        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath));

        var job2 = await IngestionTests.WaitForJobFinishedAsync(factory.CreateClient(), jobId);
        return (jobId, Enum.Parse<JobStatus>(job2.GetProperty("status").GetString()!));
    }

    private async Task<(Guid JobId, JobStatus Status)> RunJobAsync(Guid repositoryId, string tag) =>
        await RunJobAsyncCore(repositoryId, tag);

    /// <summary>One stable error-level finding, identical across jobs so its fingerprint is stable.</summary>
    private static byte[] ErrorFindingSarif()
    {
        var log = new
        {
            version = "2.1.0",
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { name = "MegaLinter", version = "1.0.0" } },
                    results = new object[]
                    {
                        new
                        {
                            ruleId = "no-eval",
                            level = "error",
                            message = new { text = "eval() is forbidden" },
                            locations = new[]
                            {
                                new { physicalLocation = new { artifactLocation = new { uri = "src/app.js" }, region = new { startLine = 5 } } }
                            }
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
            CloneUrl = "https://example.invalid/suppress.git",
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
