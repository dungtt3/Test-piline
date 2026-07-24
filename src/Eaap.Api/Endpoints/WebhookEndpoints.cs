using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Eaap.Api.Endpoints;

public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        var hooks = app.MapGroup("/hooks").WithTags("Webhooks").AllowAnonymous();

        hooks.MapPost("/github", async (HttpRequest request, WebhookServices services, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request);
            var payload = TryParsePush(body);
            if (payload is null)
            {
                return Results.BadRequest(new { message = "Unrecognized push payload." });
            }

            var repository = await services.FindRepositoryAsync(payload.CloneUrl, ct);
            if (repository is null || string.IsNullOrEmpty(repository.WebhookSecret))
            {
                return Results.Unauthorized();
            }

            // GitHub signs the raw body: X-Hub-Signature-256: sha256=<hex>.
            var provided = request.Headers["X-Hub-Signature-256"].ToString();
            var expected = "sha256=" + HexHmac(repository.WebhookSecret, body);
            if (!FixedTimeEquals(provided, expected))
            {
                return Results.Unauthorized();
            }

            return await services.HandlePushAsync(repository, payload, ct);
        })
        .WithSummary("GitHub push webhook (auto-scan on default branch)")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        hooks.MapPost("/gitlab", async (HttpRequest request, WebhookServices services, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request);
            var payload = TryParsePush(body);
            if (payload is null)
            {
                return Results.BadRequest(new { message = "Unrecognized push payload." });
            }

            var repository = await services.FindRepositoryAsync(payload.CloneUrl, ct);
            if (repository is null || string.IsNullOrEmpty(repository.WebhookSecret))
            {
                return Results.Unauthorized();
            }

            // GitLab compares a shared token header.
            var token = request.Headers["X-Gitlab-Token"].ToString();
            if (!FixedTimeEquals(token, repository.WebhookSecret))
            {
                return Results.Unauthorized();
            }

            return await services.HandlePushAsync(repository, payload, ct);
        })
        .WithSummary("GitLab push webhook (auto-scan on default branch)")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }

    /// <summary>Minimal push shape shared by GitHub and GitLab: ref, after (commit), repo clone url.</summary>
    internal static PushPayload? TryParsePush(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ref", out var refElement) || !root.TryGetProperty("after", out var afterElement)
                || !root.TryGetProperty("repository", out var repo))
            {
                return null;
            }

            var cloneUrl = repo.TryGetProperty("clone_url", out var cu) ? cu.GetString()
                : repo.TryGetProperty("git_http_url", out var gu) ? gu.GetString()
                : repo.TryGetProperty("url", out var u) ? u.GetString() : null;

            return cloneUrl is null ? null : new PushPayload(
                refElement.GetString() ?? "", afterElement.GetString() ?? "", cloneUrl);
        }
        catch
        {
            return null;
        }
    }

    private static string HexHmac(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public record PushPayload(string Ref, string CommitSha, string CloneUrl)
    {
        public string? Branch => Ref.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? Ref["refs/heads/".Length..] : null;
    }
}

/// <summary>Grouped services for the webhook handlers, so the endpoints stay slim.</summary>
public class WebhookServices(
    EaapDbContext db,
    ISnapshotService snapshotService,
    IRepoConfigReader repoConfigReader,
    IPublishEndpoint publishEndpoint,
    IOptions<WebhookOptions> options)
{
    public Task<Repository?> FindRepositoryAsync(string cloneUrl, CancellationToken ct) =>
        db.Repositories.FirstOrDefaultAsync(r => r.CloneUrl == cloneUrl, ct);

    public async Task<IResult> HandlePushAsync(Repository repository, WebhookEndpoints.PushPayload payload, CancellationToken ct)
    {
        // Only pushes to the default branch trigger a scan.
        if (payload.Branch != repository.DefaultBranch)
        {
            return Results.Ok(new { message = $"Ignored push to '{payload.Branch}' (not the default branch)." });
        }

        // Idempotent by commit: skip if the same commit was scanned very recently.
        var since = DateTimeOffset.UtcNow.AddMinutes(-options.Value.IdempotencyWindowMinutes);
        var alreadyScanned = await db.AnalysisJobs.AnyAsync(j =>
            j.CreatedAt >= since &&
            db.Snapshots.Any(s => s.Id == j.SnapshotId && s.RepositoryId == repository.Id && s.CommitSha == payload.CommitSha),
            ct);
        if (alreadyScanned)
        {
            return Results.Ok(new { message = "A scan for this commit already exists; ignored." });
        }

        var snapshot = await snapshotService.GetOrCreateAsync(repository.Id, payload.Branch, payload.CommitSha, ct);

        var repoConfig = await repoConfigReader.ReadAsync(snapshot.StoragePath, ct);
        var analyzers = repoConfig.AnalyzerList.Count > 0
            ? repoConfig.AnalyzerList.ToArray()
            : options.Value.DefaultAnalyzers;

        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SnapshotId = snapshot.Id,
            Status = JobStatus.Pending,
            RequestedAnalyzers = [.. analyzers],
            CreatedAt = DateTimeOffset.UtcNow,
            AnalyzerRuns = [.. analyzers.Select(a => new AnalyzerRun
            {
                Id = Guid.NewGuid(),
                AnalyzerId = a,
                Status = AnalyzerRunStatus.Pending
            })]
        };
        db.AnalysisJobs.Add(job);
        await db.SaveChangesAsync(ct);

        await publishEndpoint.Publish(new JobRequested(job.Id, snapshot.Id, analyzers), ct);
        return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id });
    }
}
