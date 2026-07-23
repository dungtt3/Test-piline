namespace Eaap.Domain.Entities;

public class Warning
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public AnalysisJob? Job { get; set; }
    public Guid AnalyzerRunId { get; set; }
    public AnalyzerRun? AnalyzerRun { get; set; }

    public string RuleId { get; set; } = string.Empty;
    public WarningLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }

    /// <summary>SHA256 dedup fingerprint, see build spec section 6.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>True when this fingerprint had no active baseline in the repository at ingest time.</summary>
    public bool IsNew { get; set; }

    /// <summary>Original SARIF result object (jsonb) — source of truth.</summary>
    public string SarifRaw { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}
