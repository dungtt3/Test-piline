using System.Text.Json;
using Eaap.Adapters.TestReport;
using Eaap.Sarif;

// EAAP adapter contract (build spec section 5): read /workspace (read-only), write SARIF into
// /results, keep the original reports in /artifacts, and exit 0 even when findings exist.
var testResultsDir = Environment.GetEnvironmentVariable("EAAP_TEST_RESULTS_DIR")
    ?? "/workspace/.eaap/test-results";
var resultsDir = Environment.GetEnvironmentVariable("EAAP_RESULTS_DIR") ?? "/results";
var artifactsDir = Environment.GetEnvironmentVariable("EAAP_ARTIFACTS_DIR") ?? "/artifacts";

Directory.CreateDirectory(resultsDir);
Directory.CreateDirectory(artifactsDir);

var summary = TestRunSummary.Empty;

if (!Directory.Exists(testResultsDir))
{
    Log($"no test report directory at {testResultsDir}; emitting an empty report");
}
else
{
    var reportFiles = Directory
        .EnumerateFiles(testResultsDir, "*.*", SearchOption.AllDirectories)
        .Where(path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToList();

    if (reportFiles.Count == 0)
    {
        Log($"no *.xml or *.trx report found under {testResultsDir}; emitting an empty report");
    }

    foreach (var path in reportFiles)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var parsed = TestReportParser.TryParse(stream, out var problem);
            if (parsed is null)
            {
                // A junk file must not sink the run: log it and keep going.
                Log($"skipping {path}: {problem}");
                continue;
            }

            summary = summary.Combine(parsed);
            Log($"parsed {path}: {parsed.Total} tests, {parsed.Failed} failed, {parsed.Skipped} skipped");
        }
        catch (Exception e)
        {
            Log($"skipping {path}: {e.Message}");
            continue;
        }

        CopyToArtifacts(path, testResultsDir, artifactsDir);
    }
}

var sarifPath = Path.Combine(resultsDir, "test-report.sarif");
await using (var sarifStream = File.Create(sarifPath))
{
    SarifDocument.Save(TestReportSarif.Build(summary), sarifStream);
}

var metricsPath = Path.Combine(resultsDir, "metrics.json");
await using (var metricsStream = File.Create(metricsPath))
{
    await JsonSerializer.SerializeAsync(
        metricsStream,
        new { metrics = TestReportSarif.BuildMetrics(summary) },
        new JsonSerializerOptions { WriteIndented = true });
}

Log($"wrote {sarifPath} ({summary.Failures.Count} results) and {metricsPath} " +
    $"(total={summary.Total} passed={summary.Passed} failed={summary.Failed} skipped={summary.Skipped})");
return 0;

static void Log(string message) => Console.WriteLine($"[test-report-adapter] {message}");

static void CopyToArtifacts(string path, string sourceRoot, string artifactsDir)
{
    try
    {
        var relative = Path.GetRelativePath(sourceRoot, path);
        var destination = Path.Combine(artifactsDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(path, destination, overwrite: true);
    }
    catch (Exception e)
    {
        Log($"could not copy {path} to artifacts: {e.Message}");
    }
}
