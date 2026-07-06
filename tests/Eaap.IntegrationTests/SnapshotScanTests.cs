using System.Net;
using System.Net.Http.Json;
using Eaap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class SnapshotScanTests(EaapApiFactory factory)
{
    private record ScanAccepted(Guid JobId);

    [Fact]
    public async Task Scan_ClonesRepository_CreatesSnapshot_AndReusesItForSameCommit()
    {
        var fixtureRepo = GitFixture.CreateRepositoryWithFiles(new Dictionary<string, string>
        {
            ["src/index.js"] = "var x = 1\n",
            ["README.md"] = "# fixture\n"
        });
        var headSha = GitFixture.GetHeadSha(fixtureRepo);
        var client = factory.CreateClient();

        // Register the repository.
        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", new
        {
            provider = "GenericGit",
            cloneUrl = fixtureRepo.Replace('\\', '/'),
            defaultBranch = "main"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var repositoryId = (await createResponse.Content.ReadFromJsonAsync<RepositoryDto>())!.Id;

        // First scan clones and snapshots.
        var scan1 = await client.PostAsJsonAsync($"/api/v1/repositories/{repositoryId}/scans", new
        {
            analyzers = new[] { "megalinter" }
        });
        Assert.Equal(HttpStatusCode.Accepted, scan1.StatusCode);
        var job1 = await scan1.Content.ReadFromJsonAsync<ScanAccepted>();
        Assert.NotNull(job1);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            var snapshot = Assert.Single(await db.Snapshots.Where(s => s.RepositoryId == repositoryId).ToListAsync());
            Assert.Equal(headSha, snapshot.CommitSha);
            Assert.True(snapshot.SizeBytes > 0);

            // Tarball exists on MinIO.
            using var s3 = factory.CreateS3Client();
            var metadata = await s3.GetObjectMetadataAsync(EaapApiFactory.Bucket, snapshot.StoragePath);
            Assert.Equal(snapshot.SizeBytes, metadata.ContentLength);
        }

        // Second scan for the same commit reuses the snapshot.
        var scan2 = await client.PostAsJsonAsync($"/api/v1/repositories/{repositoryId}/scans", new
        {
            commitSha = headSha,
            analyzers = new[] { "megalinter" }
        });
        Assert.Equal(HttpStatusCode.Accepted, scan2.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
            Assert.Equal(1, await db.Snapshots.CountAsync(s => s.RepositoryId == repositoryId));
            Assert.Equal(2, await db.AnalysisJobs.CountAsync(j => j.Snapshot!.RepositoryId == repositoryId));
        }
    }

    private record RepositoryDto(Guid Id);
}
