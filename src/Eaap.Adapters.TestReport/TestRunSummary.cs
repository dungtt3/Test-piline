namespace Eaap.Adapters.TestReport;

/// <summary>A single failing test, carrying a location only when the report format provides one.</summary>
public record TestFailure(string TestName, string Message, string? FilePath = null, int? StartLine = null);

/// <summary>Aggregated outcome of one or more test report files.</summary>
public record TestRunSummary(
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    double DurationSeconds,
    IReadOnlyList<TestFailure> Failures)
{
    public static TestRunSummary Empty { get; } = new(0, 0, 0, 0, 0, []);

    /// <summary>Adds another report's numbers to this one; a job may produce several report files.</summary>
    public TestRunSummary Combine(TestRunSummary other) => new(
        Total + other.Total,
        Passed + other.Passed,
        Failed + other.Failed,
        Skipped + other.Skipped,
        DurationSeconds + other.DurationSeconds,
        [.. Failures, .. other.Failures]);
}
