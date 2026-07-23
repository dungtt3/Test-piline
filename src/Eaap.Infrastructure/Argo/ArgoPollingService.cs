using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Eaap.Infrastructure.Argo;

/// <summary>
/// Polls Argo every few seconds for running jobs and publishes AnalyzerRunFinished
/// when their workflow completes (polling chosen over webhooks, see ADR-004).
/// </summary>
public class ArgoPollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<ArgoOptions> options,
    ILogger<ArgoPollingService> logger) : BackgroundService
{
    /// <summary>Runs already reported this process lifetime; the consumer is idempotent as a second guard.</summary>
    private readonly HashSet<Guid> _reportedRuns = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.PollIntervalSeconds, 1));
        using var timer = new PeriodicTimer(interval);
        while (await WaitForNextTickSafeAsync(timer, stoppingToken))
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Argo polling iteration failed");
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();

        var runningJobs = await db.AnalysisJobs
            .AsNoTracking()
            .Include(j => j.AnalyzerRuns)
            .Where(j => j.Status == JobStatus.Running && j.ArgoWorkflowName != null)
            .ToListAsync(ct);
        if (runningJobs.Count == 0)
        {
            return;
        }

        var argoClient = scope.ServiceProvider.GetRequiredService<IArgoClient>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        foreach (var job in runningJobs)
        {
            // One broken job must not starve the rest of the poll loop.
            ArgoWorkflowStatus status;
            try
            {
                status = await argoClient.GetWorkflowStatusAsync(job.ArgoWorkflowName!, ct);
            }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Workflow deleted from the cluster: close the job as failed.
                logger.LogWarning("Workflow {Workflow} of job {JobId} no longer exists, failing the job",
                    job.ArgoWorkflowName, job.Id);
                status = new ArgoWorkflowStatus("Failed");
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to poll workflow {Workflow} for job {JobId}", job.ArgoWorkflowName, job.Id);
                continue;
            }

            if (!status.IsFinished)
            {
                continue;
            }

            foreach (var run in job.AnalyzerRuns.Where(r => r.Status is AnalyzerRunStatus.Running or AnalyzerRunStatus.Pending))
            {
                if (!_reportedRuns.Add(run.Id))
                {
                    continue;
                }

                // upload-results copies /work/results recursively, so both artifacts land under this prefix.
                var resultsPrefix = $"jobs/{job.Id}/{run.Id}";
                var runStatus = status.IsSucceeded ? "Succeeded" : "Failed";
                await publishEndpoint.Publish(
                    new AnalyzerRunFinished(
                        run.Id,
                        job.Id,
                        runStatus,
                        status.IsSucceeded ? $"{resultsPrefix}/{run.AnalyzerId}.sarif" : null,
                        status.IsSucceeded ? $"{resultsPrefix}/metrics.json" : null), ct);
                logger.LogInformation("Workflow {Workflow} finished ({Phase}); reported run {RunId}",
                    job.ArgoWorkflowName, status.Phase, run.Id);
            }
        }
    }

    private static async ValueTask<bool> WaitForNextTickSafeAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
