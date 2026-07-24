using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Baselines;
using Eaap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Eaap.Infrastructure.Trends;

/// <summary>
/// Materializes one TrendPoint per finished default-branch job (build spec phase 2 section 9).
/// The table is denormalized so Grafana reads a small time series instead of re-aggregating
/// warnings on every dashboard refresh.
/// </summary>
public class TrendService(EaapDbContext db, ILogger<TrendService> logger)
{
    public async Task RecordAsync(Guid jobId, BaselineOutcome baseline, CancellationToken ct = default)
    {
        var job = await db.AnalysisJobs
            .Include(j => j.Snapshot)!.ThenInclude(s => s!.Repository)
            .FirstAsync(j => j.Id == jobId, ct);

        var snapshot = job.Snapshot!;
        var repository = snapshot.Repository!;
        if (!string.Equals(snapshot.Branch, repository.DefaultBranch, StringComparison.Ordinal))
        {
            return; // trend tracks the default branch only
        }

        // The close path guards on FinishedAt, but a retried message must not duplicate the point.
        if (await db.TrendPoints.AnyAsync(t => t.JobId == jobId, ct))
        {
            return;
        }

        var warningTotal = await db.Warnings.CountAsync(w => w.JobId == jobId, ct);
        var errorCount = await db.Warnings.CountAsync(w => w.JobId == jobId && w.Level == WarningLevel.Error, ct);

        var metrics = new Dictionary<string, double>();
        foreach (var set in await db.MetricSets.Where(m => m.JobId == jobId).ToListAsync(ct))
        {
            foreach (var (key, value) in set.Metrics)
            {
                metrics[key] = value;
            }
        }

        db.TrendPoints.Add(new TrendPoint
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            JobId = jobId,
            CommitSha = snapshot.CommitSha,
            WarningTotal = warningTotal,
            WarningNew = baseline.NewCount,
            WarningResolved = baseline.ResolvedCount,
            ErrorCount = errorCount,
            CoverageLine = metrics.TryGetValue("coverage.line", out var coverage) ? coverage : null,
            TestsTotal = metrics.TryGetValue("tests.total", out var total) ? (int)total : null,
            TestsFailed = metrics.TryGetValue("tests.failed", out var failed) ? (int)failed : null,
            CreatedAt = DateTimeOffset.UtcNow
        });

        logger.LogInformation("Recorded trend point for job {JobId} (warnings={Total}, new={New}, resolved={Resolved})",
            jobId, warningTotal, baseline.NewCount, baseline.ResolvedCount);
    }
}
