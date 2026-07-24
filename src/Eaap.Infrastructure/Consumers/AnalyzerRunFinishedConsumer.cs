using System.Text.Json;
using Eaap.Application;
using Eaap.Domain;
using Eaap.Infrastructure.Baselines;
using Eaap.Infrastructure.Trends;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Ingestion;
using Eaap.Infrastructure.Persistence;
using Eaap.Sarif;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Eaap.Infrastructure.Consumers;

/// <summary>
/// Reacts to an analyzer run finishing: downloads and ingests its SARIF output,
/// and when every run of the job is done, evaluates the quality gate and closes the job.
/// </summary>
public class AnalyzerRunFinishedConsumer(
    EaapDbContext db,
    IObjectStorage storage,
    SarifIngestionService ingestionService,
    MetricsIngestionService metricsIngestionService,
    BaselineService baselineService,
    TrendService trendService,
    IQualityGate qualityGate,
    IOptions<OpaOptions> opaOptions,
    ILogger<AnalyzerRunFinishedConsumer> logger) : IConsumer<AnalyzerRunFinished>
{
    public async Task Consume(ConsumeContext<AnalyzerRunFinished> context)
    {
        var message = context.Message;
        var run = await db.AnalyzerRuns.FirstOrDefaultAsync(r => r.Id == message.AnalyzerRunId, context.CancellationToken);
        if (run is null)
        {
            logger.LogWarning("AnalyzerRun {AnalyzerRunId} not found, ignoring event", message.AnalyzerRunId);
            return;
        }
        if (run.FinishedAt is not null)
        {
            logger.LogInformation("AnalyzerRun {AnalyzerRunId} already finished, ignoring duplicate event", run.Id);
            return;
        }

        run.Status = Enum.TryParse<AnalyzerRunStatus>(message.Status, out var status) ? status : AnalyzerRunStatus.Failed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.SarifArtifactPath = message.SarifArtifactPath;

        if (run.Status == AnalyzerRunStatus.Succeeded && !string.IsNullOrEmpty(message.SarifArtifactPath))
        {
            try
            {
                await using var sarifStream = await storage.DownloadAsync(message.SarifArtifactPath, context.CancellationToken);
                var errors = SarifValidator.Validate(sarifStream);
                if (errors.Count > 0)
                {
                    logger.LogError("SARIF of run {AnalyzerRunId} is invalid: {Errors}", run.Id, string.Join("; ", errors));
                    run.Status = AnalyzerRunStatus.Failed;
                }
                else
                {
                    sarifStream.Position = 0;
                    var count = await ingestionService.IngestAsync(run, sarifStream, context.CancellationToken);
                    logger.LogInformation("Ingested {Count} warnings for run {AnalyzerRunId}", count, run.Id);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to ingest SARIF for run {AnalyzerRunId}", run.Id);
                run.Status = AnalyzerRunStatus.Failed;
            }
        }

        if (run.Status == AnalyzerRunStatus.Succeeded && !string.IsNullOrEmpty(message.MetricsArtifactPath))
        {
            await IngestMetricsAsync(run, message.MetricsArtifactPath, context.CancellationToken);
        }

        await db.SaveChangesAsync(context.CancellationToken);
        await CloseJobIfFinishedAsync(context, run.JobId);
    }

    /// <summary>
    /// metrics.json is optional and advisory: a missing or malformed file is logged and skipped,
    /// never a reason to fail an analyzer run that produced valid SARIF.
    /// </summary>
    private async Task IngestMetricsAsync(AnalyzerRun run, string metricsPath, CancellationToken ct)
    {
        try
        {
            if (!await storage.ExistsAsync(metricsPath, ct))
            {
                return;
            }

            await using var metricsStream = await storage.DownloadAsync(metricsPath, ct);
            var metricSet = await metricsIngestionService.IngestAsync(run, metricsStream, ct);
            if (metricSet is null)
            {
                logger.LogWarning("metrics.json of run {AnalyzerRunId} contained no numeric metric", run.Id);
                return;
            }

            logger.LogInformation("Ingested {Count} metrics for run {AnalyzerRunId}",
                metricSet.Metrics.Count, run.Id);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to ingest metrics.json for run {AnalyzerRunId}, continuing", run.Id);
        }
    }

    private async Task CloseJobIfFinishedAsync(ConsumeContext context, Guid jobId)
    {
        var job = await db.AnalysisJobs
            .Include(j => j.AnalyzerRuns)
            .FirstAsync(j => j.Id == jobId, context.CancellationToken);

        var allFinished = job.AnalyzerRuns.All(r => r.Status is AnalyzerRunStatus.Succeeded or AnalyzerRunStatus.Failed);
        if (!allFinished || job.FinishedAt is not null)
        {
            return;
        }

        var baseline = new BaselineOutcome(0, 0);
        if (job.AnalyzerRuns.Any(r => r.Status == AnalyzerRunStatus.Failed))
        {
            job.Status = JobStatus.Failed;
        }
        else
        {
            // Cross-job dedup runs before the gate so newWarningCount is available to it (M6).
            baseline = await baselineService.ProcessAsync(job.Id, context.CancellationToken);

            var gate = await EvaluateGateAsync(job, context.CancellationToken);
            job.Status = gate.Passed ? JobStatus.Succeeded : JobStatus.GateFailed;
            await context.Publish(new GateEvaluated(job.Id, gate.Passed, gate.PolicyName), context.CancellationToken);
        }

        job.FinishedAt = DateTimeOffset.UtcNow;

        // A finished job on the default branch contributes one trend point (Succeeded or GateFailed).
        if (job.Status is JobStatus.Succeeded or JobStatus.GateFailed)
        {
            await trendService.RecordAsync(job.Id, baseline, context.CancellationToken);
        }

        await db.SaveChangesAsync(context.CancellationToken);
        await context.Publish(new JobFinished(job.Id, job.Status.ToString()), context.CancellationToken);
        logger.LogInformation("Job {JobId} finished with status {Status}", job.Id, job.Status);
    }

    private async Task<GateResult> EvaluateGateAsync(AnalysisJob job, CancellationToken ct)
    {
        // Suppressed findings are excluded from the summary sent to OPA (phase 3 section 5).
        var counts = await db.Warnings
            .Where(w => w.JobId == job.Id && !w.IsSuppressed)
            .GroupBy(w => new { w.RuleId, w.Level })
            .Select(g => new { g.Key.RuleId, g.Key.Level, Count = g.Count() })
            .ToListAsync(ct);

        var newWarningCount = await db.Warnings.CountAsync(w => w.JobId == job.Id && w.IsNew && !w.IsSuppressed, ct);

        var securityCounts = await db.Warnings
            .Where(w => w.JobId == job.Id && !w.IsSuppressed && w.SecuritySeverity != SecuritySeverity.None)
            .GroupBy(w => w.SecuritySeverity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int CountOf(SecuritySeverity s) => securityCounts.FirstOrDefault(x => x.Severity == s)?.Count ?? 0;

        // Runtime SLO breaches are the non-suppressed slo.* findings of the job.
        var sloViolations = await db.Warnings.CountAsync(
            w => w.JobId == job.Id && !w.IsSuppressed && w.RuleId.StartsWith("slo."), ct);

        var (debtTotal, debtDelta) = await ComputeDebtAsync(job, ct);

        var summary = new GateSummary(
            counts.Where(c => c.Level == WarningLevel.Error).Sum(c => c.Count),
            counts.Where(c => c.Level == WarningLevel.Warning).Sum(c => c.Count),
            newWarningCount,
            counts.GroupBy(c => c.RuleId).ToDictionary(g => g.Key, g => g.Sum(c => c.Count)),
            new SecurityCounts(
                CountOf(SecuritySeverity.Critical),
                CountOf(SecuritySeverity.High),
                CountOf(SecuritySeverity.Medium),
                CountOf(SecuritySeverity.Low)),
            new RuntimeInfo(sloViolations),
            new DebtInfo(debtTotal, debtDelta));

        var metrics = await GatherMetricsAsync(job.Id, ct);
        var thresholds = await ResolveThresholdsAsync(job.SnapshotId, ct);

        var gate = await qualityGate.EvaluateAsync(summary, metrics, thresholds, ct);

        db.GateEvaluations.Add(new GateEvaluation
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            PolicyName = gate.PolicyName,
            Passed = gate.Passed,
            Violations = JsonSerializer.Serialize(gate.Violations),
            EvaluatedAt = DateTimeOffset.UtcNow
        });
        return gate;
    }

    /// <summary>
    /// Current job debt (suppressed findings are already 0) and its delta from the most recent
    /// default-branch trend point. The current job's trend point does not exist yet at gate time.
    /// </summary>
    private async Task<(int Total, int Delta)> ComputeDebtAsync(AnalysisJob job, CancellationToken ct)
    {
        var total = await db.Warnings.Where(w => w.JobId == job.Id).SumAsync(w => w.DebtMinutes, ct);

        var repositoryId = await db.Snapshots
            .Where(s => s.Id == job.SnapshotId)
            .Select(s => s.RepositoryId)
            .FirstAsync(ct);

        var previousTotal = await db.TrendPoints
            .Where(t => t.RepositoryId == repositoryId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (int?)t.DebtTotalMinutes)
            .FirstOrDefaultAsync(ct) ?? 0;

        return (total, total - previousTotal);
    }

    /// <summary>Merges every metric set of the job; a later run's value wins on a key clash.</summary>
    private async Task<Dictionary<string, double>> GatherMetricsAsync(Guid jobId, CancellationToken ct)
    {
        var metrics = new Dictionary<string, double>();
        var sets = await db.MetricSets.Where(m => m.JobId == jobId).ToListAsync(ct);
        foreach (var set in sets)
        {
            foreach (var (key, value) in set.Metrics)
            {
                metrics[key] = value;
            }
        }
        return metrics;
    }

    /// <summary>Per-repository binding overrides the platform defaults; absent keys keep the default.</summary>
    private async Task<GateThresholds> ResolveThresholdsAsync(Guid snapshotId, CancellationToken ct)
    {
        var defaults = opaOptions.Value;
        var thresholds = new GateThresholds(
            defaults.MaxWarnings,
            defaults.MaxNewWarnings,
            defaults.MinCoverageLine,
            defaults.MaxTestsFailed,
            defaults.MaxSecurityCritical,
            defaults.MaxSecurityHigh,
            defaults.MaxSloViolations,
            defaults.MaxDebtDeltaMinutes);

        var repositoryId = await db.Snapshots
            .Where(s => s.Id == snapshotId)
            .Select(s => s.RepositoryId)
            .FirstAsync(ct);

        var binding = await db.GatePolicyBindings
            .FirstOrDefaultAsync(b => b.RepositoryId == repositoryId, ct);
        if (binding is null)
        {
            return thresholds;
        }

        var t = binding.Thresholds;
        return thresholds with
        {
            MaxWarnings = t.TryGetValue("maxWarnings", out var mw) ? (int)mw : thresholds.MaxWarnings,
            MaxNewWarnings = t.TryGetValue("maxNewWarnings", out var mnw) ? (int)mnw : thresholds.MaxNewWarnings,
            MinCoverageLine = t.TryGetValue("minCoverageLine", out var mcl) ? mcl : thresholds.MinCoverageLine,
            MaxTestsFailed = t.TryGetValue("maxTestsFailed", out var mtf) ? (int)mtf : thresholds.MaxTestsFailed,
            MaxSecurityCritical = t.TryGetValue("maxSecurityCritical", out var msc) ? (int)msc : thresholds.MaxSecurityCritical,
            MaxSecurityHigh = t.TryGetValue("maxSecurityHigh", out var msh) ? (int)msh : thresholds.MaxSecurityHigh,
            MaxSloViolations = t.TryGetValue("maxSloViolations", out var msv) ? (int)msv : thresholds.MaxSloViolations,
            MaxDebtDeltaMinutes = t.TryGetValue("maxDebtDeltaMinutes", out var mdd) ? (int)mdd : thresholds.MaxDebtDeltaMinutes
        };
    }
}
