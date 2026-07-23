using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.Adapters.TestReport;

/// <summary>Turns failing tests into SARIF results. Test counts are metrics, not warnings.</summary>
public static class TestReportSarif
{
    public const string ToolName = "EaapTestReport";
    public const string RuleId = "test.failed";

    public static SarifLog Build(TestRunSummary summary)
    {
        var run = new Run
        {
            Tool = new Tool
            {
                Driver = new ToolComponent
                {
                    Name = ToolName,
                    Version = "1.0.0",
                    InformationUri = new Uri("https://github.com/eaap/adapters/test-report")
                }
            },
            Results = [.. summary.Failures.Select(ToResult)]
        };

        return new SarifLog
        {
            SchemaUri = new Uri(
                "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json"),
            Version = SarifVersion.Current,
            Runs = [run]
        };
    }

    private static Result ToResult(TestFailure failure)
    {
        var result = new Result
        {
            RuleId = RuleId,
            Level = FailureLevel.Error,
            Message = new Message { Text = $"{failure.TestName}: {failure.Message}" }
        };

        // Most report formats carry no source location; SARIF allows results without one.
        if (string.IsNullOrWhiteSpace(failure.FilePath))
        {
            return result;
        }

        var physicalLocation = new PhysicalLocation
        {
            ArtifactLocation = new ArtifactLocation
            {
                Uri = new Uri(failure.FilePath, UriKind.RelativeOrAbsolute)
            }
        };
        if (failure.StartLine is > 0)
        {
            physicalLocation.Region = new Region { StartLine = failure.StartLine.Value };
        }

        result.Locations = [new Location { PhysicalLocation = physicalLocation }];
        return result;
    }

    /// <summary>The five standard tests.* keys required by build spec phase 2 section 3.</summary>
    public static Dictionary<string, double> BuildMetrics(TestRunSummary summary) => new()
    {
        ["tests.total"] = summary.Total,
        ["tests.passed"] = summary.Passed,
        ["tests.failed"] = summary.Failed,
        ["tests.skipped"] = summary.Skipped,
        ["tests.durationSeconds"] = Math.Round(summary.DurationSeconds, 3)
    };
}
