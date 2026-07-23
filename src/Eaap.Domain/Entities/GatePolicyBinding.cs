namespace Eaap.Domain.Entities;

/// <summary>
/// Per-repository quality gate override. A repository without a binding falls back to
/// the platform-wide thresholds from configuration (phase 1 behaviour).
/// </summary>
public class GatePolicyBinding
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Repository? Repository { get; set; }

    /// <summary>Rego policy to evaluate; null keeps the platform default.</summary>
    public string? PolicyName { get; set; }

    /// <summary>Threshold overrides (jsonb), e.g. { "minCoverageLine": 90, "maxNewWarnings": 0 }.</summary>
    public Dictionary<string, double> Thresholds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
