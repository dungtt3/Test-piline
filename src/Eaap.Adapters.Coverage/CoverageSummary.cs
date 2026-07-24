namespace Eaap.Adapters.Coverage;

/// <summary>Per-file line coverage, used to flag poorly covered files as warnings.</summary>
public record FileCoverage(string Path, int LinesCovered, int LinesValid)
{
    /// <summary>Line coverage percentage; a file with no measurable line counts as fully covered.</summary>
    public double LineRate => LinesValid == 0 ? 100d : LinesCovered * 100d / LinesValid;

    public FileCoverage Combine(FileCoverage other) =>
        new(Path, LinesCovered + other.LinesCovered, LinesValid + other.LinesValid);
}

/// <summary>
/// Aggregated coverage across one or more reports. Rates are always derived from the summed
/// counters — averaging the per-report percentages would weight a 10-line file the same as a
/// 10 000-line one (build spec phase 2 section 5).
/// </summary>
public record CoverageSummary(
    int LinesCovered,
    int LinesValid,
    int BranchesCovered,
    int BranchesValid,
    int MethodsCovered,
    int MethodsValid,
    IReadOnlyList<FileCoverage> Files)
{
    public static CoverageSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, []);

    public bool HasAnyMeasurement => LinesValid > 0 || BranchesValid > 0 || MethodsValid > 0;

    public double? LineRate => LinesValid == 0 ? null : LinesCovered * 100d / LinesValid;
    public double? BranchRate => BranchesValid == 0 ? null : BranchesCovered * 100d / BranchesValid;
    public double? MethodRate => MethodsValid == 0 ? null : MethodsCovered * 100d / MethodsValid;

    /// <summary>Sums counters and merges per-file entries by path, so a file split across two reports counts once.</summary>
    public CoverageSummary Combine(CoverageSummary other)
    {
        var files = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files.Concat(other.Files))
        {
            files[file.Path] = files.TryGetValue(file.Path, out var existing)
                ? existing.Combine(file)
                : file;
        }

        return new CoverageSummary(
            LinesCovered + other.LinesCovered,
            LinesValid + other.LinesValid,
            BranchesCovered + other.BranchesCovered,
            BranchesValid + other.BranchesValid,
            MethodsCovered + other.MethodsCovered,
            MethodsValid + other.MethodsValid,
            [.. files.Values]);
    }
}
