using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.Adapters.Coverage;

/// <summary>
/// Coverage numbers are metrics; only poorly covered files become SARIF warnings.
/// </summary>
public static class CoverageSarif
{
    public const string ToolName = "EaapCoverage";
    public const string LowCoverageRuleId = "coverage.file.low";
    public const string TruncatedRuleId = "coverage.files.truncated";

    /// <summary>Cap on emitted per-file results, so a legacy repo cannot flood the warning list.</summary>
    public const int MaxFileResults = 200;

    public static SarifLog Build(CoverageSummary summary, double fileThreshold)
    {
        // Worst files first: if the list gets truncated, the ones that matter survive.
        var lowCoverage = summary.Files
            .Where(file => file.LinesValid > 0 && file.LineRate < fileThreshold)
            .OrderBy(file => file.LineRate)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        var results = lowCoverage
            .Take(MaxFileResults)
            .Select(file => ToResult(file, fileThreshold))
            .ToList();

        if (lowCoverage.Count > MaxFileResults)
        {
            results.Add(new Result
            {
                RuleId = TruncatedRuleId,
                Level = FailureLevel.Warning,
                Message = new Message
                {
                    Text = $"{lowCoverage.Count} files are below {fileThreshold:0.##}% line coverage; "
                        + $"only the {MaxFileResults} worst are reported individually."
                }
            });
        }

        var run = new Run
        {
            Tool = new Tool
            {
                Driver = new ToolComponent
                {
                    Name = ToolName,
                    Version = "1.0.0",
                    InformationUri = new Uri("https://github.com/eaap/adapters/coverage")
                }
            },
            Results = results
        };

        return new SarifLog
        {
            SchemaUri = new Uri(
                "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json"),
            Version = SarifVersion.Current,
            Runs = [run]
        };
    }

    private static Result ToResult(FileCoverage file, double threshold) => new()
    {
        RuleId = LowCoverageRuleId,
        Level = FailureLevel.Warning,
        Message = new Message
        {
            Text = $"Line coverage {file.LineRate:0.##}% is below the {threshold:0.##}% threshold "
                + $"({file.LinesCovered}/{file.LinesValid} lines)."
        },
        Locations =
        [
            new Location
            {
                PhysicalLocation = new PhysicalLocation
                {
                    ArtifactLocation = new ArtifactLocation
                    {
                        Uri = new Uri(file.Path, UriKind.RelativeOrAbsolute)
                    }
                }
            }
        ]
    };

    /// <summary>Emits a key only when that dimension was actually measured.</summary>
    public static Dictionary<string, double> BuildMetrics(CoverageSummary summary)
    {
        var metrics = new Dictionary<string, double>();
        if (summary.LineRate is { } line)
        {
            metrics["coverage.line"] = Math.Round(line, 2);
        }
        if (summary.BranchRate is { } branch)
        {
            metrics["coverage.branch"] = Math.Round(branch, 2);
        }
        if (summary.MethodRate is { } method)
        {
            metrics["coverage.method"] = Math.Round(method, 2);
        }
        return metrics;
    }
}
