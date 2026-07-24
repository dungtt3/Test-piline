namespace Eaap.Application;

public record GateSummary(
    int ErrorCount,
    int WarningCount,
    int NewWarningCount,
    IReadOnlyDictionary<string, int> ByRule,
    SecurityCounts Security);

/// <summary>Non-suppressed security findings of a job, grouped by severity.</summary>
public record SecurityCounts(int Critical, int High, int Medium, int Low)
{
    public static SecurityCounts Zero { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Quality gate thresholds. Sentinel values disable a rule so it does not fire by default:
/// a negative MaxNewWarnings means "unlimited" (a repo's first scan, where everything is new,
/// must not fail), and MinCoverageLine 0 means "no coverage floor". Security thresholds default
/// to 0 (strict): any critical or high finding fails the gate unless the repo relaxes it.
/// </summary>
public record GateThresholds(
    int MaxWarnings,
    int MaxNewWarnings,
    double MinCoverageLine,
    int MaxTestsFailed,
    int MaxSecurityCritical,
    int MaxSecurityHigh);

public record GateResult(bool Passed, string PolicyName, string[] Violations);

/// <summary>Quality gate evaluation (OPA).</summary>
public interface IQualityGate
{
    Task<GateResult> EvaluateAsync(
        GateSummary summary,
        IReadOnlyDictionary<string, double> metrics,
        GateThresholds thresholds,
        CancellationToken ct = default);
}
