using Eaap.Domain;

namespace Eaap.Api;

public record CreateRepositoryRequest(GitProvider Provider, string CloneUrl, string? DefaultBranch);

public record ScanRequest(string? Branch, string? CommitSha, string[] Analyzers);

public record ScanAccepted(Guid JobId);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public record AnalyzerRunDto(
    Guid Id,
    string AnalyzerId,
    string Status,
    string? SarifArtifactPath,
    int WarningCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public record GateEvaluationDto(string PolicyName, bool Passed, string[] Violations, DateTimeOffset EvaluatedAt);

public record JobResponse(
    Guid Id,
    Guid SnapshotId,
    string Status,
    string? ArgoWorkflowName,
    List<string> RequestedAnalyzers,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    List<AnalyzerRunDto> AnalyzerRuns,
    GateEvaluationDto? GateEvaluation);

public record WarningDto(
    Guid Id,
    Guid AnalyzerRunId,
    string RuleId,
    string Level,
    string Message,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string Fingerprint,
    bool IsNew);

public record BaselineDto(
    Guid Id,
    string Fingerprint,
    Guid FirstSeenJobId,
    DateTimeOffset FirstSeenAt,
    string Status,
    DateTimeOffset? ResolvedAt);
