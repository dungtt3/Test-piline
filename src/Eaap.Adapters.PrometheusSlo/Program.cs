using System.Text.Json;
using Eaap.Adapters.PrometheusSlo;
using Eaap.Sarif;

// Query-mode adapter (build spec phase 4 section 2): no /workspace. Reads the runtime config
// from EAAP_RUNTIME_CONFIG, queries Prometheus for each SLO, and writes SARIF + metrics.
// Prometheus unreachable is a hard failure (exit != 0), distinct from an SLO being violated.
var resultsDir = Environment.GetEnvironmentVariable("EAAP_RESULTS_DIR") ?? "/results";
Directory.CreateDirectory(resultsDir);

var configJson = Environment.GetEnvironmentVariable("EAAP_RUNTIME_CONFIG");
if (string.IsNullOrWhiteSpace(configJson))
{
    Log("EAAP_RUNTIME_CONFIG is empty; nothing to evaluate");
    await WriteOutputsAsync(resultsDir, []);
    return 0;
}

RuntimeConfig config;
try
{
    config = JsonSerializer.Deserialize<RuntimeConfig>(configJson)
        ?? throw new InvalidOperationException("runtime config deserialized to null");
}
catch (Exception e)
{
    Log($"invalid EAAP_RUNTIME_CONFIG: {e.Message}");
    return 2;
}

var prometheusUrl = Environment.GetEnvironmentVariable("EAAP_PROMETHEUS_URL");
if (!string.IsNullOrWhiteSpace(prometheusUrl))
{
    config = config with { PrometheusUrl = prometheusUrl };
}
if (string.IsNullOrWhiteSpace(config.PrometheusUrl))
{
    Log("no Prometheus URL configured");
    return 2;
}

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var client = new PrometheusClient(httpClient, config.PrometheusUrl);

var results = new List<SloResult>();
foreach (var slo in config.Slos)
{
    double observed;
    try
    {
        observed = await client.QueryAsync(slo.Query);
    }
    catch (Exception e)
    {
        // Cannot reach/parse Prometheus: the analyzer failed, it is not an SLO pass or fail.
        Log($"Prometheus query for SLO '{slo.Id}' failed: {e.Message}");
        return 1;
    }

    var evaluated = SloEvaluator.Evaluate(slo, observed);
    results.Add(evaluated);
    Log($"SLO '{slo.Id}': observed={observed:G} threshold={slo.Threshold:G} op='{slo.Operator}' " +
        $"-> {(evaluated.Violated ? "VIOLATED" : "ok")}");
}

await WriteOutputsAsync(resultsDir, results);
Log($"evaluated {results.Count} SLO(s), {results.Count(r => r.Violated)} violated");
return 0;

static async Task WriteOutputsAsync(string resultsDir, List<SloResult> results)
{
    var sarifPath = Path.Combine(resultsDir, "prometheus-slo.sarif");
    await using (var sarifStream = File.Create(sarifPath))
    {
        SarifDocument.Save(SloEvaluator.BuildSarif(results), sarifStream);
    }

    var metricsPath = Path.Combine(resultsDir, "metrics.json");
    await using (var metricsStream = File.Create(metricsPath))
    {
        await JsonSerializer.SerializeAsync(
            metricsStream,
            new { metrics = SloEvaluator.BuildMetrics(results) },
            new JsonSerializerOptions { WriteIndented = true });
    }
}

static void Log(string message) => Console.WriteLine($"[prometheus-slo-adapter] {message}");
