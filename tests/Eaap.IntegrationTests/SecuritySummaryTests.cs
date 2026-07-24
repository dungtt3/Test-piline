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
public class SecuritySummaryTests(EaapApiFactory factory)
{
    [Fact]
    public async Task SecurityFindings_AreClassified_Filtered_AndSummarized()
    {
        var (jobId, runId) = await SeedSecurityJobAsync();
        var sarifPath = $"jobs/{jobId}/{runId}/trivy.sarif";
        await UploadAsync(sarifPath, BuildSecuritySarif());

        await factory.Services.GetRequiredService<IBus>()
            .Publish(new AnalyzerRunFinished(runId, jobId, "Succeeded", sarifPath));

        var client = factory.CreateClient();
        await IngestionTests.WaitForJobFinishedAsync(client, jobId);

        // Filter by severity (comma list).
        var highAndCritical = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/jobs/{jobId}/warnings?securitySeverity=Critical,High");
        Assert.Equal(3, highAndCritical.GetProperty("totalCount").GetInt32()); // 1 critical + 2 high

        // Filter by CWE.
        var xss = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{jobId}/warnings?cwe=CWE-79");
        Assert.Equal(1, xss.GetProperty("totalCount").GetInt32());
        var xssItem = xss.GetProperty("items")[0];
        Assert.Equal("High", xssItem.GetProperty("securitySeverity").GetString());
        Assert.Equal("CWE-79", xssItem.GetProperty("cwe").GetString());

        // Security summary.
        var summary = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{jobId}/security-summary");
        var bySeverity = summary.GetProperty("bySeverity");
        Assert.Equal(1, bySeverity.GetProperty("critical").GetInt32());
        Assert.Equal(2, bySeverity.GetProperty("high").GetInt32());
        Assert.Equal(1, bySeverity.GetProperty("medium").GetInt32());
        Assert.Equal(0, bySeverity.GetProperty("low").GetInt32());

        var byCwe = summary.GetProperty("byCwe").EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToList();
        Assert.Contains("CWE-502", byCwe);
        Assert.Contains("CWE-79", byCwe);
        Assert.Contains("CWE-89", byCwe);

        var byCve = summary.GetProperty("byCve");
        Assert.Equal(1, byCve.GetArrayLength());
        Assert.Equal("CVE-2021-44228", byCve[0].GetProperty("key").GetString());
    }

    /// <summary>Four security findings spanning several severities, CWEs and one CVE.</summary>
    private static byte[] BuildSecuritySarif()
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
                                new { id = "CVE-2021-44228", properties = new { security_severity = "9.8", tags = new[] { "CWE-502" } } },
                                new { id = "xss-rule", properties = new { security_severity = "7.5", tags = new[] { "CWE-79" } } },
                                new { id = "sqli-rule", properties = new { security_severity = "5.0", tags = new[] { "CWE-89" } } },
                                new { id = "hardcoded-secret", properties = new { security_severity = (string?)null, tags = new[] { "secret" } } }
                            }
                        }
                    },
                    results = new object[]
                    {
                        Result(0, "CVE-2021-44228", "error", "log4j CVE-2021-44228 remote code execution", "pom.xml"),
                        Result(1, "xss-rule", "warning", "reflected XSS in template", "web/view.js"),
                        Result(2, "sqli-rule", "warning", "SQL injection via concatenation", "app/db.py"),
                        Result(3, "hardcoded-secret", "error", "AWS key detected", "config/settings.py")
                    }
                }
            }
        };

        // System.Text.Json writes "security_severity"; rewrite to the SARIF key "security-severity".
        var json = JsonSerializer.Serialize(log).Replace("security_severity", "security-severity");
        return Encoding.UTF8.GetBytes(json);
    }

    private static object Result(int ruleIndex, string ruleId, string level, string message, string uri) => new
    {
        ruleId,
        ruleIndex,
        level,
        message = new { text = message },
        locations = new[]
        {
            new { physicalLocation = new { artifactLocation = new { uri }, region = new { startLine = 1 } } }
        }
    };

    private async Task<(Guid JobId, Guid RunId)> SeedSecurityJobAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/sec.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            Branch = "main",
            CommitSha = Guid.NewGuid().ToString("N") + "22222222",
            StoragePath = "snapshots/sec.tar.gz",
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
        db.AddRange(repository, snapshot, job);
        await db.SaveChangesAsync();
        return (job.Id, job.AnalyzerRuns[0].Id);
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
