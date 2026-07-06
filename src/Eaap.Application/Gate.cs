namespace Eaap.Application;

public record GateSummary(int ErrorCount, int WarningCount, IReadOnlyDictionary<string, int> ByRule);

public record GateResult(bool Passed, string PolicyName, string[] Violations);

/// <summary>Quality gate evaluation (OPA in Phase 1).</summary>
public interface IQualityGate
{
    Task<GateResult> EvaluateAsync(GateSummary summary, CancellationToken ct = default);
}
