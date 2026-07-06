using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Persistence;
using Eaap.Infrastructure.Snapshots;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eaap.UnitTests;

public class SnapshotServiceTests
{
    private static EaapDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<EaapDbContext>()
            .UseInMemoryDatabase("snapshots-" + Guid.NewGuid())
            .Options);

    [Fact]
    public async Task GetOrCreate_WithKnownCommit_ReusesSnapshotWithoutCloning()
    {
        await using var db = CreateDb();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GenericGit,
            CloneUrl = "https://example.invalid/repo.git",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var existing = new Snapshot
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            Branch = "main",
            CommitSha = new string('a', 40),
            StoragePath = "snapshots/x.tar.gz",
            SizeBytes = 123,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Repositories.Add(repository);
        db.Snapshots.Add(existing);
        await db.SaveChangesAsync();

        var gitClient = Substitute.For<IGitClient>();
        gitClient.CloneAsync(default!, default!, default, default)
            .ReturnsForAnyArgs<GitCloneResult>(_ => throw new InvalidOperationException("must not clone"));

        var service = new SnapshotService(
            db,
            gitClient,
            Substitute.For<IObjectStorage>(),
            Substitute.For<IPublishEndpoint>(),
            NullLogger<SnapshotService>.Instance);

        var snapshot = await service.GetOrCreateAsync(repository.Id, null, existing.CommitSha);

        Assert.Equal(existing.Id, snapshot.Id);
        await gitClient.DidNotReceiveWithAnyArgs().CloneAsync(default!, default!, default, default);
    }
}
