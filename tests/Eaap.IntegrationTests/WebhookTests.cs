using System.Net;
using System.Security.Cryptography;
using System.Text;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eaap.IntegrationTests;

[Collection("eaap")]
public class WebhookTests(EaapApiFactory factory)
{
    private const string Secret = "webhook-shared-secret";

    [Fact]
    public async Task GitHubPush_ValidSignature_CreatesScan_AndIsIdempotent()
    {
        var (repoId, cloneUrl, commit) = await SeedRepositoryWithSnapshotAsync();
        var body = GitHubPushBody(cloneUrl, "refs/heads/main", commit);
        var client = factory.CreateClient();

        var first = await PostGitHubAsync(client, body, Sign(Secret, body));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(1, await JobCountAsync(repoId, commit));

        // Same commit again within the window -> ignored, still one job.
        var second = await PostGitHubAsync(client, body, Sign(Secret, body));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await JobCountAsync(repoId, commit));
    }

    [Fact]
    public async Task GitHubPush_WrongSignature_Is401_AndCreatesNoJob()
    {
        var (repoId, cloneUrl, commit) = await SeedRepositoryWithSnapshotAsync();
        var body = GitHubPushBody(cloneUrl, "refs/heads/main", commit);
        var client = factory.CreateClient();

        var response = await PostGitHubAsync(client, body, Sign("the-wrong-secret", body));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await JobCountAsync(repoId, commit));
    }

    [Fact]
    public async Task GitHubPush_NonDefaultBranch_IsIgnored()
    {
        var (repoId, cloneUrl, commit) = await SeedRepositoryWithSnapshotAsync();
        var body = GitHubPushBody(cloneUrl, "refs/heads/feature-x", commit);
        var client = factory.CreateClient();

        var response = await PostGitHubAsync(client, body, Sign(Secret, body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await JobCountAsync(repoId, commit));
    }

    [Fact]
    public async Task UnknownRepository_Is401()
    {
        var body = GitHubPushBody("https://github.com/acme/unknown.git", "refs/heads/main", new string('a', 40));
        var client = factory.CreateClient();

        var response = await PostGitHubAsync(client, body, Sign(Secret, body));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostGitHubAsync(HttpClient client, string body, string signature)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Hub-Signature-256", signature);
        return await client.PostAsync("/hooks/github", content);
    }

    private static string GitHubPushBody(string cloneUrl, string reference, string commit) =>
        "{\"ref\":\"" + reference + "\",\"after\":\"" + commit
        + "\",\"repository\":{\"clone_url\":\"" + cloneUrl + "\",\"full_name\":\"acme/widgets\"}}";

    private static string Sign(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private async Task<int> JobCountAsync(Guid repositoryId, string commit)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();
        return await db.AnalysisJobs.CountAsync(j =>
            db.Snapshots.Any(s => s.Id == j.SnapshotId && s.RepositoryId == repositoryId && s.CommitSha == commit));
    }

    /// <summary>Seeds a repo with a webhook secret and a snapshot for the commit, so the handler
    /// reuses it instead of cloning an unreachable URL.</summary>
    private async Task<(Guid RepositoryId, string CloneUrl, string Commit)> SeedRepositoryWithSnapshotAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EaapDbContext>();

        var cloneUrl = $"https://github.com/acme/repo-{Guid.NewGuid():N}.git";
        var commit = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Provider = GitProvider.GitHub,
            CloneUrl = cloneUrl,
            DefaultBranch = "main",
            WebhookSecret = Secret,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            Branch = "main",
            CommitSha = commit,
            StoragePath = $"snapshots/{repository.Id}/{commit}.tar.gz",
            SizeBytes = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(repository, snapshot);
        await db.SaveChangesAsync();
        return (repository.Id, cloneUrl, commit);
    }
}
