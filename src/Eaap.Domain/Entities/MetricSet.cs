namespace Eaap.Domain.Entities;

/// <summary>
/// Numeric measurements emitted by an analyzer run via /results/metrics.json.
/// Metrics are deliberately kept out of SARIF: warnings are findings, metrics are numbers.
/// </summary>
public class MetricSet
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public AnalysisJob? Job { get; set; }
    public Guid AnalyzerRunId { get; set; }
    public AnalyzerRun? AnalyzerRun { get; set; }

    /// <summary>Dot-separated keys to values (jsonb), e.g. { "coverage.line": 82.5, "tests.total": 240 }.</summary>
    public Dictionary<string, double> Metrics { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}
