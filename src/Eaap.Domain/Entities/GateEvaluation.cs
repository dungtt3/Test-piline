namespace Eaap.Domain.Entities;

public class GateEvaluation
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public AnalysisJob? Job { get; set; }
    public string PolicyName { get; set; } = string.Empty;
    public bool Passed { get; set; }

    /// <summary>Violation messages returned by OPA (jsonb array).</summary>
    public string Violations { get; set; } = "[]";

    public DateTimeOffset EvaluatedAt { get; set; }
}
