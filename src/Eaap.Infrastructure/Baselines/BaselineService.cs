using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Eaap.Infrastructure.Baselines;

/// <summary>Outcome of processing a job's warnings against the repository baseline.</summary>
public record BaselineOutcome(int NewCount, int ResolvedCount);

/// <summary>
/// Cross-job dedup (build spec phase 2 section 6). A warning is "new" when its fingerprint had
/// no active baseline in the repository; a baseline is "resolved" when it stops appearing.
///
/// Two simplifications are applied on purpose (ADR-009):
///  - Baselines are only created/resolved for jobs on the repository's default branch. A feature
///    branch still gets IsNew computed against the default-branch baseline, but never mutates it.
///  - Resolution only happens when the job ran every analyzer ever seen on the default branch;
///    a job missing an analyzer resolves nothing, so a partial run cannot mass-resolve findings.
/// </summary>
public class BaselineService(EaapDbContext db, ILogger<BaselineService> logger)
{
    public async Task<BaselineOutcome> ProcessAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.AnalysisJobs
            .Include(j => j.AnalyzerRuns)
            .Include(j => j.Snapshot)!.ThenInclude(s => s!.Repository)
            .FirstAsync(j => j.Id == jobId, ct);

        var snapshot = job.Snapshot!;
        var repository = snapshot.Repository!;
        var isDefaultBranch = string.Equals(snapshot.Branch, repository.DefaultBranch, StringComparison.Ordinal);

        var warnings = await db.Warnings.Where(w => w.JobId == jobId).ToListAsync(ct);
        var jobFingerprints = warnings.Select(w => w.Fingerprint).ToHashSet(StringComparer.Ordinal);

        // Every baseline of the repo, not just active ones: a resolved fingerprint that reappears
        // must reactivate its existing row rather than insert a duplicate (unique repo+fingerprint).
        var baselines = await db.WarningBaselines
            .Where(b => b.RepositoryId == repository.Id)
            .ToListAsync(ct);
        var baselineByFingerprint = baselines.ToDictionary(b => b.Fingerprint, StringComparer.Ordinal);

        var newCount = MarkWarnings(warnings, baselineByFingerprint, repository.Id, jobId, isDefaultBranch);

        var resolvedCount = 0;
        if (isDefaultBranch)
        {
            resolvedCount = await ResolveMissingAsync(repository, jobId, job, jobFingerprints, baselines, ct);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Baseline for job {JobId}: {New} new, {Resolved} resolved (defaultBranch={Default})",
            jobId, newCount, resolvedCount, isDefaultBranch);
        return new BaselineOutcome(newCount, resolvedCount);
    }

    private int MarkWarnings(
        List<Warning> warnings,
        Dictionary<string, WarningBaseline> baselineByFingerprint,
        Guid repositoryId,
        Guid jobId,
        bool isDefaultBranch)
    {
        var newCount = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var warning in warnings)
        {
            if (baselineByFingerprint.TryGetValue(warning.Fingerprint, out var baseline))
            {
                if (baseline.Status == BaselineStatus.Active)
                {
                    warning.IsNew = false;
                    continue;
                }

                // Known before, resolved, now back: new again.
                warning.IsNew = true;
                newCount++;
                if (isDefaultBranch)
                {
                    baseline.Status = BaselineStatus.Active;
                    baseline.ResolvedAt = null;
                    baseline.FirstSeenJobId = jobId;
                    baseline.FirstSeenAt = now;
                }
                continue;
            }

            warning.IsNew = true;
            newCount++;
            if (isDefaultBranch)
            {
                var created = new WarningBaseline
                {
                    Id = Guid.NewGuid(),
                    RepositoryId = repositoryId,
                    Fingerprint = warning.Fingerprint,
                    FirstSeenJobId = jobId,
                    FirstSeenAt = now,
                    Status = BaselineStatus.Active
                };
                db.WarningBaselines.Add(created);
                baselineByFingerprint[warning.Fingerprint] = created;
            }
        }

        return newCount;
    }

    private async Task<int> ResolveMissingAsync(
        Repository repository,
        Guid jobId,
        AnalysisJob job,
        HashSet<string> jobFingerprints,
        List<WarningBaseline> baselines,
        CancellationToken ct)
    {
        // Analyzers this job actually completed vs. every analyzer ever run on the default branch.
        var jobAnalyzers = job.AnalyzerRuns
            .Where(r => r.Status == AnalyzerRunStatus.Succeeded)
            .Select(r => r.AnalyzerId)
            .ToHashSet(StringComparer.Ordinal);

        var everSeenAnalyzers = await (
            from run in db.AnalyzerRuns
            join j in db.AnalysisJobs on run.JobId equals j.Id
            join s in db.Snapshots on j.SnapshotId equals s.Id
            where s.RepositoryId == repository.Id
                && s.Branch == repository.DefaultBranch
                && run.Status == AnalyzerRunStatus.Succeeded
            select run.AnalyzerId).Distinct().ToListAsync(ct);

        if (!jobAnalyzers.IsSupersetOf(everSeenAnalyzers))
        {
            logger.LogInformation(
                "Job {JobId} ran {JobAnalyzers} but the repo has seen {EverSeen}; skipping baseline resolution",
                jobId, string.Join(",", jobAnalyzers), string.Join(",", everSeenAnalyzers));
            return 0;
        }

        var resolvedAt = DateTimeOffset.UtcNow;
        var resolvedCount = 0;
        foreach (var baseline in baselines.Where(b => b.Status == BaselineStatus.Active))
        {
            // FirstSeenJobId==jobId means we just (re)activated it in this same run — never resolve that.
            if (jobFingerprints.Contains(baseline.Fingerprint) || baseline.FirstSeenJobId == jobId)
            {
                continue;
            }

            baseline.Status = BaselineStatus.Resolved;
            baseline.ResolvedAt = resolvedAt;
            resolvedCount++;
        }

        return resolvedCount;
    }
}
