using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Eaap.Infrastructure.Consumers;

/// <summary>
/// Turns a JobRequested event into an Argo workflow submission.
/// Phase 1 supports a single analyzer per job (megalinter); extra analyzers fail their runs.
/// </summary>
public class JobRequestedConsumer(
    EaapDbContext db,
    IArgoClient argoClient,
    IOptions<AdapterOptions> adapterOptions,
    ILogger<JobRequestedConsumer> logger) : IConsumer<JobRequested>
{
    public async Task Consume(ConsumeContext<JobRequested> context)
    {
        var message = context.Message;
        var job = await db.AnalysisJobs
            .Include(j => j.AnalyzerRuns)
            .Include(j => j.Snapshot)
            .FirstOrDefaultAsync(j => j.Id == message.JobId, context.CancellationToken);
        if (job is null)
        {
            logger.LogWarning("Job {JobId} not found, ignoring JobRequested", message.JobId);
            return;
        }
        if (job.Status != JobStatus.Pending)
        {
            logger.LogInformation("Job {JobId} already {Status}, ignoring duplicate JobRequested", job.Id, job.Status);
            return;
        }

        try
        {
            var primaryRun = job.AnalyzerRuns.First();
            var adapter = ResolveAdapter(primaryRun.AnalyzerId);

            var workflowName = await argoClient.SubmitAnalysisWorkflowAsync(new ArgoSubmitRequest(
                job.Id,
                primaryRun.Id,
                primaryRun.AnalyzerId,
                adapter.Image,
                job.Snapshot!.StoragePath,
                job.Snapshot.CommitSha), context.CancellationToken);

            job.ArgoWorkflowName = workflowName;
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            primaryRun.Status = AnalyzerRunStatus.Running;
            primaryRun.StartedAt = job.StartedAt;

            // Phase 1 limitation (see docs/backlog.md): one workflow per job.
            foreach (var extraRun in job.AnalyzerRuns.Skip(1))
            {
                logger.LogWarning("Analyzer {AnalyzerId} skipped: Phase 1 runs a single analyzer per job", extraRun.AnalyzerId);
                extraRun.Status = AnalyzerRunStatus.Failed;
                extraRun.FinishedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(context.CancellationToken);
            await context.Publish(new JobStarted(job.Id, workflowName), context.CancellationToken);
            logger.LogInformation("Job {JobId} submitted as workflow {WorkflowName}", job.Id, workflowName);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to submit workflow for job {JobId}", job.Id);
            job.Status = JobStatus.Failed;
            job.FinishedAt = DateTimeOffset.UtcNow;
            foreach (var run in job.AnalyzerRuns)
            {
                run.Status = AnalyzerRunStatus.Failed;
                run.FinishedAt = job.FinishedAt;
            }
            await db.SaveChangesAsync(context.CancellationToken);
            await context.Publish(new JobFinished(job.Id, job.Status.ToString()), context.CancellationToken);
        }
    }

    private AdapterEntry ResolveAdapter(string analyzerId)
    {
        if (!adapterOptions.Value.Registry.TryGetValue(analyzerId, out var adapter) || string.IsNullOrEmpty(adapter.Image))
        {
            throw new InvalidOperationException($"No adapter registered for analyzer '{analyzerId}'.");
        }
        return adapter;
    }
}
