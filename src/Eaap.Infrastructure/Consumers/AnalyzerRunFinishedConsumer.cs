using System.Text.Json;
using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Ingestion;
using Eaap.Infrastructure.Persistence;
using Eaap.Sarif;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Eaap.Infrastructure.Consumers;

/// <summary>
/// Reacts to an analyzer run finishing: downloads and ingests its SARIF output,
/// and when every run of the job is done, evaluates the quality gate and closes the job.
/// </summary>
public class AnalyzerRunFinishedConsumer(
    EaapDbContext db,
    IObjectStorage storage,
    SarifIngestionService ingestionService,
    IQualityGate qualityGate,
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

        await db.SaveChangesAsync(context.CancellationToken);
        await CloseJobIfFinishedAsync(context, run.JobId);
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

        if (job.AnalyzerRuns.Any(r => r.Status == AnalyzerRunStatus.Failed))
        {
            job.Status = JobStatus.Failed;
        }
        else
        {
            var gate = await EvaluateGateAsync(job, context.CancellationToken);
            job.Status = gate.Passed ? JobStatus.Succeeded : JobStatus.GateFailed;
            await context.Publish(new GateEvaluated(job.Id, gate.Passed, gate.PolicyName), context.CancellationToken);
        }

        job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
        await context.Publish(new JobFinished(job.Id, job.Status.ToString()), context.CancellationToken);
        logger.LogInformation("Job {JobId} finished with status {Status}", job.Id, job.Status);
    }

    private async Task<GateResult> EvaluateGateAsync(AnalysisJob job, CancellationToken ct)
    {
        var counts = await db.Warnings
            .Where(w => w.JobId == job.Id)
            .GroupBy(w => new { w.RuleId, w.Level })
            .Select(g => new { g.Key.RuleId, g.Key.Level, Count = g.Count() })
            .ToListAsync(ct);

        var summary = new GateSummary(
            counts.Where(c => c.Level == WarningLevel.Error).Sum(c => c.Count),
            counts.Where(c => c.Level == WarningLevel.Warning).Sum(c => c.Count),
            counts.GroupBy(c => c.RuleId).ToDictionary(g => g.Key, g => g.Sum(c => c.Count)));

        var gate = await qualityGate.EvaluateAsync(summary, ct);

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
}
