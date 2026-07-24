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

        repositories.MapGet("/{id:guid}/baseline", async (
            Guid id,
            EaapDbContext db,
            CancellationToken ct,
            string? status,
            int page = 1,
            int pageSize = 50) =>
        {
            if (!await db.Repositories.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }

            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = db.WarningBaselines.AsNoTracking().Where(b => b.RepositoryId == id);
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<BaselineStatus>(status, ignoreCase: true, out var parsedStatus))
            {
                query = query.Where(b => b.Status == parsedStatus);
            }

            var totalCount = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(b => b.FirstSeenAt).ThenBy(b => b.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(b => new BaselineDto(
                    b.Id, b.Fingerprint, b.FirstSeenJobId, b.FirstSeenAt, b.Status.ToString(), b.ResolvedAt))
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<BaselineDto>(items, page, pageSize, totalCount));
        })
        .WithSummary("List a repository's warning baseline (paged, filterable by status)")
        .Produces<PagedResult<BaselineDto>>()
        .Produces(StatusCodes.Status404NotFound);

        repositories.MapGet("/{id:guid}/gate", async (Guid id, EaapDbContext db, CancellationToken ct) =>
        {
            if (!await db.Repositories.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }

            var binding = await db.GatePolicyBindings.AsNoTracking().FirstOrDefaultAsync(b => b.RepositoryId == id, ct);
            return binding is null
                ? Results.Ok(new GateBindingResponse(id, null, new Dictionary<string, double>(), null))
                : Results.Ok(new GateBindingResponse(id, binding.PolicyName, binding.Thresholds, binding.UpdatedAt));
        })
        .WithSummary("Get the per-repository quality gate binding (empty when none is set)")
        .Produces<GateBindingResponse>()
        .Produces(StatusCodes.Status404NotFound);

        repositories.MapPut("/{id:guid}/gate", async (
            Guid id, GateBindingRequest request, EaapDbContext db, CancellationToken ct) =>
        {
            if (!await db.Repositories.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }

            var thresholds = request.Thresholds ?? [];
            var now = DateTimeOffset.UtcNow;
            var binding = await db.GatePolicyBindings.FirstOrDefaultAsync(b => b.RepositoryId == id, ct);
            if (binding is null)
            {
                binding = new GatePolicyBinding
                {
                    Id = Guid.NewGuid(),
                    RepositoryId = id,
                    PolicyName = request.PolicyName,
                    Thresholds = thresholds,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.GatePolicyBindings.Add(binding);
            }
            else
            {
                binding.PolicyName = request.PolicyName;
                binding.Thresholds = thresholds;
                binding.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(new GateBindingResponse(id, binding.PolicyName, binding.Thresholds, binding.UpdatedAt));
        })
        .WithSummary("Create or replace the per-repository quality gate binding")
        .Produces<GateBindingResponse>()
        .Produces(StatusCodes.Status404NotFound);

        repositories.MapGet("/{id:guid}/trend", async (
            Guid id,
            EaapDbContext db,
            CancellationToken ct,
            DateTimeOffset? from,
            DateTimeOffset? to) =>
        {
            if (!await db.Repositories.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }

            var query = db.TrendPoints.AsNoTracking().Where(t => t.RepositoryId == id);
            if (from is { } f)
            {
                query = query.Where(t => t.CreatedAt >= f);
            }
            if (to is { } t2)
            {
                query = query.Where(t => t.CreatedAt <= t2);
            }

            var points = await query
                .OrderBy(t => t.CreatedAt)
                .Select(t => new TrendPointDto(
                    t.JobId, t.CommitSha, t.WarningTotal, t.WarningNew, t.WarningResolved,
                    t.ErrorCount, t.CoverageLine, t.TestsTotal, t.TestsFailed, t.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(points);
        })
        .WithSummary("Get the repository's trend points over time (for dashboards)")
        .Produces<List<TrendPointDto>>()
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }
}
