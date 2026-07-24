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
    private static SuppressionDto ToDto(Suppression s) =>
        new(s.Id, s.Fingerprint, s.Reason, s.CreatedBy, s.ExpiresAt, s.CreatedAt);

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

        repositories.MapPost("/{id:guid}/suppressions", async (
            Guid id, CreateSuppressionRequest request, EaapDbContext db, CancellationToken ct) =>
        {
            if (!await db.Repositories.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }

            var errors = new Dictionary<string, string[]>();
            var fingerprint = request.Fingerprint?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(fingerprint))
            {
                errors[nameof(request.Fingerprint)] = ["fingerprint is required."];
            }
            if ((request.Reason?.Trim().Length ?? 0) < 10)
            {
                errors[nameof(request.Reason)] = ["reason must be at least 10 characters."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            // The fingerprint must belong to a real finding or baseline of this repository.
            var known = await db.Warnings
                    .AnyAsync(w => w.Fingerprint == fingerprint
                        && db.AnalysisJobs.Any(j => j.Id == w.JobId
                            && db.Snapshots.Any(s => s.Id == j.SnapshotId && s.RepositoryId == id)), ct)
                || await db.WarningBaselines.AnyAsync(b => b.RepositoryId == id && b.Fingerprint == fingerprint, ct);
            if (!known)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Fingerprint)] = ["fingerprint not found in this repository's warnings or baseline."]
                });
            }

            if (await db.Suppressions.AnyAsync(s => s.RepositoryId == id && s.Fingerprint == fingerprint, ct))
            {
                return Results.Conflict(new { message = "A suppression already exists for this fingerprint." });
            }

            var suppression = new Suppression
            {
                Id = Guid.NewGuid(),
                RepositoryId = id,
                Fingerprint = fingerprint,
                Reason = request.Reason!.Trim(),
                CreatedBy = "anonymous", // free text until Phase 4 introduces auth
                ExpiresAt = request.ExpiresAt,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Suppressions.Add(suppression);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/repositories/{id}/suppressions/{suppression.Id}",
                ToDto(suppression));
        })
        .WithSummary("Suppress a finding by fingerprint for this repository")
        .Produces<SuppressionDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        repositories.MapGet("/{id:guid}/suppressions", async (
            Guid id, EaapDbContext db, CancellationToken ct, bool includeExpired = false) =>
        {
            if (!await db.Repositories.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }

            var now = DateTimeOffset.UtcNow;
            var query = db.Suppressions.AsNoTracking().Where(s => s.RepositoryId == id);
            if (!includeExpired)
            {
                query = query.Where(s => s.ExpiresAt == null || s.ExpiresAt > now);
            }

            var items = await query
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new SuppressionDto(s.Id, s.Fingerprint, s.Reason, s.CreatedBy, s.ExpiresAt, s.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(items);
        })
        .WithSummary("List a repository's suppressions")
        .Produces<List<SuppressionDto>>()
        .Produces(StatusCodes.Status404NotFound);

        repositories.MapDelete("/{id:guid}/suppressions/{suppressionId:guid}", async (
            Guid id, Guid suppressionId, EaapDbContext db, CancellationToken ct) =>
        {
            var suppression = await db.Suppressions
                .FirstOrDefaultAsync(s => s.Id == suppressionId && s.RepositoryId == id, ct);
            if (suppression is null)
            {
                return Results.NotFound();
            }
            db.Suppressions.Remove(suppression);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("Remove a suppression")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }
}
