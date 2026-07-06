namespace Eaap.Domain.Entities;

public class Snapshot
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Repository? Repository { get; set; }
    public string Branch { get; set; } = string.Empty;

    /// <summary>Full 40-hex commit SHA the snapshot was taken at.</summary>
    public string CommitSha { get; set; } = string.Empty;

    /// <summary>Object key of the source tarball on MinIO.</summary>
    public string StoragePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
