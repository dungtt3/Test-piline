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
    bool IsNew,
    string SecuritySeverity,
    string? Cve,
    string? Cwe,
    bool IsSuppressed);

public record SecuritySummaryResponse(
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyList<CountItem> ByCwe,
    IReadOnlyList<CountItem> ByCve);

public record CountItem(string Key, int Count);

public record LoginRequest(string Email, string Password);

public record LoginResponse(string Token, string TokenType, int ExpiresInHours);

public record CreateTokenRequest(string Name, DateTimeOffset? ExpiresAt);

public record CreateTokenResponse(Guid Id, string Token, string Name, DateTimeOffset? ExpiresAt);

public record ApiTokenDto(Guid Id, string Name, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public record CreateUserRequest(string Email, string Password, string DisplayName, string[] Roles);

public record UserDto(Guid Id, string Email, string DisplayName, string[] Roles, DateTimeOffset CreatedAt);

public record SetRoleRequest(string[] Roles);

public record CreateNotificationRequest(string Type, Dictionary<string, object>? Config, string[] Triggers, bool Enabled);

public record NotificationChannelDto(
    Guid Id, Guid? RepositoryId, string Type, string[] Triggers, bool Enabled, DateTimeOffset CreatedAt);

public record CreateSuppressionRequest(string Fingerprint, string Reason, DateTimeOffset? ExpiresAt);

public record SuppressionDto(
    Guid Id,
    string Fingerprint,
    string Reason,
    string CreatedBy,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

public record BaselineDto(
    Guid Id,
    string Fingerprint,
    Guid FirstSeenJobId,
    DateTimeOffset FirstSeenAt,
    string Status,
    DateTimeOffset? ResolvedAt);

public record GateBindingRequest(string? PolicyName, Dictionary<string, double> Thresholds);

public record GateBindingResponse(
    Guid RepositoryId,
    string? PolicyName,
    IReadOnlyDictionary<string, double> Thresholds,
    DateTimeOffset? UpdatedAt);

public record TrendPointDto(
    Guid JobId,
    string CommitSha,
    int WarningTotal,
    int WarningNew,
    int WarningResolved,
    int WarningSuppressed,
    int ErrorCount,
    double? CoverageLine,
    int? TestsTotal,
    int? TestsFailed,
    int DebtTotalMinutes,
    DateTimeOffset CreatedAt);

public record DebtResponse(int CurrentTotalMinutes, double CurrentTotalHours, IReadOnlyList<DebtPointDto> Trend);

public record DebtPointDto(Guid JobId, string CommitSha, int DebtTotalMinutes, DateTimeOffset CreatedAt);
