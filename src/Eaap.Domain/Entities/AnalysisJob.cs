namespace Eaap.Domain.Entities;

public class AnalysisJob
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public Snapshot? Snapshot { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? ArgoWorkflowName { get; set; }

    /// <summary>Analyzer ids requested for this job (stored as jsonb).</summary>
    public List<string> RequestedAnalyzers { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public List<AnalyzerRun> AnalyzerRuns { get; set; } = [];
    public List<GateEvaluation> GateEvaluations { get; set; } = [];
}
