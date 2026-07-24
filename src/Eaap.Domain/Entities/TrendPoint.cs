namespace Eaap.Domain.Entities;

/// <summary>
/// Materialized once per finished job on the default branch. Denormalized on purpose so the
/// Grafana dashboard reads one small table instead of aggregating warnings on every refresh.
/// </summary>
public class TrendPoint
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Repository? Repository { get; set; }
    public Guid JobId { get; set; }
    public AnalysisJob? Job { get; set; }

    public string CommitSha { get; set; } = string.Empty;

    public int WarningTotal { get; set; }
    public int WarningNew { get; set; }
    public int WarningResolved { get; set; }

    /// <summary>Suppressed findings, tracked separately and excluded from WarningTotal (phase 3).</summary>
    public int WarningSuppressed { get; set; }

    public int ErrorCount { get; set; }

    public double? CoverageLine { get; set; }
    public int? TestsTotal { get; set; }
    public int? TestsFailed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
