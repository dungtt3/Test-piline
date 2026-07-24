using System.Security.Cryptography;
using System.Text;

namespace Eaap.Sarif;

/// <summary>
/// Dedup fingerprint per build spec section 6:
/// SHA256(lowercase(ruleId | normalizedPath | startLine-or-0 | first 80 chars of message)).
/// </summary>
public static class WarningFingerprint
{
    /// <summary>
    /// Stable fingerprint for findings that have no file/line and a value-bearing message (e.g. runtime
    /// SLO warnings): SHA256(ruleId | fingerprintKey). An adapter opts in via properties.fingerprintKey
    /// so the fingerprint stays constant across runs even though the observed value changes each time.
    /// </summary>
    public static string ComputeFromKey(string? ruleId, string fingerprintKey)
    {
        var input = string.Join('|', ruleId ?? string.Empty, fingerprintKey).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Compute(string? ruleId, string? filePath, int? startLine, string? messageText)
    {
        var message = messageText ?? string.Empty;
        var first80 = message.Length > 80 ? message[..80] : message;
        var input = string.Join('|',
            ruleId ?? string.Empty,
            NormalizePath(filePath),
            (startLine?.ToString() ?? "0"),
            first80).ToLowerInvariant();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Normalizes to a "/"-separated path relative to the /workspace mount.</summary>
    public static string NormalizePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        var normalized = filePath.Replace('\\', '/');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            normalized = uri.AbsolutePath;
        }

        normalized = normalized.TrimStart('/');
        const string workspacePrefix = "workspace/";
        if (normalized.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[workspacePrefix.Length..];
        }

        return normalized;
    }
}
