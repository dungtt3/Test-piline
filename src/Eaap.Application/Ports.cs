namespace Eaap.Application;

/// <summary>Object storage abstraction (MinIO via S3 API).</summary>
public interface IObjectStorage
{
    Task UploadAsync(string key, Stream content, long length, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

/// <summary>Result of cloning a repository at a specific ref.</summary>
public record GitCloneResult(string LocalPath, string CommitSha);

/// <summary>Git operations backed by the git CLI.</summary>
public interface IGitClient
{
    /// <summary>Clones <paramref name="cloneUrl"/> at <paramref name="branch"/> (and optional commit) into a temp directory.</summary>
    Task<GitCloneResult> CloneAsync(string cloneUrl, string branch, string? commitSha, CancellationToken ct = default);
}

/// <summary>Creates or reuses source snapshots stored as tarballs on object storage.</summary>
public interface ISnapshotService
{
    Task<Domain.Entities.Snapshot> GetOrCreateAsync(Guid repositoryId, string? branch, string? commitSha, CancellationToken ct = default);
}
