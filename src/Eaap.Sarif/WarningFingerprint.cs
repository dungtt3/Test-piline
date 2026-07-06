using System.Security.Cryptography;
using System.Text;

namespace Eaap.Sarif;

/// <summary>
/// Dedup fingerprint per build spec section 6:
/// SHA256(lowercase(ruleId | normalizedPath | startLine-or-0 | first 80 chars of message)).
/// </summary>
public static class WarningFingerprint
{
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
