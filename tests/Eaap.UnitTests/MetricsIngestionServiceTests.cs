using System.Text;
using System.Text.Json;
using Eaap.Infrastructure.Ingestion;

namespace Eaap.UnitTests;

public class MetricsIngestionServiceTests
{
    private static MemoryStream StreamOf(string json) => new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_RealFixture_ReadsEveryStandardKey()
    {
        using var stream = File.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "metrics-valid.json"));

        var metrics = MetricsIngestionService.Parse(stream);

        Assert.Equal(7, metrics.Count);
        Assert.Equal(240, metrics["tests.total"]);
        Assert.Equal(2, metrics["tests.failed"]);
        Assert.Equal(5, metrics["tests.skipped"]);
        Assert.Equal(41.7, metrics["tests.durationSeconds"]);
        Assert.Equal(82.5, metrics["coverage.line"]);
    }

    [Fact]
    public void Parse_MissingMetricsProperty_ReturnsEmpty()
    {
        using var stream = StreamOf("""{"somethingElse": 1}""");

        Assert.Empty(MetricsIngestionService.Parse(stream));
    }

    [Fact]
    public void Parse_MetricsIsNotAnObject_ReturnsEmpty()
    {
        using var stream = StreamOf("""{"metrics": [1, 2, 3]}""");

        Assert.Empty(MetricsIngestionService.Parse(stream));
    }

    [Fact]
    public void Parse_RootIsNotAnObject_ReturnsEmpty()
    {
        using var stream = StreamOf("""[{"metrics": {"a": 1}}]""");

        Assert.Empty(MetricsIngestionService.Parse(stream));
    }

    [Fact]
    public void Parse_SkipsNonNumericEntriesButKeepsNumericOnes()
    {
        using var stream = StreamOf("""
            {"metrics": {
                "coverage.line": 80.5,
                "coverage.note": "not a number",
                "tests.total": null,
                "tests.enabled": true,
                "tests.failed": 0
            }}
            """);

        var metrics = MetricsIngestionService.Parse(stream);

        Assert.Equal(2, metrics.Count);
        Assert.Equal(80.5, metrics["coverage.line"]);
        Assert.Equal(0, metrics["tests.failed"]);
        Assert.DoesNotContain("coverage.note", metrics.Keys);
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsSoTheCallerCanDecideToContinue()
    {
        // The consumer catches this and keeps the run Succeeded: metrics are advisory,
        // only SARIF decides an analyzer run's status.
        using var stream = StreamOf("this is not json");

        Assert.ThrowsAny<JsonException>(() => MetricsIngestionService.Parse(stream));
    }
}
