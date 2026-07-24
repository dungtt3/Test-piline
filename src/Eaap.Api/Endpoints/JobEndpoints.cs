using System.Text.Json;
using Eaap.Application;
using Eaap.Domain;
using Eaap.Infrastructure.Persistence;
using Eaap.Sarif;
using Microsoft.CodeAnalysis.Sarif;
using Microsoft.EntityFrameworkCore;

namespace Eaap.Api.Endpoints;

public static class JobEndpoints
{
    /// <summary>Parses a comma-separated severity filter like "High,Critical" into enum values.</summary>
    private static List<SecuritySeverity> ParseSeverities(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<SecuritySeverity>(s, ignoreCase: true, out var parsed) ? parsed : (SecuritySeverity?)null)
            .Where(s => s is not null)
            .Select(s => s!.Value)
            .Distinct()];
    }

    public static RouteGroupBuilder MapJobEndpoints(this RouteGroupBuilder group)
    {
        var jobs = group.MapGroup("/jobs").WithTags("Jobs");

        jobs.MapGet("/{id:guid}", async (Guid id, EaapDbContext db, CancellationToken ct) =>
        {
            var job = await db.AnalysisJobs
                .AsNoTracking()
                .Include(j => j.AnalyzerRuns)
                .Include(j => j.GateEvaluations)
                .FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            var gate = job.GateEvaluations.OrderByDescending(g => g.EvaluatedAt).FirstOrDefault();
            var response = new JobResponse(
                job.Id,
                job.SnapshotId,
                job.Status.ToString(),
                job.ArgoWorkflowName,
                job.RequestedAnalyzers,
                job.CreatedAt,
                job.StartedAt,
                job.FinishedAt,
                [.. job.AnalyzerRuns.Select(r => new AnalyzerRunDto(
                    r.Id, r.AnalyzerId, r.Status.ToString(), r.SarifArtifactPath,
                    r.WarningCount, r.StartedAt, r.FinishedAt))],
                gate is null
                    ? null
                    : new GateEvaluationDto(
                        gate.PolicyName,
                        gate.Passed,
                        JsonSerializer.Deserialize<string[]>(gate.Violations) ?? [],
                        gate.EvaluatedAt));
            return Results.Ok(response);
        })
        .WithSummary("Get a job with analyzer runs and gate evaluation")
        .Produces<JobResponse>()
        .Produces(StatusCodes.Status404NotFound);

        jobs.MapGet("/{id:guid}/warnings", async (
            Guid id,
            EaapDbContext db,
            CancellationToken ct,
            string? level,
            string? ruleId,
            bool? isNew,
            string? securitySeverity,
            string? cwe,
            bool includeSuppressed = false,
            int page = 1,
            int pageSize = 50) =>
        {
            if (!await db.AnalysisJobs.AnyAsync(j => j.Id == id, ct))
            {
                return Results.NotFound();
            }

            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = db.Warnings.AsNoTracking().Where(w => w.JobId == id);
            if (!string.IsNullOrEmpty(level) && Enum.TryParse<WarningLevel>(level, ignoreCase: true, out var parsedLevel))
            {
                query = query.Where(w => w.Level == parsedLevel);
            }
            if (!string.IsNullOrEmpty(ruleId))
            {
                query = query.Where(w => w.RuleId == ruleId);
            }
            if (isNew is { } wantNew)
            {
                query = query.Where(w => w.IsNew == wantNew);
            }
            // Suppressed findings are hidden by default (build spec phase 3 section 5).
            if (!includeSuppressed)
            {
                query = query.Where(w => !w.IsSuppressed);
            }
            var severities = ParseSeverities(securitySeverity);
            if (severities.Count > 0)
            {
                query = query.Where(w => severities.Contains(w.SecuritySeverity));
            }
            if (!string.IsNullOrEmpty(cwe))
            {
                query = query.Where(w => w.Cwe == cwe);
            }

            var totalCount = await query.CountAsync(ct);
            var items = await query
                .OrderBy(w => w.FilePath).ThenBy(w => w.StartLine).ThenBy(w => w.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(w => new WarningDto(
                    w.Id, w.AnalyzerRunId, w.RuleId, w.Level.ToString(), w.Message,
                    w.FilePath, w.StartLine, w.EndLine, w.Fingerprint, w.IsNew,
                    w.SecuritySeverity.ToString(), w.Cve, w.Cwe, w.IsSuppressed))
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<WarningDto>(items, page, pageSize, totalCount));
        })
        .WithSummary("List warnings of a job (filter by level, ruleId, isNew, securitySeverity, cwe)")
        .Produces<PagedResult<WarningDto>>()
        .Produces(StatusCodes.Status404NotFound);

        jobs.MapGet("/{id:guid}/security-summary", async (Guid id, EaapDbContext db, CancellationToken ct) =>
        {
            if (!await db.AnalysisJobs.AnyAsync(j => j.Id == id, ct))
            {
                return Results.NotFound();
            }

            // Suppressed findings are excluded, consistent with the default warnings view and the gate.
            var warnings = db.Warnings.AsNoTracking().Where(w => w.JobId == id && !w.IsSuppressed);

            var bySeverity = await warnings
                .GroupBy(w => w.SecuritySeverity)
                .Select(g => new { Severity = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var byCwe = await warnings
                .Where(w => w.Cwe != null)
                .GroupBy(w => w.Cwe!)
                .Select(g => new CountItem(g.Key, g.Count()))
                .ToListAsync(ct);

            var byCve = await warnings
                .Where(w => w.Cve != null)
                .GroupBy(w => w.Cve!)
                .Select(g => new CountItem(g.Key, g.Count()))
                .ToListAsync(ct);

            // Always present all five severities, defaulting to zero.
            var severityCounts = Enum.GetValues<SecuritySeverity>()
                .ToDictionary(
                    s => s.ToString().ToLowerInvariant(),
                    s => bySeverity.FirstOrDefault(x => x.Severity == s)?.Count ?? 0);

            return Results.Ok(new SecuritySummaryResponse(
                severityCounts,
                [.. byCwe.OrderByDescending(c => c.Count).ThenBy(c => c.Key)],
                [.. byCve.OrderByDescending(c => c.Count).ThenBy(c => c.Key)]));
        })
        .WithSummary("Security summary of a job: counts by severity, CWE and CVE")
        .Produces<SecuritySummaryResponse>()
        .Produces(StatusCodes.Status404NotFound);

        jobs.MapGet("/{id:guid}/sarif", async (
            Guid id,
            EaapDbContext db,
            IObjectStorage storage,
            CancellationToken ct) =>
        {
            var job = await db.AnalysisJobs
                .AsNoTracking()
                .Include(j => j.AnalyzerRuns)
                .FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            var logs = new List<SarifLog>();
            foreach (var run in job.AnalyzerRuns.Where(r => !string.IsNullOrEmpty(r.SarifArtifactPath)))
            {
                await using var stream = await storage.DownloadAsync(run.SarifArtifactPath!, ct);
                logs.Add(SarifDocument.Load(stream));
            }

            var merged = SarifDocument.Merge(logs);
            using var output = new MemoryStream();
            SarifDocument.Save(merged, output);
            return Results.File(output.ToArray(), "application/sarif+json");
        })
        .WithSummary("Get the merged SARIF log of a job")
        .Produces(StatusCodes.Status200OK, contentType: "application/sarif+json")
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }
}
