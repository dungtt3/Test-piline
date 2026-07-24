namespace Eaap.Domain.Entities;

public class Repository
{
    public Guid Id { get; set; }
    public GitProvider Provider { get; set; }
    public string CloneUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";

    /// <summary>Per-repository secret to verify Git provider webhook signatures (phase 4).</summary>
    public string? WebhookSecret { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
