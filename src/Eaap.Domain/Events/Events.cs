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

/// <summary>Published from ingestion when a job introduces new Critical security findings (phase 4).</summary>
public record NewCriticalSecurityFound(Guid JobId, int Count);

/// <summary>
/// One notification to deliver to one channel (phase 4 section 6). Fanned out per matching
/// channel so each delivery retries independently.
/// </summary>
public record NotificationDeliveryRequested(
    Guid ChannelId,
    string Event,
    Guid JobId,
    Guid RepositoryId,
    string RepositoryName,
    string Status,
    DateTimeOffset OccurredAt);
