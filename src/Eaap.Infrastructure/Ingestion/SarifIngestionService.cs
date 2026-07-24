using System.Text.Json.Nodes;
using Eaap.Domain;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Persistence;
using Eaap.Sarif;
using Microsoft.CodeAnalysis.Sarif;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Eaap.Infrastructure.Ingestion;

/// <summary>Maps SARIF results to Warning rows, deduplicating by fingerprint within a job.</summary>
public class SarifIngestionService(EaapDbContext db, IOptions<AdapterOptions> adapterOptions)
{
    private static readonly JsonSerializerSettings RawSerializerSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    /// <summary>Ingests a SARIF log for the given analyzer run and returns the number of warnings stored.</summary>
    public async Task<int> IngestAsync(AnalyzerRun run, Stream sarifStream, CancellationToken ct = default)
    {
        var log = SarifDocument.Load(sarifStream);

        // Whether this analyzer is a security scanner decides how findings without an explicit
        // CVSS score are classified (build spec phase 3 section 4).
        var isSecurity = adapterOptions.Value.Registry.TryGetValue(run.AnalyzerId, out var adapter)
            && adapter.IsSecurity;

        // Warnings already stored for this job (e.g. from other analyzer runs) participate in dedup.
        var keptByFingerprint = await db.Warnings
            .Where(w => w.JobId == run.JobId)
            .ToDictionaryAsync(w => w.Fingerprint, ct);

        var storedForRun = 0;
        foreach (var sarifRun in log.Runs ?? [])
        {
            foreach (var result in sarifRun.Results ?? [])
            {
                var ruleId = result.RuleId ?? result.Rule?.Id ?? "unknown";
                var message = result.Message?.Text ?? string.Empty;
                var location = result.Locations?.FirstOrDefault()?.PhysicalLocation;
                var filePath = location?.ArtifactLocation?.Uri?.OriginalString;
                int? startLine = location?.Region?.StartLine > 0 ? location.Region.StartLine : null;
                int? endLine = location?.Region?.EndLine > 0 ? location.Region.EndLine : null;

                var fingerprint = WarningFingerprint.Compute(ruleId, filePath, startLine, message);
                if (keptByFingerprint.TryGetValue(fingerprint, out var duplicate))
                {
                    duplicate.SarifRaw = IncrementDuplicateCount(duplicate.SarifRaw);
                    continue;
                }

                var security = SecurityEnricher.Enrich(result, sarifRun, isSecurity);

                var warning = new Warning
                {
                    Id = Guid.NewGuid(),
                    JobId = run.JobId,
                    AnalyzerRunId = run.Id,
                    RuleId = ruleId,
                    Level = MapLevel(result.Level),
                    Message = message,
                    FilePath = WarningFingerprint.NormalizePath(filePath),
                    StartLine = startLine,
                    EndLine = endLine,
                    Fingerprint = fingerprint,
                    SecuritySeverity = security.Severity,
                    Cve = security.Cve,
                    Cwe = security.Cwe,
                    SarifRaw = JsonConvert.SerializeObject(result, RawSerializerSettings),
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Warnings.Add(warning);
                keptByFingerprint[fingerprint] = warning;
                storedForRun++;
            }
        }

        run.WarningCount = storedForRun;
        await db.SaveChangesAsync(ct);
        return storedForRun;
    }

    private static WarningLevel MapLevel(FailureLevel level) => level switch
    {
        FailureLevel.Error => WarningLevel.Error,
        FailureLevel.Warning => WarningLevel.Warning,
        FailureLevel.Note => WarningLevel.Note,
        _ => WarningLevel.None
    };

    /// <summary>Bumps properties.duplicateCount in the stored raw SARIF result (2 = seen twice).</summary>
    private static string IncrementDuplicateCount(string sarifRaw)
    {
        var root = JsonNode.Parse(sarifRaw) as JsonObject ?? [];
        if (root["properties"] is not JsonObject properties)
        {
            properties = [];
            root["properties"] = properties;
        }
        var current = properties["duplicateCount"]?.GetValue<int>() ?? 1;
        properties["duplicateCount"] = current + 1;
        return root.ToJsonString();
    }
}
