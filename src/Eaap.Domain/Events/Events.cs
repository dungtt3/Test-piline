namespace Eaap.Domain.Events;

public record SnapshotCreated(Guid SnapshotId, Guid RepositoryId, string CommitSha);

public record JobRequested(Guid JobId, Guid SnapshotId, string[] Analyzers);

public record JobStarted(Guid JobId, string ArgoWorkflowName);

/// <summary>
/// MetricsArtifactPath is optional: adapters emit /results/metrics.json only when they
/// measure something (manifest field emitsMetrics), so phase 1 adapters keep working unchanged.
/// </summary>
public record AnalyzerRunFinished(
    Guid AnalyzerRunId,
    Guid JobId,
    string Status,
    string? SarifArtifactPath,
    string? MetricsArtifactPath = null);

public record JobFinished(Guid JobId, string Status);

public record GateEvaluated(Guid JobId, bool Passed, string PolicyName);
