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
public class BaselineTests(EaapApiFactory factory)
{
    // A finding is identified in these fixtures by its rule id; each maps to one SARIF result.
    private static readonly string[] Job1Findings = ["ruleA", "ruleB", "ruleC", "ruleD", "ruleE"];
    private static readonly string[] Job2Findings = ["ruleA", "ruleB", "ruleC", "ruleF"]; // fixed D,E; added F

    [Fact]
    public async Task ThreeConsecutiveJobs_TrackNewAndResolvedAcrossTheRepository()
    {
        var repositoryId = await SeedRepositoryAsync();

        // Job 1: five findings, all brand new -> five active baselines.
        var job1 = await RunJobAsync(repositoryId, "commit1", Job1Findings);
        Assert.Equal(5, await NewWarningCountAsync(job1));
        Assert.Equal(5, await ActiveBaselineCountAsync(repositoryId));

        // Job 2: three carried over, D and E fixed, F added -> 1 new, 2 resolved.
        var job2 = await RunJobAsync(repositoryId, "commit2", Job2Findings);
        Assert.Equal(1, await NewWarningCountAsync(job2));
        Assert.Equal(4, await ActiveBaselineCountAsync(repositoryId));
        Assert.Equal(2, await ResolvedBaselineCountAsync(repositoryId));

        // Only ruleF should be flagged new in job 2.
        var newRules = await NewRuleIdsAsync(job2);
        Assert.Equal(["ruleF"], newRules);

        // Job 3: identical to job 2 -> nothing new, nothing newly resolved.
        var job3 = await RunJobAsync(repositoryId, "commit3", Job2Findings);
        Assert.Equal(0, await NewWarningCountAsync(job3));
        Assert.Equal(4, await ActiveBaselineCountAsync(repositoryId));
        Assert.Equal(2, await ResolvedBaselineCountAsync(repositoryId));
    }

    [Fact]
    public async Task BaselineApi_FiltersByStatus()
    {
        var repositoryId = await SeedRepositoryAsync();
        await RunJobAsync(repositoryId, "b-commit1", Job1Findings);
        await RunJobAsync(repositoryId, "b-commit2", Job2Findings);

        var client = factory.CreateClient();

        var active = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/repositories/{repositoryId}/baseline?status=Active");
        Assert.Equal(4, active.GetProperty("totalCount").GetInt32());

        var resolved = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/repositories/{repositoryId}/baseline?status=Resolved");
        Assert.Equal(2, resolved.GetProperty("totalCount").GetInt32());

        var all = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/repositories/{repositoryId}/baseline");
        Assert.Equal(6, all.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task WarningsApi_FiltersByIsNew()
    {
        var repositoryId = await SeedRepositoryAsync();
        await RunJobAsync(repositoryId, "n-commit1", Job1Findings);
        var job2 = await RunJobAsync(repositoryId, "n-commit2", Job2Findings);

        var client = factory.CreateClient();

        var onlyNew = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/jobs/{job2}/warnings?isNew=true");
        Assert.Equal(1, onlyNew.GetProperty("totalCount").GetInt32());

        var onlyOld = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/jobs/{job2}/warnings?isNew=false");
        Assert.Equal(3, onlyOld.GetProperty("totalCount").GetInt32());
    }

    private async Task<Guid> RunJobAsync(Guid repositoryId, string commitTag, string[] ruleIds)
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
                CommitSha = Pad(commitTag),
                StoragePath = $"snapshots/{commitTag}.tar.gz",
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
            db.AddRange(snapshot, job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            runId = job.AnalyzerRuns[0].Id;
        }

        var sarifPath = $"jobs/{jobId}/{runId}/megalinter.sarif";
        await UploadAsync(sarifPath, BuildSarif(ruleIds));

        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath));

        await IngestionTests.WaitForJobFinishedAsync(factory.CreateClient(), jobId);
        return jobId;
    }

    /// <summary>Builds a valid SARIF log with one warning-level result per rule id.</summary>
    private static byte[] BuildSarif(string[] ruleIds)
    {
        var results = ruleIds.Select(rule => new
        {
            ruleId = rule,
            level = "warning",
            message = new { text = $"Finding {rule}" },
            locations = new[]
            {
                new
                {
                    physicalLocation = new
                    {
                        artifactLocation = new { uri = $"src/{rule}.cs" },
                        region = new { startLine = 10 }
                    }
                }
            }
        });

        var log = new
        {
            version = "2.1.0",
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { name = "MegaLinter", version = "1.0.0" } },
                    results
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
            CloneUrl = "https://example.invalid/baseline.git",
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

    private async Task<int> NewWarningCountAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        return await db.Warnings.CountAsync(w => w.JobId == jobId && w.IsNew);
    }

    private async Task<string[]> NewRuleIdsAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        return await db.Warnings
            .Where(w => w.JobId == jobId && w.IsNew)
            .Select(w => w.RuleId)
            .OrderBy(r => r)
            .ToArrayAsync();
    }

    private async Task<int> ActiveBaselineCountAsync(Guid repositoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        return await db.WarningBaselines.CountAsync(b => b.RepositoryId == repositoryId && b.Status == BaselineStatus.Active);
    }

    private async Task<int> ResolvedBaselineCountAsync(Guid repositoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        return await db.WarningBaselines.CountAsync(b => b.RepositoryId == repositoryId && b.Status == BaselineStatus.Resolved);
    }

    private static string Pad(string tag)
    {
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(tag)).ToLowerInvariant();
        return (hex + new string('0', 40))[..40];
    }
}
