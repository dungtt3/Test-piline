namespace Eaap.Domain.Entities;

/// <summary>
/// First sighting of a warning fingerprint within a repository. This is the reference
/// point that makes a warning "new" or already-known across jobs (build spec phase 2 section 6).
/// </summary>
public class WarningBaseline
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Repository? Repository { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    public Guid FirstSeenJobId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }

    public BaselineStatus Status { get; set; } = BaselineStatus.Active;
    public DateTimeOffset? ResolvedAt { get; set; }
}
