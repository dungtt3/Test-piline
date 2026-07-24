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

    /// <summary>Security classification (phase 3); None for non-security findings.</summary>
    public SecuritySeverity SecuritySeverity { get; set; } = SecuritySeverity.None;

    /// <summary>CVE identifier if the finding names one, e.g. "CVE-2021-44228".</summary>
    public string? Cve { get; set; }

    /// <summary>CWE identifier if the finding names one, e.g. "CWE-79".</summary>
    public string? Cwe { get; set; }

    /// <summary>Denormalized at ingest: a matching, in-effect Suppression exists for this fingerprint.</summary>
    public bool IsSuppressed { get; set; }

    /// <summary>Estimated remediation minutes (phase 4); 0 for suppressed findings.</summary>
    public int DebtMinutes { get; set; }

    /// <summary>Original SARIF result object (jsonb) — source of truth.</summary>
    public string SarifRaw { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}
