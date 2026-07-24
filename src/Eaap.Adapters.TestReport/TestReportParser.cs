using System.Globalization;
using System.Xml.Linq;

namespace Eaap.Adapters.TestReport;

/// <summary>
/// Parses TRX (<c>dotnet test --logger trx</c>) and JUnit XML (pytest, jest, surefire…) reports.
/// Counts come from the individual test elements rather than the summary attributes, because
/// those attributes are inconsistent across the tools that emit JUnit XML.
/// </summary>
public static class TestReportParser
{
    /// <summary>
    /// Parses one report file. Returns null when the content is not valid XML, or when its root
    /// is neither a TRX nor a JUnit document; <paramref name="problem"/> then says why so the
    /// caller can log it and move on instead of failing the whole run.
    /// </summary>
    public static TestRunSummary? TryParse(Stream xml, out string problem)
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
        if (root is null)
        {
            problem = "empty XML document";
            return null;
        }

        problem = string.Empty;
        return root.Name.LocalName switch
        {
            "TestRun" => ParseTrx(root),
            "testsuites" or "testsuite" => ParseJUnit(root),
            _ => Unrecognized(root, out problem)
        };
    }

    private static TestRunSummary? Unrecognized(XElement root, out string problem)
    {
        problem = $"unrecognized root element <{root.Name.LocalName}>, expected TestRun, testsuites or testsuite";
        return null;
    }

    private static TestRunSummary ParseTrx(XElement root)
    {
        var results = Descendants(root, "UnitTestResult").ToList();
        var failures = new List<TestFailure>();
        int passed = 0, failed = 0, skipped = 0;
        var duration = 0d;

        foreach (var result in results)
        {
            var outcome = (string?)result.Attribute("outcome") ?? string.Empty;
            duration += ParseTimeSpanSeconds((string?)result.Attribute("duration"));

            switch (outcome)
            {
                case "Passed":
                    passed++;
                    break;
                // NotExecuted is what TRX calls a skipped test.
                case "NotExecuted":
                    skipped++;
                    break;
                default:
                    // Failed, Timeout, Aborted, Inconclusive — all count as failing.
                    failed++;
                    failures.Add(new TestFailure(
                        (string?)result.Attribute("testName") ?? "(unnamed test)",
                        TrxFailureMessage(result)));
                    break;
            }
        }

        return new TestRunSummary(results.Count, passed, failed, skipped, duration, failures);
    }

    private static string TrxFailureMessage(XElement result)
    {
        var errorInfo = Descendants(result, "ErrorInfo").FirstOrDefault();
        var message = Descendants(errorInfo ?? result, "Message").FirstOrDefault()?.Value?.Trim();
        return string.IsNullOrEmpty(message) ? "test failed without a reported message" : message;
    }

    private static TestRunSummary ParseJUnit(XElement root)
    {
        var cases = Descendants(root, "testcase").ToList();
        var failures = new List<TestFailure>();
        int passed = 0, failed = 0, skipped = 0;
        var duration = 0d;

        foreach (var testCase in cases)
        {
            duration += ParseSeconds((string?)testCase.Attribute("time"));

            // <error> is a JUnit failure too (crashed rather than asserted).
            var failureElement = Descendants(testCase, "failure").FirstOrDefault()
                ?? Descendants(testCase, "error").FirstOrDefault();

            if (failureElement is not null)
            {
                failed++;
                failures.Add(new TestFailure(
                    JUnitTestName(testCase),
                    JUnitFailureMessage(failureElement),
                    (string?)testCase.Attribute("file"),
                    ParseLine((string?)testCase.Attribute("line"))));
            }
            else if (Descendants(testCase, "skipped").Any())
            {
                skipped++;
            }
            else
            {
                passed++;
            }
        }

        return new TestRunSummary(cases.Count, passed, failed, skipped, duration, failures);
    }

    private static string JUnitTestName(XElement testCase)
    {
        var name = (string?)testCase.Attribute("name") ?? "(unnamed test)";
        var className = (string?)testCase.Attribute("classname");
        return string.IsNullOrEmpty(className) ? name : $"{className}.{name}";
    }

    private static string JUnitFailureMessage(XElement failureElement)
    {
        var message = (string?)failureElement.Attribute("message");
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message.Trim();
        }

        var body = failureElement.Value?.Trim();
        return string.IsNullOrEmpty(body) ? "test failed without a reported message" : body;
    }

    /// <summary>Matches on local name so both namespaced (TRX) and bare (JUnit) documents work.</summary>
    private static IEnumerable<XElement> Descendants(XElement element, string localName) =>
        element.Descendants().Where(e => e.Name.LocalName == localName);

    private static double ParseSeconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0d;

    /// <summary>TRX durations are "HH:MM:SS.fffffff" rather than a plain number of seconds.</summary>
    private static double ParseTimeSpanSeconds(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) ? span.TotalSeconds : 0d;

    private static int? ParseLine(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) && line > 0 ? line : null;
}
