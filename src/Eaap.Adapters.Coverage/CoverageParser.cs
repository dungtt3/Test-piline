using System.Globalization;
using System.Xml.Linq;

namespace Eaap.Adapters.Coverage;

/// <summary>Parses Cobertura XML (coverlet, JaCoCo-style) and lcov tracefiles.</summary>
public static class CoverageParser
{
    /// <summary>
    /// Parses a Cobertura report. Returns null when the document is not valid XML or its root is
    /// not &lt;coverage&gt;; <paramref name="problem"/> then explains why.
    /// </summary>
    public static CoverageSummary? TryParseCobertura(Stream xml, out string problem)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(xml);
        }
        catch (System.Xml.XmlException e)
        {
            problem = $"not valid XML ({e.Message})";
            return null;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "coverage")
        {
            problem = $"unrecognized root element <{root?.Name.LocalName ?? "none"}>, expected <coverage>";
            return null;
        }

        problem = string.Empty;

        var files = root.Descendants()
            .Where(e => e.Name.LocalName == "class" && !string.IsNullOrEmpty((string?)e.Attribute("filename")))
            .GroupBy(e => NormalizePath((string)e.Attribute("filename")!), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                // Partial classes share a filename; count each source line once, covered if any hit it.
                var hitsByLine = new Dictionary<int, int>();
                foreach (var line in group.SelectMany(c => c.Descendants().Where(e => e.Name.LocalName == "line")))
                {
                    if (!TryParseInt((string?)line.Attribute("number"), out var number))
                    {
                        continue;
                    }
                    TryParseInt((string?)line.Attribute("hits"), out var hits);
                    hitsByLine[number] = Math.Max(hitsByLine.GetValueOrDefault(number), hits);
                }

                return new FileCoverage(group.Key, hitsByLine.Count(p => p.Value > 0), hitsByLine.Count);
            })
            .Where(file => file.LinesValid > 0)
            .ToList();

        var methods = root.Descendants().Where(e => e.Name.LocalName == "method").ToList();
        var methodsValid = methods.Count;
        var methodsCovered = methods.Count(m => ParseRate((string?)m.Attribute("line-rate")) > 0);

        // Cobertura carries authoritative totals at the root; fall back to the per-file sums.
        var linesValid = ReadInt(root, "lines-valid") ?? files.Sum(f => f.LinesValid);
        var linesCovered = ReadInt(root, "lines-covered") ?? files.Sum(f => f.LinesCovered);

        return new CoverageSummary(
            linesCovered,
            linesValid,
            ReadInt(root, "branches-covered") ?? 0,
            ReadInt(root, "branches-valid") ?? 0,
            methodsCovered,
            methodsValid,
            files);
    }

    /// <summary>Parses an lcov tracefile. Returns null when no record was found at all.</summary>
    public static CoverageSummary? TryParseLcov(Stream stream, out string problem)
    {
        var files = new List<FileCoverage>();
        int linesCovered = 0, linesValid = 0, branchesCovered = 0, branchesValid = 0;
        int methodsCovered = 0, methodsValid = 0;

        string? currentFile = null;
        var hitsByLine = new Dictionary<int, int>();
        var sawRecord = false;

        using var reader = new StreamReader(stream, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                if (trimmed == "end_of_record")
                {
                    FlushRecord();
                }
                continue;
            }

            var tag = trimmed[..separator];
            var value = trimmed[(separator + 1)..];

            switch (tag)
            {
                case "SF":
                    FlushRecord();
                    currentFile = NormalizePath(value);
                    sawRecord = true;
                    break;
                case "DA":
                    var parts = value.Split(',');
                    if (parts.Length >= 2 && TryParseInt(parts[0], out var number))
                    {
                        TryParseInt(parts[1], out var hits);
                        hitsByLine[number] = Math.Max(hitsByLine.GetValueOrDefault(number), hits);
                    }
                    break;
                case "BRH":
                    branchesCovered += ParseIntOrZero(value);
                    break;
                case "BRF":
                    branchesValid += ParseIntOrZero(value);
                    break;
                case "FNH":
                    methodsCovered += ParseIntOrZero(value);
                    break;
                case "FNF":
                    methodsValid += ParseIntOrZero(value);
                    break;
            }
        }
        FlushRecord();

        if (!sawRecord)
        {
            problem = "no SF record found, not an lcov tracefile";
            return null;
        }

        problem = string.Empty;
        return new CoverageSummary(
            linesCovered, linesValid, branchesCovered, branchesValid, methodsCovered, methodsValid, files);

        // LF/LH are ignored on purpose: DA records are the ground truth and stay consistent
        // with the per-file numbers reported to the user.
        void FlushRecord()
        {
            if (currentFile is not null && hitsByLine.Count > 0)
            {
                var covered = hitsByLine.Count(p => p.Value > 0);
                files.Add(new FileCoverage(currentFile, covered, hitsByLine.Count));
                linesCovered += covered;
                linesValid += hitsByLine.Count;
            }
            currentFile = null;
            hitsByLine.Clear();
        }
    }

    /// <summary>Reports SARIF-friendly, workspace-relative paths regardless of the tool's separator.</summary>
    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        const string workspacePrefix = "workspace/";
        return normalized.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[workspacePrefix.Length..]
            : normalized;
    }

    private static int? ReadInt(XElement element, string attribute) =>
        TryParseInt((string?)element.Attribute(attribute), out var value) ? value : null;

    private static int ParseIntOrZero(string? value) => TryParseInt(value, out var result) ? result : 0;

    private static bool TryParseInt(string? value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static double ParseRate(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 0d;
}
