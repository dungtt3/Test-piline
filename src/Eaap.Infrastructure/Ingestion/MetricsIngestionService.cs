using System.Text.Json;
using Eaap.Domain.Entities;
using Eaap.Infrastructure.Persistence;

namespace Eaap.Infrastructure.Ingestion;

/// <summary>
/// Reads an adapter's optional /results/metrics.json into a MetricSet (build spec phase 2 section 3).
/// Schema: { "metrics": { "&lt;dot.separated.key&gt;": &lt;number&gt; } }.
/// </summary>
public class MetricsIngestionService(EaapDbContext db)
{
    /// <summary>
    /// Extracts the numeric metric entries. Anything that is not a number is skipped rather than
    /// treated as fatal — a malformed metrics file must never invalidate an otherwise good SARIF run.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Parse(Stream metricsStream)
    {
        using var document = JsonDocument.Parse(metricsStream);
        var metrics = new Dictionary<string, double>();

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("metrics", out var metricsElement)
            || metricsElement.ValueKind != JsonValueKind.Object)
        {
            return metrics;
        }

        foreach (var property in metricsElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var value))
            {
                metrics[property.Name] = value;
            }
        }

        return metrics;
    }

    /// <summary>Stores the metrics of a run; returns null when the document carries no usable metric.</summary>
    public async Task<MetricSet?> IngestAsync(AnalyzerRun run, Stream metricsStream, CancellationToken ct = default)
    {
        var metrics = Parse(metricsStream);
        if (metrics.Count == 0)
        {
            return null;
        }

        var metricSet = new MetricSet
        {
            Id = Guid.NewGuid(),
            JobId = run.JobId,
            AnalyzerRunId = run.Id,
            Metrics = new Dictionary<string, double>(metrics),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.MetricSets.Add(metricSet);
        await db.SaveChangesAsync(ct);
        return metricSet;
    }
}
