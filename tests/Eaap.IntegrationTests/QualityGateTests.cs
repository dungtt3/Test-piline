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

    private OpaQualityGate NewGate() =>
        new(new HttpClient { BaseAddress = new Uri(factory.OpaBaseUrl) },
            Options.Create(new OpaOptions { BaseUrl = factory.OpaBaseUrl }));

    private static readonly IReadOnlyDictionary<string, double> NoMetrics = new Dictionary<string, double>();
    private static readonly IReadOnlyDictionary<string, int> NoRules = new Dictionary<string, int>();

    // Lenient defaults matching the platform config: warnings 100, new-warnings disabled, no
    // coverage floor, no failing tests, strict security (0), strict SLO (0), debt-delta disabled.
    private static GateThresholds Defaults(int maxWarnings = 100) =>
        new(maxWarnings, -1, 0, 0, 0, 0, 0, int.MaxValue);

    private static GateSummary Sum(
        int errors = 0, int warnings = 0, int newWarnings = 0,
        IReadOnlyDictionary<string, int>? byRule = null, SecurityCounts? security = null,
        RuntimeInfo? runtime = null, DebtInfo? debt = null) =>
        new(errors, warnings, newWarnings, byRule ?? NoRules, security ?? SecurityCounts.Zero,
            runtime ?? RuntimeInfo.Zero, debt ?? DebtInfo.Zero);

    [Fact]
    public async Task MaxWarningsZero_WarningsOnly_GateFails()
    {
        var result = await NewGate().EvaluateAsync(
            Sum(0, 2, 0, new Dictionary<string, int> { ["semi"] = 2 }),
            NoMetrics, Defaults(maxWarnings: 0));

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("warningCount=2 > max 0"));
    }

    [Fact]
    public async Task DefaultThresholds_FewWarnings_GatePasses()
    {
        var result = await NewGate().EvaluateAsync(
            Sum(0, 12, 0, new Dictionary<string, int> { ["semi"] = 12 }),
            NoMetrics, Defaults());

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task NewWarnings_OverThreshold_GateFails()
    {
        // AC (b): newWarningCount=1 with maxNewWarnings=0.
        var result = await NewGate().EvaluateAsync(
            Sum(0, 5, 1, NoRules), NoMetrics, Defaults() with { MaxNewWarnings = 0 });

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("newWarningCount=1 > max 0"));
    }

    [Fact]
    public async Task NewWarnings_FirstScan_NotFailedWhenThresholdDisabled()
    {
        // Default maxNewWarnings=-1: a first scan where everything is new must still pass.
        var result = await NewGate().EvaluateAsync(
            Sum(0, 5, 5, NoRules), NoMetrics, Defaults());

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Coverage_BelowFloor_GateFails_AboveFloor_Passes()
    {
        // AC (a): 82.5% coverage passes at minCoverageLine=80, fails at 90.
        var metrics = new Dictionary<string, double> { ["coverage.line"] = 82.5 };

        var pass = await NewGate().EvaluateAsync(
            Sum(0, 0, 0, NoRules), metrics, Defaults() with { MinCoverageLine = 80 });
        Assert.True(pass.Passed);

        var fail = await NewGate().EvaluateAsync(
            Sum(0, 0, 0, NoRules), metrics, Defaults() with { MinCoverageLine = 90 });
        Assert.False(fail.Passed);
        Assert.Contains(fail.Violations, v => v.Contains("coverage.line=82.5 < min 90"));
    }

    [Fact]
    public async Task Coverage_MetricMissing_DoesNotFail_ButRecordsSkippedNote()
    {
        var result = await NewGate().EvaluateAsync(
            Sum(0, 0, 0, NoRules), NoMetrics, Defaults() with { MinCoverageLine = 80 });

        Assert.True(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("skipped") && v.Contains("coverage"));
    }

    [Fact]
    public async Task TestsFailed_OverThreshold_GateFails()
    {
        var metrics = new Dictionary<string, double> { ["tests.failed"] = 2, ["tests.total"] = 40 };

        var result = await NewGate().EvaluateAsync(
            Sum(0, 0, 0, NoRules), metrics, Defaults());

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("tests.failed=2 > max 0"));
    }

    [Fact]
    public async Task SecurityHighOrCritical_FailsGateByDefault()
    {
        var result = await NewGate().EvaluateAsync(
            Sum(security: new SecurityCounts(Critical: 1, High: 2, Medium: 5, Low: 9)),
            NoMetrics, Defaults());

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("security.critical=1 > max 0"));
        Assert.Contains(result.Violations, v => v.Contains("security.high=2 > max 0"));
    }

    [Fact]
    public async Task SecurityMediumAndLow_DoNotFailGate()
    {
        var result = await NewGate().EvaluateAsync(
            Sum(security: new SecurityCounts(Critical: 0, High: 0, Medium: 8, Low: 20)),
            NoMetrics, Defaults());

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task SecurityHigh_AllowedWhenThresholdRelaxed()
    {
        // Enterprises relax the strict default via a binding.
        var result = await NewGate().EvaluateAsync(
            Sum(security: new SecurityCounts(Critical: 0, High: 3, Medium: 0, Low: 0)),
            NoMetrics, Defaults() with { MaxSecurityHigh = 5 });

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RuntimeSloViolations_FailGateByDefault()
    {
        var result = await NewGate().EvaluateAsync(
            Sum(runtime: new RuntimeInfo(SloViolations: 1)), NoMetrics, Defaults());

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("runtime.sloViolations=1 > max 0"));
    }

    [Fact]
    public async Task DebtDelta_DisabledByDefault_ButFailsWhenBindingTightensIt()
    {
        // Debt grew by 90 minutes.
        var grew = Sum(debt: new DebtInfo(TotalMinutes: 500, DeltaMinutes: 90));

        var byDefault = await NewGate().EvaluateAsync(grew, NoMetrics, Defaults());
        Assert.True(byDefault.Passed); // maxDebtDeltaMinutes disabled by default

        var enforced = await NewGate().EvaluateAsync(grew, NoMetrics, Defaults() with { MaxDebtDeltaMinutes = 0 });
        Assert.False(enforced.Passed);
        Assert.Contains(enforced.Violations, v => v.Contains("debt.deltaMinutes=90 > max 0"));
    }

    [Fact]
    public async Task CrossLifecycle_AllDimensionsEvaluatedInOneResult()
    {
        // A single evaluation spanning source, coverage, security, runtime and debt, each failing.
        var summary = Sum(
            errors: 1,
            security: new SecurityCounts(Critical: 1, High: 0, Medium: 0, Low: 0),
            runtime: new RuntimeInfo(SloViolations: 2),
            debt: new DebtInfo(TotalMinutes: 600, DeltaMinutes: 45));
        var metrics = new Dictionary<string, double> { ["coverage.line"] = 40, ["tests.failed"] = 3 };
        var thresholds = Defaults() with { MinCoverageLine = 80, MaxDebtDeltaMinutes = 0 };

        var result = await NewGate().EvaluateAsync(summary, metrics, thresholds);

        Assert.False(result.Passed);
        // Violations are grouped by source across the whole lifecycle.
        Assert.Contains(result.Violations, v => v.Contains("errorCount=1"));
        Assert.Contains(result.Violations, v => v.Contains("coverage.line=40 < min 80"));
        Assert.Contains(result.Violations, v => v.Contains("tests.failed=3 > max 0"));
        Assert.Contains(result.Violations, v => v.Contains("security.critical=1 > max 0"));
        Assert.Contains(result.Violations, v => v.Contains("runtime.sloViolations=2 > max 0"));
        Assert.Contains(result.Violations, v => v.Contains("debt.deltaMinutes=45 > max 0"));
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
