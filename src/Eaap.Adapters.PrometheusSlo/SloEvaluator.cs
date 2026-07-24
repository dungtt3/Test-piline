using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.Adapters.PrometheusSlo;

/// <summary>
/// Turns measured SLO values into SARIF (one result per violation) and metrics
/// (every SLO's value). Pure, so the comparison logic is unit-testable without Prometheus.
/// </summary>
public static class SloEvaluator
{
    public const string ToolName = "EaapPrometheusSlo";

    /// <summary>Evaluates the healthy condition <c>observed &lt;operator&gt; threshold</c>; a false result is a violation.</summary>
    public static bool IsViolation(double observed, string op, double threshold) => op.Trim() switch
    {
        "<" => !(observed < threshold),
        "<=" => !(observed <= threshold),
        ">" => !(observed > threshold),
        ">=" => !(observed >= threshold),
        "==" => observed != threshold,
        "!=" => observed == threshold,
        _ => throw new ArgumentException($"Unsupported SLO operator '{op}'.")
    };

    public static SloResult Evaluate(SloDefinition slo, double observed) =>
        new(slo, observed, IsViolation(observed, slo.Operator, slo.Threshold));

    public static SarifLog BuildSarif(IEnumerable<SloResult> results)
    {
        var run = new Run
        {
            Tool = new Tool
            {
                Driver = new ToolComponent
                {
                    Name = ToolName,
                    Version = "1.0.0",
                    InformationUri = new Uri("https://github.com/eaap/adapters/prometheus-slo")
                }
            },
            Results = [.. results.Where(r => r.Violated).Select(ToResult)]
        };

        return new SarifLog
        {
            SchemaUri = new Uri(
                "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json"),
            Version = SarifVersion.Current,
            Runs = [run]
        };
    }

    private static Result ToResult(SloResult r)
    {
        var result = new Result
        {
            RuleId = $"slo.{r.Slo.Id}",
            Level = ParseLevel(r.Slo.Level),
            Message = new Message
            {
                Text = $"SLO '{r.Slo.Id}' violated: observed {r.ObservedValue:G} is not {r.Slo.Operator} {r.Slo.Threshold:G} "
                    + $"(query: {r.Slo.Query})"
            }
        };
        // No file/line for a runtime finding; fingerprintKey keeps the fingerprint stable across runs.
        result.SetProperty("observedValue", r.ObservedValue);
        result.SetProperty("threshold", r.Slo.Threshold);
        result.SetProperty("query", r.Slo.Query);
        result.SetProperty("fingerprintKey", r.Slo.Id);
        return result;
    }

    /// <summary>Every SLO reports its value as runtime.slo.&lt;id&gt;.value, even when it passed.</summary>
    public static Dictionary<string, double> BuildMetrics(IEnumerable<SloResult> results) =>
        results.ToDictionary(r => $"runtime.slo.{r.Slo.Id}.value", r => r.ObservedValue);

    private static FailureLevel ParseLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "warning" => FailureLevel.Warning,
        "note" => FailureLevel.Note,
        _ => FailureLevel.Error
    };
}
