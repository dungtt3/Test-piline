namespace Eaap.Domain.Entities;

/// <summary>
/// A human decision to stop a finding (by fingerprint) from counting against a repository's
/// gate and trend (build spec phase 3 section 5). The warning is still stored and returned;
/// it is only excluded from the gate summary and the trend total.
/// </summary>
public class Suppression
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Repository? Repository { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Why the finding is suppressed; required, at least 10 characters.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Free text until Phase 4 introduces authentication.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Null means the suppression never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>In effect when it has no expiry or the expiry is still in the future.</summary>
    public bool IsInEffect(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}
