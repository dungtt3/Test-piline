using Eaap.Application;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Domain.Events;
using Eaap.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Eaap.Api.Endpoints;

public static class RepositoryEndpoints
{
    public static RouteGroupBuilder MapRepositoryEndpoints(this RouteGroupBuilder group)
    {
        var repositories = group.MapGroup("/repositories").WithTags("Repositories");

        repositories.MapPost("/", async (CreateRepositoryRequest request, EaapDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.CloneUrl))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.CloneUrl)] = ["cloneUrl is required."]
                });
            }

            var repository = new Repository
            {
                Id = Guid.NewGuid(),
                Provider = request.Provider,
                CloneUrl = request.CloneUrl,
                DefaultBranch = string.IsNullOrWhiteSpace(request.DefaultBranch) ? "main" : request.DefaultBranch,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Repositories.Add(repository);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/repositories/{repository.Id}", repository);
        })
        .WithSummary("Register a repository")
        .Produces<Repository>(StatusCodes.Status201Created)
        .ProducesValidationProblem();

        repositories.MapGet("/", (EaapDbContext db, CancellationToken ct) =>
            db.Repositories.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync(ct))
        .WithSummary("List repositories");

        repositories.MapGet("/{id:guid}", async (Guid id, EaapDbContext db, CancellationToken ct) =>
        {
            var repository = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            return repository is null ? Results.NotFound() : Results.Ok(repository);
        })
        .WithSummary("Get a repository")
        .Produces<Repository>()
        .Produces(StatusCodes.Status404NotFound);

        repositories.MapPost("/{id:guid}/scans", async (
            Guid id,
            ScanRequest request,
            EaapDbContext db,
            ISnapshotService snapshotService,
            IPublishEndpoint publishEndpoint,
            CancellationToken ct) =>
        {
            var repository = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (repository is null)
            {
                return Results.NotFound();
            }

            if (request.Analyzers is not { Length: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Analyzers)] = ["At least one analyzer id is required."]
                });
            }

            var snapshot = await snapshotService.GetOrCreateAsync(id, request.Branch, request.CommitSha, ct);

            var job = new AnalysisJob
            {
                Id = Guid.NewGuid(),
                SnapshotId = snapshot.Id,
                Status = JobStatus.Pending,
                RequestedAnalyzers = [.. request.Analyzers],
                CreatedAt = DateTimeOffset.UtcNow,
                AnalyzerRuns = [.. request.Analyzers.Select(analyzerId => new AnalyzerRun
                {
                    Id = Guid.NewGuid(),
                    AnalyzerId = analyzerId,
                    Status = AnalyzerRunStatus.Pending
                })]
            };
            db.AnalysisJobs.Add(job);
            await db.SaveChangesAsync(ct);

            await publishEndpoint.Publish(new JobRequested(job.Id, snapshot.Id, request.Analyzers), ct);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", new ScanAccepted(job.Id));
        })
        .WithSummary("Request a scan for a repository")
        .Produces<ScanAccepted>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status404NotFound)
        .ProducesValidationProblem();

        return group;
    }
}
