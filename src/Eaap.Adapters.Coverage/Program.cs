using System.Globalization;
using System.Text.Json;
using Eaap.Adapters.Coverage;
using Eaap.Sarif;

// EAAP adapter contract (build spec section 5): read /workspace (read-only), write SARIF into
// /results, keep the original reports in /artifacts, and exit 0 even when findings exist.
var coverageDir = Environment.GetEnvironmentVariable("EAAP_COVERAGE_DIR") ?? "/workspace/.eaap/coverage";
var resultsDir = Environment.GetEnvironmentVariable("EAAP_RESULTS_DIR") ?? "/results";
var artifactsDir = Environment.GetEnvironmentVariable("EAAP_ARTIFACTS_DIR") ?? "/artifacts";

var threshold = ParseThreshold(Environment.GetEnvironmentVariable("COVERAGE_FILE_THRESHOLD"));

Directory.CreateDirectory(resultsDir);
Directory.CreateDirectory(artifactsDir);

var summary = CoverageSummary.Empty;

if (!Directory.Exists(coverageDir))
{
    Log($"no coverage directory at {coverageDir}; emitting an empty report");
}
else
{
    // Cobertura takes priority over lcov: when a project emits both, they describe the same
    // run and counting both would double every line.
    var cobertura = FindFiles(coverageDir, path =>
        path.EndsWith(".cobertura.xml", StringComparison.OrdinalIgnoreCase)
        || (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).Contains("cobertura", StringComparison.OrdinalIgnoreCase)));

    // Fall back to any *.xml only when nothing was explicitly named cobertura.
    if (cobertura.Count == 0)
    {
        cobertura = FindFiles(coverageDir, path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
    }

    var lcov = FindFiles(coverageDir, path =>
        Path.GetFileName(path).Equals("lcov.info", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".lcov", StringComparison.OrdinalIgnoreCase));

    if (cobertura.Count > 0 && lcov.Count > 0)
    {
        Log($"found {cobertura.Count} Cobertura and {lcov.Count} lcov report(s); using Cobertura only");
        lcov = [];
    }

    foreach (var path in cobertura)
    {
        Ingest(path, stream => CoverageParser.TryParseCobertura(stream, out var problem) is { } parsed
            ? (parsed, null)
            : (null, problem));
    }

    foreach (var path in lcov)
    {
        Ingest(path, stream => CoverageParser.TryParseLcov(stream, out var problem) is { } parsed
            ? (parsed, null)
            : (null, problem));
    }

    if (cobertura.Count == 0 && lcov.Count == 0)
    {
        Log($"no Cobertura or lcov report found under {coverageDir}; emitting an empty report");
    }
}

var sarifPath = Path.Combine(resultsDir, "coverage.sarif");
await using (var sarifStream = File.Create(sarifPath))
{
    SarifDocument.Save(CoverageSarif.Build(summary, threshold), sarifStream);
}

var metrics = CoverageSarif.BuildMetrics(summary);
var metricsPath = Path.Combine(resultsDir, "metrics.json");
await using (var metricsStream = File.Create(metricsPath))
{
    await JsonSerializer.SerializeAsync(
        metricsStream, new { metrics }, new JsonSerializerOptions { WriteIndented = true });
}

if (!summary.HasAnyMeasurement)
{
    Log("no coverage measured; metrics.json carries no coverage.* key");
}

Log($"wrote {sarifPath} and {metricsPath} "
    + $"(lines {summary.LinesCovered}/{summary.LinesValid}, files {summary.Files.Count}, threshold {threshold}%)");
return 0;

void Ingest(string path, Func<Stream, (CoverageSummary? Parsed, string? Problem)> parse)
{
    try
    {
        using var stream = File.OpenRead(path);
        var (parsed, problem) = parse(stream);
        if (parsed is null)
        {
            Log($"skipping {path}: {problem}");
            return;
        }

        summary = summary.Combine(parsed);
        Log($"parsed {path}: {parsed.LinesCovered}/{parsed.LinesValid} lines over {parsed.Files.Count} file(s)");
        CopyToArtifacts(path, coverageDir, artifactsDir);
    }
    catch (Exception e)
    {
        Log($"skipping {path}: {e.Message}");
    }
}

static List<string> FindFiles(string root, Func<string, bool> predicate) =>
    [.. Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
        .Where(predicate)
        .OrderBy(path => path, StringComparer.Ordinal)];

static double ParseThreshold(string? raw)
{
    const double defaultThreshold = 50d;
    if (string.IsNullOrWhiteSpace(raw))
    {
        return defaultThreshold;
    }
    return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        ? value
        : defaultThreshold;
}

static void Log(string message) => Console.WriteLine($"[coverage-adapter] {message}");

static void CopyToArtifacts(string path, string sourceRoot, string artifactsDir)
{
    try
    {
        var destination = Path.Combine(artifactsDir, Path.GetRelativePath(sourceRoot, path));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(path, destination, overwrite: true);
    }
    catch (Exception e)
    {
        Log($"could not copy {path} to artifacts: {e.Message}");
    }
}
