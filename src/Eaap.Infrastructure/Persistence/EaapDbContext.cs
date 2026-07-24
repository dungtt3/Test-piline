using System.Text.Json;
using Eaap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Eaap.Infrastructure.Persistence;

public class EaapDbContext(DbContextOptions<EaapDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions NumericMapJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes metric/threshold maps ourselves instead of relying on the provider's native
    /// jsonb mapping: the column stays jsonb on Postgres, but the model also builds under the
    /// InMemory provider that the unit tests use.
    /// </summary>
    private static readonly ValueConverter<Dictionary<string, double>, string> NumericMapConverter = new(
        map => JsonSerializer.Serialize(map, NumericMapJsonOptions),
        json => JsonSerializer.Deserialize<Dictionary<string, double>>(json, NumericMapJsonOptions) ?? new());

    /// <summary>Order-independent comparison so change tracking sees real edits, not reordering.</summary>
    private static readonly ValueComparer<Dictionary<string, double>> NumericMapComparer = new(
        (left, right) => left != null && right != null
            ? left.Count == right.Count && !left.Except(right).Any()
            : left == right,
        map => map.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        map => new Dictionary<string, double>(map));

    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<AnalysisJob> AnalysisJobs => Set<AnalysisJob>();
    public DbSet<AnalyzerRun> AnalyzerRuns => Set<AnalyzerRun>();
    public DbSet<Warning> Warnings => Set<Warning>();
    public DbSet<GateEvaluation> GateEvaluations => Set<GateEvaluation>();
    public DbSet<MetricSet> MetricSets => Set<MetricSet>();
    public DbSet<WarningBaseline> WarningBaselines => Set<WarningBaseline>();
    public DbSet<GatePolicyBinding> GatePolicyBindings => Set<GatePolicyBinding>();
    public DbSet<TrendPoint> TrendPoints => Set<TrendPoint>();
    public DbSet<Suppression> Suppressions => Set<Suppression>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationDeliveryLog> NotificationDeliveryLogs => Set<NotificationDeliveryLog>();

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
            entity.Property(e => e.SecuritySeverity).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Cve).HasMaxLength(32);
            entity.Property(e => e.Cwe).HasMaxLength(16);
            entity.HasIndex(e => e.JobId);
            entity.HasIndex(e => e.Fingerprint);
            entity.HasIndex(e => e.SecuritySeverity);
            entity.HasOne(e => e.Job).WithMany().HasForeignKey(e => e.JobId);
            entity.HasOne(e => e.AnalyzerRun).WithMany().HasForeignKey(e => e.AnalyzerRunId);
        });

        modelBuilder.Entity<GateEvaluation>(entity =>
        {
            entity.Property(e => e.PolicyName).HasMaxLength(256);
            entity.Property(e => e.Violations).HasColumnType("jsonb");
        });

        modelBuilder.Entity<MetricSet>(entity =>
        {
            entity.Property(e => e.Metrics)
                .HasColumnType("jsonb")
                .HasConversion(NumericMapConverter, NumericMapComparer);
            entity.HasIndex(e => e.JobId);
            entity.HasOne(e => e.Job).WithMany().HasForeignKey(e => e.JobId);
            entity.HasOne(e => e.AnalyzerRun).WithMany().HasForeignKey(e => e.AnalyzerRunId);
        });

        modelBuilder.Entity<WarningBaseline>(entity =>
        {
            entity.Property(e => e.Fingerprint).HasMaxLength(64);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(e => e.Fingerprint);
            entity.HasIndex(e => new { e.RepositoryId, e.Fingerprint }).IsUnique();
            entity.HasOne(e => e.Repository).WithMany().HasForeignKey(e => e.RepositoryId);
        });

        modelBuilder.Entity<GatePolicyBinding>(entity =>
        {
            entity.Property(e => e.PolicyName).HasMaxLength(256);
            entity.Property(e => e.Thresholds)
                .HasColumnType("jsonb")
                .HasConversion(NumericMapConverter, NumericMapComparer);
            entity.HasIndex(e => e.RepositoryId).IsUnique();
            entity.HasOne(e => e.Repository).WithMany().HasForeignKey(e => e.RepositoryId);
        });

        modelBuilder.Entity<Suppression>(entity =>
        {
            entity.Property(e => e.Fingerprint).HasMaxLength(64);
            entity.Property(e => e.Reason).HasMaxLength(2048);
            entity.Property(e => e.CreatedBy).HasMaxLength(256);
            entity.HasIndex(e => e.Fingerprint);
            entity.HasIndex(e => new { e.RepositoryId, e.Fingerprint }).IsUnique();
            entity.HasOne(e => e.Repository).WithMany().HasForeignKey(e => e.RepositoryId);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.Property(e => e.PasswordHash).HasMaxLength(256);
            entity.Property(e => e.DisplayName).HasMaxLength(256);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasMany(e => e.Roles).WithOne(r => r.User).HasForeignKey(r => r.UserId);
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(e => new { e.UserId, e.Role }).IsUnique();
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.Property(e => e.TokenHash).HasMaxLength(64);
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<NotificationChannel>(entity =>
        {
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ConfigJson).HasColumnType("jsonb");
            entity.Property(e => e.Triggers).HasColumnType("jsonb");
            entity.HasIndex(e => e.RepositoryId);
            entity.HasOne(e => e.Repository).WithMany().HasForeignKey(e => e.RepositoryId);
        });

        modelBuilder.Entity<NotificationDeliveryLog>(entity =>
        {
            entity.Property(e => e.Event).HasMaxLength(64);
            entity.Property(e => e.Error).HasMaxLength(2048);
            entity.HasIndex(e => e.ChannelId);
            entity.HasOne(e => e.Channel).WithMany().HasForeignKey(e => e.ChannelId);
        });

        modelBuilder.Entity<TrendPoint>(entity =>
        {
            entity.Property(e => e.CommitSha).HasMaxLength(40);
            entity.HasIndex(e => new { e.RepositoryId, e.CreatedAt });
            entity.HasIndex(e => e.JobId).IsUnique();
            entity.HasOne(e => e.Repository).WithMany().HasForeignKey(e => e.RepositoryId);
            entity.HasOne(e => e.Job).WithMany().HasForeignKey(e => e.JobId);
        });
    }
}
