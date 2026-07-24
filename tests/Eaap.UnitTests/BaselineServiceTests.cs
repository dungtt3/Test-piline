using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Baselines;
using Eaap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eaap.UnitTests;

public class BaselineServiceTests
{
    private static EaapDbContext NewDb() =>
        new(new DbContextOptionsBuilder<EaapDbContext>()
            .UseInMemoryDatabase("baseline-" + Guid.NewGuid())
            .Options);

    [Fact]
    public async Task FeatureBranchJob_MarksIsNewButDoesNotTouchBaselines()
    {
        using var db = NewDb();
        var repositoryId = await SeedRepositoryAsync(db, defaultBranch: "main");
        // An active baseline established earlier on the default branch.
        db.WarningBaselines.Add(new WarningBaseline
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            Fingerprint = "known",
            FirstSeenJobId = Guid.NewGuid(),
            FirstSeenAt = DateTimeOffset.UtcNow,
            Status = BaselineStatus.Active
        });
        await db.SaveChangesAsync();

        var (jobId, _) = await SeedJobAsync(db, repositoryId, branch: "feature/x",
            ("known", false), ("brandnew", false));

        var outcome = await new BaselineService(db, NullLogger<BaselineService>.Instance).ProcessAsync(jobId);

        // "brandnew" is new relative to the default-branch baseline, "known" is not.
        Assert.Equal(1, outcome.NewCount);
        Assert.Equal(0, outcome.ResolvedCount);
        // No new baseline row was created from the feature branch.
        Assert.Equal(1, await db.WarningBaselines.CountAsync(b => b.RepositoryId == repositoryId));

        var warnings = await db.Warnings.Where(w => w.JobId == jobId).ToListAsync();
        Assert.True(warnings.Single(w => w.RuleId == "brandnew").IsNew);
        Assert.False(warnings.Single(w => w.RuleId == "known").IsNew);
    }

    [Fact]
    public async Task ResolvedFingerprintReappearing_ReactivatesInsteadOfDuplicating()
    {
        using var db = NewDb();
        var repositoryId = await SeedRepositoryAsync(db, defaultBranch: "main");
        db.WarningBaselines.Add(new WarningBaseline
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            Fingerprint = "flaky",
            FirstSeenJobId = Guid.NewGuid(),
            FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-2),
            Status = BaselineStatus.Resolved,
            ResolvedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var (jobId, _) = await SeedJobAsync(db, repositoryId, branch: "main", ("flaky", false));

        var outcome = await new BaselineService(db, NullLogger<BaselineService>.Instance).ProcessAsync(jobId);

        // Reappearing after resolution counts as new again, and reuses the single baseline row.
        Assert.Equal(1, outcome.NewCount);
        var baseline = Assert.Single(await db.WarningBaselines.Where(b => b.RepositoryId == repositoryId).ToListAsync());
        Assert.Equal(BaselineStatus.Active, baseline.Status);
        Assert.Null(baseline.ResolvedAt);
    }

    [Fact]
    public async Task ResolutionSkipped_WhenJobRanFewerAnalyzersThanEverSeen()
    {
        using var db = NewDb();
        var repositoryId = await SeedRepositoryAsync(db, defaultBranch: "main");

        // Prior job on default branch ran "semgrep" and left an active baseline.
        var priorJobId = Guid.NewGuid();
        var priorSnapshot = new Snapshot
        {
            Id = Guid.NewGuid(), RepositoryId = repositoryId, Branch = "main",
            CommitSha = new string('a', 40), StoragePath = "s/a.tar.gz", SizeBytes = 1, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Snapshots.Add(priorSnapshot);
        db.AnalysisJobs.Add(new AnalysisJob
        {
            Id = priorJobId, SnapshotId = priorSnapshot.Id, Status = JobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            AnalyzerRuns = [new AnalyzerRun { Id = Guid.NewGuid(), AnalyzerId = "semgrep", Status = AnalyzerRunStatus.Succeeded }]
        });
        db.WarningBaselines.Add(new WarningBaseline
        {
            Id = Guid.NewGuid(), RepositoryId = repositoryId, Fingerprint = "semgrep-finding",
            FirstSeenJobId = priorJobId, FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-1), Status = BaselineStatus.Active
        });
        await db.SaveChangesAsync();

        // New job runs only "megalinter" — it must not resolve the semgrep finding it never looked for.
        var (jobId, _) = await SeedJobAsync(db, repositoryId, branch: "main",
            analyzerId: "megalinter", findings: ("megalinter-finding", false));

        var outcome = await new BaselineService(db, NullLogger<BaselineService>.Instance).ProcessAsync(jobId);

        Assert.Equal(0, outcome.ResolvedCount);
        Assert.Equal(2, await db.WarningBaselines.CountAsync(b => b.Status == BaselineStatus.Active));
    }

    private static async Task<Guid> SeedRepositoryAsync(EaapDbContext db, string defaultBranch)
    {
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/r.git",
            DefaultBranch = defaultBranch,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return repository.Id;
    }

    private static async Task<(Guid JobId, Guid RunId)> SeedJobAsync(
        EaapDbContext db,
        Guid repositoryId,
        string branch,
        params (string RuleId, bool IsNew)[] findings) =>
        await SeedJobAsync(db, repositoryId, branch, "megalinter", findings);

    private static async Task<(Guid JobId, Guid RunId)> SeedJobAsync(
        EaapDbContext db,
        Guid repositoryId,
        string branch,
        string analyzerId,
        params (string RuleId, bool IsNew)[] findings)
    {
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            Branch = branch,
            CommitSha = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..40],
            StoragePath = "s/x.tar.gz",
            SizeBytes = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var run = new AnalyzerRun
        {
            Id = Guid.NewGuid(),
            AnalyzerId = analyzerId,
            Status = AnalyzerRunStatus.Succeeded,
            FinishedAt = DateTimeOffset.UtcNow
        };
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SnapshotId = snapshot.Id,
            Status = JobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            AnalyzerRuns = [run]
        };
        db.AddRange(snapshot, job);

        foreach (var (ruleId, isNew) in findings)
        {
            db.Warnings.Add(new Warning
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                AnalyzerRunId = run.Id,
                RuleId = ruleId,
                Level = WarningLevel.Warning,
                Message = ruleId,
                Fingerprint = ruleId, // deterministic fingerprint per rule for the test
                IsNew = isNew,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return (job.Id, run.Id);
    }
}
