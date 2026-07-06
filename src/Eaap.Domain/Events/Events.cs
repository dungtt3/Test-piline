namespace Eaap.Domain.Events;

public record SnapshotCreated(Guid SnapshotId, Guid RepositoryId, string CommitSha);

public record JobRequested(Guid JobId, Guid SnapshotId, string[] Analyzers);

public record JobStarted(Guid JobId, string ArgoWorkflowName);

public record AnalyzerRunFinished(Guid AnalyzerRunId, Guid JobId, string Status, string? SarifArtifactPath);

public record JobFinished(Guid JobId, string Status);

public record GateEvaluated(Guid JobId, bool Passed, string PolicyName);
