using System.Text.RegularExpressions;
using Eaap.Domain;
using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.Sarif;

/// <summary>The security classification derived from a SARIF result (build spec phase 3 section 4).</summary>
public record SecurityInfo(SecuritySeverity Severity, string? Cve, string? Cwe)
{
    public static SecurityInfo None { get; } = new(SecuritySeverity.None, null, null);
}

/// <summary>
/// Maps a SARIF result to a security severity, CVE and CWE. Trivy/Semgrep expose a numeric
/// security-severity (CVSS); other security scanners only have a SARIF level, which is mapped
/// for adapters declared as <c>category: security</c>. Non-security adapters stay None.
/// </summary>
public static partial class SecurityEnricher
{
    public static SecurityInfo Enrich(Result result, Run run, bool isSecurityCategory)
    {
        var rule = ResolveRule(result, run);
        var severity = Severity(result, rule, isSecurityCategory);
        var haystack = BuildHaystack(result, rule);
        return new SecurityInfo(severity, MatchCve(haystack), MatchCwe(haystack));
    }

    private static SecuritySeverity Severity(Result result, ReportingDescriptor? rule, bool isSecurityCategory)
    {
        // 1. Explicit CVSS score wins, wherever the tool put it (result or rule properties).
        if (TryGetSecuritySeverity(result, rule, out var cvss))
        {
            return FromCvss(cvss);
        }

        // 2. Otherwise map the SARIF level, but only for security-category adapters.
        if (!isSecurityCategory)
        {
            return SecuritySeverity.None;
        }

        return result.Level switch
        {
            FailureLevel.Error => SecuritySeverity.High,
            FailureLevel.Warning => SecuritySeverity.Medium,
            _ => SecuritySeverity.Low
        };
    }

    /// <summary>CVSS bands per spec: 0 None, 0.1-3.9 Low, 4-6.9 Medium, 7-8.9 High, 9-10 Critical.</summary>
    public static SecuritySeverity FromCvss(double cvss) => cvss switch
    {
        <= 0 => SecuritySeverity.None,
        < 4.0 => SecuritySeverity.Low,
        < 7.0 => SecuritySeverity.Medium,
        < 9.0 => SecuritySeverity.High,
        _ => SecuritySeverity.Critical
    };

    private static bool TryGetSecuritySeverity(Result result, ReportingDescriptor? rule, out double cvss)
    {
        cvss = 0;
        var raw = GetStringProperty(result, "security-severity")
            ?? GetStringProperty(rule, "security-severity");
        return raw is not null
            && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out cvss);
    }

    private static string? MatchCve(string haystack)
    {
        var match = CveRegex().Match(haystack);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string? MatchCwe(string haystack)
    {
        var match = CweRegex().Match(haystack);
        return match.Success ? $"CWE-{match.Groups[1].Value}" : null;
    }

    /// <summary>Everything a CVE/CWE could hide in: rule id, message, tags, and taxa.</summary>
    private static string BuildHaystack(Result result, ReportingDescriptor? rule)
    {
        var parts = new List<string?>
        {
            result.RuleId,
            result.Rule?.Id,
            result.Message?.Text,
            rule?.Id,
            rule?.Name
        };

        AppendTags(parts, result);
        AppendTags(parts, rule);

        foreach (var taxon in result.Taxa ?? [])
        {
            parts.Add(taxon.Id);
        }
        foreach (var relationship in rule?.Relationships ?? [])
        {
            parts.Add(relationship.Target?.Id);
        }

        return string.Join('\n', parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    private static void AppendTags(List<string?> parts, PropertyBagHolder? holder)
    {
        if (holder is null)
        {
            return;
        }
        try
        {
            if (holder.TryGetProperty("tags", out string[]? tags) && tags is not null)
            {
                parts.AddRange(tags);
            }
        }
        catch
        {
            // tags is not a string array on this tool; ignore.
        }
    }

    private static ReportingDescriptor? ResolveRule(Result result, Run run)
    {
        var rules = run.Tool?.Driver?.Rules;
        if (rules is null)
        {
            return null;
        }

        // Prefer the explicit index, fall back to matching by id.
        var index = result.Rule?.Index ?? result.RuleIndex;
        if (index >= 0 && index < rules.Count)
        {
            return rules[index];
        }

        var id = result.RuleId ?? result.Rule?.Id;
        return id is null ? null : rules.FirstOrDefault(r => r.Id == id);
    }

    private static string? GetStringProperty(PropertyBagHolder? holder, string key)
    {
        if (holder is null)
        {
            return null;
        }
        try
        {
            // security-severity is a JSON string like "9.8".
            return holder.TryGetProperty(key, out string? value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"CVE-\d{4}-\d+", RegexOptions.IgnoreCase)]
    private static partial Regex CveRegex();

    [GeneratedRegex(@"CWE-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CweRegex();
}
