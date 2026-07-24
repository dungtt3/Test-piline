using System.Text;
using Eaap.Adapters.Coverage;
using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.UnitTests;

public class CoverageParserTests
{
    private static FileStream OpenFixture(string name) =>
        File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));

    private static CoverageSummary ParseCobertura(string fixture)
    {
        using var stream = OpenFixture(fixture);
        var summary = CoverageParser.TryParseCobertura(stream, out var problem);
        Assert.NotNull(summary);
        Assert.Empty(problem);
        return summary;
    }

    [Fact]
    public void TryParseCobertura_RealCoverletReport_ReadsCountersAndFiles()
    {
        var summary = ParseCobertura("coverlet-cobertura.xml");

        Assert.True(summary.LinesValid > 0);
        Assert.True(summary.LinesCovered > 0);
        Assert.NotEmpty(summary.Files);
        Assert.All(summary.Files, file => Assert.DoesNotContain('\\', file.Path));
        Assert.NotNull(summary.LineRate);
    }

    [Fact]
    public void Combine_TwoReports_SumsLinesInsteadOfAveragingRates()
    {
        // 9/10 = 90% and 1/90 = 1.11%. Averaging the two rates gives 45.56%, which would be
        // badly wrong; the correct answer weights by line count: 10/100 = 10%.
        var a = ParseCobertura("cobertura-merge-a.xml");
        var b = ParseCobertura("cobertura-merge-b.xml");

        Assert.Equal(90d, a.LineRate!.Value, 2);
        Assert.Equal(1.11d, b.LineRate!.Value, 2);

        var merged = a.Combine(b);

        Assert.Equal(10, merged.LinesCovered);
        Assert.Equal(100, merged.LinesValid);
        Assert.Equal(10d, merged.LineRate!.Value, 6);
        Assert.NotEqual(45.56d, merged.LineRate!.Value, 2);
    }

    [Fact]
    public void Combine_SameFileInTwoReports_MergesIntoOneEntry()
    {
        var first = new CoverageSummary(5, 10, 0, 0, 0, 0, [new FileCoverage("src/a.cs", 5, 10)]);
        var second = new CoverageSummary(3, 10, 0, 0, 0, 0, [new FileCoverage("src/a.cs", 3, 10)]);

        var merged = first.Combine(second);

        var file = Assert.Single(merged.Files);
        Assert.Equal(8, file.LinesCovered);
        Assert.Equal(20, file.LinesValid);
    }

    [Fact]
    public void TryParseCobertura_PartialClassesSharingAFile_CountEachLineOnce()
    {
        using var stream = StreamOf("""
            <coverage lines-covered="2" lines-valid="3">
              <packages><package name="p"><classes>
                <class name="A" filename="src/a.cs">
                  <lines><line number="1" hits="1" /><line number="2" hits="0" /></lines>
                </class>
                <class name="A.Nested" filename="src/a.cs">
                  <lines><line number="2" hits="4" /><line number="3" hits="1" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """);

        var summary = CoverageParser.TryParseCobertura(stream, out _);

        var file = Assert.Single(summary!.Files);
        // Lines 1,2,3 — line 2 is covered by the second class, so 3 of 3 lines are hit.
        Assert.Equal(3, file.LinesValid);
        Assert.Equal(3, file.LinesCovered);
    }

    [Fact]
    public void TryParseCobertura_NotACoverageDocument_ReturnsNullWithReason()
    {
        using var stream = OpenFixture("not-a-test-report.xml");

        var summary = CoverageParser.TryParseCobertura(stream, out var problem);

        Assert.Null(summary);
        Assert.Contains("configuration", problem);
    }

    [Fact]
    public void TryParseCobertura_MalformedXml_ReturnsNullWithReason()
    {
        using var stream = StreamOf("<coverage lines-valid=");

        Assert.Null(CoverageParser.TryParseCobertura(stream, out var problem));
        Assert.Contains("not valid XML", problem);
    }

    [Fact]
    public void TryParseLcov_RealTracefile_ReadsLinesBranchesAndFunctions()
    {
        using var stream = OpenFixture("lcov.info");

        var summary = CoverageParser.TryParseLcov(stream, out var problem);

        Assert.NotNull(summary);
        Assert.Empty(problem);
        // cart.js: 3 of 5 lines hit; checkout.js: 4 of 4.
        Assert.Equal(9, summary.LinesValid);
        Assert.Equal(7, summary.LinesCovered);
        Assert.Equal(2, summary.BranchesValid);
        Assert.Equal(1, summary.BranchesCovered);
        Assert.Equal(3, summary.MethodsValid);
        Assert.Equal(2, summary.MethodsCovered);

        Assert.Equal(2, summary.Files.Count);
        var cart = summary.Files.Single(f => f.Path == "src/cart.js");
        Assert.Equal(5, cart.LinesValid);
        Assert.Equal(3, cart.LinesCovered);
    }

    [Fact]
    public void TryParseLcov_NotATracefile_ReturnsNullWithReason()
    {
        using var stream = StreamOf("just some text\nwithout any records\n");

        Assert.Null(CoverageParser.TryParseLcov(stream, out var problem));
        Assert.Contains("no SF record", problem);
    }

    [Fact]
    public void Build_FilesBelowThreshold_BecomeWarningsWithLocation()
    {
        var summary = new CoverageSummary(10, 100, 0, 0, 0, 0,
        [
            new FileCoverage("src/good.cs", 9, 10),   // 90%
            new FileCoverage("src/bad.cs", 1, 10)     // 10%
        ]);

        var results = CoverageSarif.Build(summary, 50).Runs[0].Results;

        var result = Assert.Single(results);
        Assert.Equal("coverage.file.low", result.RuleId);
        Assert.Equal(FailureLevel.Warning, result.Level);
        Assert.Contains("10%", result.Message.Text);
        Assert.Equal("src/bad.cs", result.Locations[0].PhysicalLocation.ArtifactLocation.Uri.OriginalString);
    }

    [Fact]
    public void Build_ThresholdIsConfigurable()
    {
        var summary = new CoverageSummary(9, 10, 0, 0, 0, 0, [new FileCoverage("src/a.cs", 9, 10)]);

        Assert.Empty(CoverageSarif.Build(summary, 50).Runs[0].Results);
        Assert.Single(CoverageSarif.Build(summary, 95).Runs[0].Results);
    }

    [Fact]
    public void Build_MoreThanTwoHundredLowFiles_TruncatesWithASummaryResult()
    {
        var files = Enumerable.Range(0, CoverageSarif.MaxFileResults + 5)
            .Select(i => new FileCoverage($"src/file{i:D4}.cs", 0, 10))
            .ToList();
        var summary = new CoverageSummary(0, files.Count * 10, 0, 0, 0, 0, files);

        var results = CoverageSarif.Build(summary, 50).Runs[0].Results;

        Assert.Equal(CoverageSarif.MaxFileResults + 1, results.Count);
        var truncated = results[^1];
        Assert.Equal("coverage.files.truncated", truncated.RuleId);
        Assert.Contains($"{files.Count} files", truncated.Message.Text);
    }

    [Fact]
    public void Build_TruncationKeepsTheWorstFiles()
    {
        var files = new List<FileCoverage> { new("src/worst.cs", 0, 100) };
        files.AddRange(Enumerable.Range(0, CoverageSarif.MaxFileResults + 10)
            .Select(i => new FileCoverage($"src/mid{i:D4}.cs", 4, 10)));
        var summary = new CoverageSummary(0, 0, 0, 0, 0, 0, files);

        var results = CoverageSarif.Build(summary, 50).Runs[0].Results;

        Assert.Contains("src/worst.cs",
            results[0].Locations[0].PhysicalLocation.ArtifactLocation.Uri.OriginalString);
    }

    [Fact]
    public void BuildMetrics_OnlyEmitsDimensionsThatWereMeasured()
    {
        var linesOnly = new CoverageSummary(82, 100, 0, 0, 0, 0, []);

        var metrics = CoverageSarif.BuildMetrics(linesOnly);

        Assert.Equal(82d, metrics["coverage.line"]);
        Assert.DoesNotContain("coverage.branch", metrics.Keys);
        Assert.DoesNotContain("coverage.method", metrics.Keys);
    }

    [Fact]
    public void BuildMetrics_AllDimensions_AreRoundedPercentages()
    {
        var summary = new CoverageSummary(2, 3, 1, 3, 3, 4, []);

        var metrics = CoverageSarif.BuildMetrics(summary);

        Assert.Equal(66.67, metrics["coverage.line"]);
        Assert.Equal(33.33, metrics["coverage.branch"]);
        Assert.Equal(75d, metrics["coverage.method"]);
    }

    [Fact]
    public void BuildMetrics_NothingMeasured_EmitsNoCoverageKey()
    {
        Assert.Empty(CoverageSarif.BuildMetrics(CoverageSummary.Empty));
    }

    [Fact]
    public void Build_OutputPassesThePlatformSarifValidator()
    {
        var summary = new CoverageSummary(1, 10, 0, 0, 0, 0, [new FileCoverage("src/bad.cs", 1, 10)]);

        using var stream = new MemoryStream();
        Eaap.Sarif.SarifDocument.Save(CoverageSarif.Build(summary, 50), stream);
        stream.Position = 0;

        Assert.Empty(Eaap.Sarif.SarifValidator.Validate(stream));
    }
}
