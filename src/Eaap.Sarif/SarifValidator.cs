using System.Text.Json;

namespace Eaap.Sarif;

/// <summary>
/// Structural validation of SARIF logs per build spec section 6:
/// version must be 2.1.0, at least one run, every run needs tool.driver.name.
/// Works on raw JSON so malformed documents produce errors instead of exceptions.
/// </summary>
public static class SarifValidator
{
    public static IReadOnlyList<string> Validate(Stream stream)
    {
        var errors = new List<string>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream);
        }
        catch (JsonException e)
        {
            return [$"Invalid JSON: {e.Message}"];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ["Root element must be a JSON object."];
            }

            if (!root.TryGetProperty("version", out var version) || version.GetString() != "2.1.0")
            {
                errors.Add($"Unsupported SARIF version '{(root.TryGetProperty("version", out var v) ? v.ToString() : "<missing>")}', expected '2.1.0'.");
            }

            if (!root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0)
            {
                errors.Add("SARIF log must contain at least one run.");
            }
            else
            {
                var index = 0;
                foreach (var run in runs.EnumerateArray())
                {
                    if (!run.TryGetProperty("tool", out var tool)
                        || !tool.TryGetProperty("driver", out var driver)
                        || !driver.TryGetProperty("name", out var name)
                        || string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        errors.Add($"Run [{index}] is missing tool.driver.name.");
                    }
                    index++;
                }
            }
        }

        return errors;
    }
}
