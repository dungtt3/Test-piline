using Eaap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Eaap.Infrastructure.Persistence;

public class EaapDbContext(DbContextOptions<EaapDbContext> options) : DbContext(options)
{
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<AnalysisJob> AnalysisJobs => Set<AnalysisJob>();
    public DbSet<AnalyzerRun> AnalyzerRuns => Set<AnalyzerRun>();
    public DbSet<Warning> Warnings => Set<Warning>();
    public DbSet<GateEvaluation> GateEvaluations => Set<GateEvaluation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Repository>(entity =>
        {
            entity.Property(e => e.Provider).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.CloneUrl).HasMaxLength(2048);
            entity.Property(e => e.DefaultBranch).HasMaxLength(256);
        });

        modelBuilder.Entity<Snapshot>(entity =>
        {
            entity.Property(e => e.CommitSha).HasMaxLength(40);
            entity.Property(e => e.Branch).HasMaxLength(256);
            entity.Property(e => e.StoragePath).HasMaxLength(1024);
            entity.HasIndex(e => new { e.RepositoryId, e.CommitSha }).IsUnique();
            entity.HasOne(e => e.Repository).WithMany().HasForeignKey(e => e.RepositoryId);
        });

        modelBuilder.Entity<AnalysisJob>(entity =>
        {
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.ArgoWorkflowName).HasMaxLength(256);
            entity.Property(e => e.RequestedAnalyzers).HasColumnType("jsonb");
            entity.HasIndex(e => e.SnapshotId);
            entity.HasOne(e => e.Snapshot).WithMany().HasForeignKey(e => e.SnapshotId);
            entity.HasMany(e => e.AnalyzerRuns).WithOne(r => r.Job).HasForeignKey(r => r.JobId);
            entity.HasMany(e => e.GateEvaluations).WithOne(g => g.Job).HasForeignKey(g => g.JobId);
        });

        modelBuilder.Entity<AnalyzerRun>(entity =>
        {
            entity.Property(e => e.AnalyzerId).HasMaxLength(128);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.SarifArtifactPath).HasMaxLength(1024);
            entity.Property(e => e.RawArtifactPath).HasMaxLength(1024);
        });

        modelBuilder.Entity<Warning>(entity =>
        {
            entity.Property(e => e.RuleId).HasMaxLength(512);
            entity.Property(e => e.Level).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Fingerprint).HasMaxLength(64);
            entity.Property(e => e.SarifRaw).HasColumnType("jsonb");
            entity.HasIndex(e => e.JobId);
            entity.HasIndex(e => e.Fingerprint);
            entity.HasOne(e => e.Job).WithMany().HasForeignKey(e => e.JobId);
            entity.HasOne(e => e.AnalyzerRun).WithMany().HasForeignKey(e => e.AnalyzerRunId);
        });

        modelBuilder.Entity<GateEvaluation>(entity =>
        {
            entity.Property(e => e.PolicyName).HasMaxLength(256);
            entity.Property(e => e.Violations).HasColumnType("jsonb");
        });
    }
}
