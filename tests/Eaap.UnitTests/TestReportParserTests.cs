using System.Text;
using Eaap.Adapters.TestReport;
using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.UnitTests;

public class TestReportParserTests
{
    private static FileStream OpenFixture(string name) =>
        File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static MemoryStream StreamOf(string xml) => new(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void TryParse_RealTrxFromDotnetTest_CountsOutcomes()
    {
        using var stream = OpenFixture("dotnet-test.trx");

        var summary = TestReportParser.TryParse(stream, out var problem);

        Assert.NotNull(summary);
        Assert.Empty(problem);
        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Skipped);

        var failure = Assert.Single(summary.Failures);
        Assert.Contains("Divide_ByZero_IsGuarded", failure.TestName);
        Assert.NotEmpty(failure.Message);
    }

    [Fact]
    public void TryParse_RealJUnitFromPytest_CountsOutcomes()
    {
        using var stream = OpenFixture("pytest-junit.xml");

        var summary = TestReportParser.TryParse(stream, out var problem);

        Assert.NotNull(summary);
        Assert.Empty(problem);
        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Skipped);

        var failure = Assert.Single(summary.Failures);
        Assert.Contains("test_discount_is_applied", failure.TestName);
        Assert.Contains("discount was not applied", failure.Message);
    }

    [Fact]
    public void TryParse_TrxCountersDisagreeWithResults_ElementsWin()
    {
        // Real TRX from dotnet test reports notExecuted="0" in <Counters> while still carrying a
        // NotExecuted result. Trusting the summary attributes would lose the skipped test.
        using var stream = OpenFixture("dotnet-test.trx");

        var summary = TestReportParser.TryParse(stream, out _);

        Assert.Equal(1, summary!.Skipped);
    }

    [Fact]
    public void TryParse_JunkButValidXml_ReturnsNullWithReason()
    {
        using var stream = OpenFixture("not-a-test-report.xml");

        var summary = TestReportParser.TryParse(stream, out var problem);

        Assert.Null(summary);
        Assert.Contains("configuration", problem);
    }

    [Fact]
    public void TryParse_MalformedXml_ReturnsNullWithReason()
    {
        using var stream = StreamOf("<testsuite><testcase name=\"broken\"");

        var summary = TestReportParser.TryParse(stream, out var problem);

        Assert.Null(summary);
        Assert.Contains("not valid XML", problem);
    }

    [Fact]
    public void TryParse_JUnitErrorElement_CountsAsFailure()
    {
        using var stream = StreamOf("""
            <testsuite name="s" tests="1">
              <testcase classname="mod" name="crashes" time="0.5">
                <error message="TypeError: cannot read property">traceback</error>
              </testcase>
            </testsuite>
            """);

        var summary = TestReportParser.TryParse(stream, out _);

        Assert.Equal(1, summary!.Failed);
        Assert.Equal(0, summary.Passed);
        Assert.Contains("TypeError", Assert.Single(summary.Failures).Message);
    }

    [Fact]
    public void TryParse_JUnitFileAndLineAttributes_BecomeLocation()
    {
        using var stream = StreamOf("""
            <testsuite name="s" tests="1">
              <testcase classname="mod" name="fails" file="src/cart.py" line="42">
                <failure message="boom" />
              </testcase>
            </testsuite>
            """);

        var failure = Assert.Single(TestReportParser.TryParse(stream, out _)!.Failures);

        Assert.Equal("src/cart.py", failure.FilePath);
        Assert.Equal(42, failure.StartLine);
    }

    [Fact]
    public void TryParse_RootTestsuitesWithMultipleSuites_AggregatesAll()
    {
        using var stream = StreamOf("""
            <testsuites>
              <testsuite name="a" tests="2">
                <testcase classname="a" name="ok" time="0.1" />
                <testcase classname="a" name="bad"><failure message="x" /></testcase>
              </testsuite>
              <testsuite name="b" tests="1">
                <testcase classname="b" name="skipme"><skipped /></testcase>
              </testsuite>
            </testsuites>
            """);

        var summary = TestReportParser.TryParse(stream, out _);

        Assert.Equal(3, summary!.Total);
        Assert.Equal(1, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public void Combine_AddsCountsAndConcatenatesFailures()
    {
        var first = new TestRunSummary(2, 1, 1, 0, 1.5, [new TestFailure("a", "m")]);
        var second = new TestRunSummary(3, 1, 1, 1, 2.5, [new TestFailure("b", "m")]);

        var combined = first.Combine(second);

        Assert.Equal(5, combined.Total);
        Assert.Equal(2, combined.Passed);
        Assert.Equal(2, combined.Failed);
        Assert.Equal(1, combined.Skipped);
        Assert.Equal(4.0, combined.DurationSeconds);
        Assert.Equal(2, combined.Failures.Count);
    }

    [Fact]
    public void BuildMetrics_EmitsAllFiveStandardKeys()
    {
        var metrics = TestReportSarif.BuildMetrics(new TestRunSummary(10, 7, 2, 1, 12.3456, []));

        Assert.Equal(
            ["tests.durationSeconds", "tests.failed", "tests.passed", "tests.skipped", "tests.total"],
            metrics.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(10, metrics["tests.total"]);
        Assert.Equal(2, metrics["tests.failed"]);
        Assert.Equal(12.346, metrics["tests.durationSeconds"]);
    }

    [Fact]
    public void Build_ProducesOneErrorResultPerFailure_AndValidatesAsSarif()
    {
        var summary = new TestRunSummary(2, 1, 1, 0, 1, [new TestFailure("Suite.Test", "boom", "src/a.cs", 7)]);

        var log = TestReportSarif.Build(summary);

        var run = Assert.Single(log.Runs);
        Assert.Equal("EaapTestReport", run.Tool.Driver.Name);
        var result = Assert.Single(run.Results);
        Assert.Equal("test.failed", result.RuleId);
        Assert.Equal(FailureLevel.Error, result.Level);
        Assert.Contains("Suite.Test", result.Message.Text);
        Assert.Equal(7, result.Locations[0].PhysicalLocation.Region.StartLine);

        // The platform validator must accept what the adapter writes.
        using var stream = new MemoryStream();
        Eaap.Sarif.SarifDocument.Save(log, stream);
        stream.Position = 0;
        Assert.Empty(Eaap.Sarif.SarifValidator.Validate(stream));
    }

    [Fact]
    public void Build_SavedToFile_IsStillAcceptedByTheValidator()
    {
        // Saving to a FileStream writes a UTF-8 BOM, unlike saving to a MemoryStream. Ingestion
        // reads exactly this file back from MinIO, so the BOM must not break validation.
        var summary = new TestRunSummary(1, 0, 1, 0, 1, [new TestFailure("Suite.Test", "boom")]);
        var path = Path.Combine(Path.GetTempPath(), $"eaap-sarif-{Guid.NewGuid():N}.sarif");

        try
        {
            using (var file = File.Create(path))
            {
                Eaap.Sarif.SarifDocument.Save(TestReportSarif.Build(summary), file);
            }

            using var reopened = File.OpenRead(path);
            Assert.Empty(Eaap.Sarif.SarifValidator.Validate(reopened));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Build_NoFailures_ProducesEmptyButValidSarif()
    {
        var log = TestReportSarif.Build(TestRunSummary.Empty);

        Assert.Empty(Assert.Single(log.Runs).Results);

        using var stream = new MemoryStream();
        Eaap.Sarif.SarifDocument.Save(log, stream);
        stream.Position = 0;
        Assert.Empty(Eaap.Sarif.SarifValidator.Validate(stream));
    }
}
