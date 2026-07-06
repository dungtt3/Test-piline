namespace Eaap.Domain.Entities;

public class AnalyzerRun
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public AnalysisJob? Job { get; set; }

    /// <summary>Adapter id, e.g. "megalinter".</summary>
    public string AnalyzerId { get; set; } = string.Empty;

    public AnalyzerRunStatus Status { get; set; } = AnalyzerRunStatus.Pending;
    public string? SarifArtifactPath { get; set; }
    public string? RawArtifactPath { get; set; }
    public int WarningCount { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
