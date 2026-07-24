namespace Eaap.Application;

public record GateSummary(
    int ErrorCount,
    int WarningCount,
    int NewWarningCount,
    IReadOnlyDictionary<string, int> ByRule);

/// <summary>
/// Quality gate thresholds. Sentinel values disable a rule so it does not fire by default:
/// a negative MaxNewWarnings means "unlimited" (a repo's first scan, where everything is new,
/// must not fail), and MinCoverageLine 0 means "no coverage floor".
/// </summary>
public record GateThresholds(
    int MaxWarnings,
    int MaxNewWarnings,
    double MinCoverageLine,
    int MaxTestsFailed);

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
