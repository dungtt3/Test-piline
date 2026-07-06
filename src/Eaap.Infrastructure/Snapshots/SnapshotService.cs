using System.Formats.Tar;
using System.IO.Compression;
using Eaap.Application;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Eaap.Infrastructure.Snapshots;

/// <summary>Creates source snapshots (gzipped tarballs on object storage) and reuses them per commit.</summary>
public class SnapshotService(
    EaapDbContext db,
    IGitClient gitClient,
    IObjectStorage storage,
    IPublishEndpoint publishEndpoint,
    ILogger<SnapshotService> logger) : ISnapshotService
{
    public async Task<Snapshot> GetOrCreateAsync(Guid repositoryId, string? branch, string? commitSha, CancellationToken ct = default)
    {
        var repository = await db.Repositories.FindAsync([repositoryId], ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found.");

        var effectiveBranch = string.IsNullOrWhiteSpace(branch) ? repository.DefaultBranch : branch;

        // When the commit is known upfront we can reuse an existing snapshot without cloning.
        if (!string.IsNullOrEmpty(commitSha))
        {
            var existing = await FindExistingAsync(repositoryId, commitSha, ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        var clone = await gitClient.CloneAsync(repository.CloneUrl, effectiveBranch, commitSha, ct);
        try
        {
            // The clone resolved the actual commit — check again before uploading.
            var existing = await FindExistingAsync(repositoryId, clone.CommitSha, ct);
            if (existing is not null)
            {
                return existing;
            }

            var storagePath = $"snapshots/{repositoryId}/{clone.CommitSha}.tar.gz";
            long sizeBytes;
            using (var tarball = CreateTarball(clone.LocalPath))
            {
                sizeBytes = tarball.Length;
                await storage.UploadAsync(storagePath, tarball, tarball.Length, "application/gzip", ct);
            }

            var snapshot = new Snapshot
            {
                Id = Guid.NewGuid(),
                RepositoryId = repositoryId,
                Branch = effectiveBranch,
                CommitSha = clone.CommitSha,
                StoragePath = storagePath,
                SizeBytes = sizeBytes,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Snapshots.Add(snapshot);
            await db.SaveChangesAsync(ct);

            await publishEndpoint.Publish(new SnapshotCreated(snapshot.Id, repositoryId, snapshot.CommitSha), ct);
            logger.LogInformation("Created snapshot {SnapshotId} for {RepositoryId}@{Sha} ({Size} bytes)",
                snapshot.Id, repositoryId, snapshot.CommitSha, sizeBytes);
            return snapshot;
        }
        finally
        {
            TryDeleteDirectory(clone.LocalPath);
        }
    }

    private Task<Snapshot?> FindExistingAsync(Guid repositoryId, string commitSha, CancellationToken ct) =>
        db.Snapshots.FirstOrDefaultAsync(s => s.RepositoryId == repositoryId && s.CommitSha == commitSha, ct);

    /// <summary>Packs the working tree (excluding .git) into an in-memory .tar.gz.</summary>
    private static MemoryStream CreateTarball(string sourceDirectory)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            var root = new DirectoryInfo(sourceDirectory);
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, file.FullName).Replace('\\', '/');
                if (relativePath.StartsWith(".git/", StringComparison.Ordinal))
                {
                    continue;
                }
                tar.WriteEntry(file.FullName, relativePath);
            }
        }
        output.Position = 0;
        return output;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            // Git object files are read-only on Windows; clear the flag before deleting.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort: leaked temp clones are harmless.
        }
    }
}
