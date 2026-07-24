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
using Npgsql;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class TrendTests(EaapApiFactory factory)
{
    [Fact]
    public async Task FinishedDefaultBranchJobs_MaterializeTrendPointsWithRealNumbers()
    {
        var repositoryId = await SeedRepositoryAsync();

        // Job 1: three findings, coverage 75% -> all new.
        await RunJobAsync(repositoryId, "t1", ["ruleA", "ruleB", "ruleC"], coverageLine: 75, testsFailed: 0);
        // Job 2: ruleC fixed, ruleD added -> 1 new, 1 resolved, coverage up.
        await RunJobAsync(repositoryId, "t2", ["ruleA", "ruleB", "ruleD"], coverageLine: 88, testsFailed: 0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var points = await db.TrendPoints
            .Where(t => t.RepositoryId == repositoryId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, points.Count);

        Assert.Equal(3, points[0].WarningTotal);
        Assert.Equal(3, points[0].WarningNew);
        Assert.Equal(0, points[0].WarningResolved);
        Assert.Equal(75, points[0].CoverageLine);

        Assert.Equal(3, points[1].WarningTotal);
        Assert.Equal(1, points[1].WarningNew);
        Assert.Equal(1, points[1].WarningResolved);
        Assert.Equal(88, points[1].CoverageLine);
    }

    [Fact]
    public async Task TrendApi_ReturnsPointsInChronologicalOrder()
    {
        var repositoryId = await SeedRepositoryAsync();
        await RunJobAsync(repositoryId, "api1", ["ruleA"], coverageLine: 60, testsFailed: 0);
        await RunJobAsync(repositoryId, "api2", ["ruleA"], coverageLine: 61, testsFailed: 0);

        var client = factory.CreateClient();
        var trend = await client.GetFromJsonAsync<JsonElement>($"/api/v1/repositories/{repositoryId}/trend");

        Assert.Equal(2, trend.GetArrayLength());
        var first = trend[0];
        Assert.Equal(60, first.GetProperty("coverageLine").GetDouble());
    }

    [Fact]
    public async Task GrafanaRoRole_CanSelectTrendPoints_ButCannotWrite()
    {
        // Materialize at least one point so the read returns data.
        var repositoryId = await SeedRepositoryAsync();
        await RunJobAsync(repositoryId, "ro1", ["ruleA"], coverageLine: 50, testsFailed: 0);

        var builder = new NpgsqlConnectionStringBuilder(factory.PostgresConnectionString)
        {
            Username = "grafana_ro",
            Password = "grafana-ro-dev"
        };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        // SELECT is granted.
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM \"TrendPoints\"", conn))
        {
            var count = (long)(await read.ExecuteScalarAsync())!;
            Assert.True(count >= 1);
        }

        // INSERT and UPDATE are denied.
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO \"TrendPoints\" (\"Id\") VALUES (gen_random_uuid())", conn))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState); // insufficient_privilege
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE \"Warnings\" SET \"Message\" = 'x'", conn))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }
    }

    private async Task RunJobAsync(Guid repositoryId, string tag, string[] ruleIds, double coverageLine, int testsFailed)
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

        var basePath = $"jobs/{jobId}/{runId}";
        await UploadAsync($"{basePath}/megalinter.sarif", BuildSarif(ruleIds));
        var metrics = $"{{\"metrics\":{{\"coverage.line\":{coverageLine.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"\"tests.total\":10,\"tests.failed\":{testsFailed}}}}}";
        await UploadAsync($"{basePath}/metrics.json", Encoding.UTF8.GetBytes(metrics));

        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", $"{basePath}/megalinter.sarif", $"{basePath}/metrics.json"));

        await IngestionTests.WaitForJobFinishedAsync(factory.CreateClient(), jobId);
    }

    private static byte[] BuildSarif(string[] ruleIds)
    {
        var results = ruleIds.Select(rule => new
        {
            ruleId = rule,
            level = "warning",
            message = new { text = $"Finding {rule}" },
            locations = new[]
            {
                new { physicalLocation = new { artifactLocation = new { uri = $"src/{rule}.cs" }, region = new { startLine = 3 } } }
            }
        });
        var log = new
        {
            version = "2.1.0",
            runs = new[] { new { tool = new { driver = new { name = "MegaLinter", version = "1.0.0" } }, results } }
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(log));
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

    private async Task<Guid> SeedRepositoryAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/trend.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return repository.Id;
    }
}
