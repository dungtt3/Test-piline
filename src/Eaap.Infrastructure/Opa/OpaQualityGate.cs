using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eaap.Application;
using Microsoft.Extensions.Options;

namespace Eaap.Infrastructure.Opa;

/// <summary>Evaluates the quality gate by querying OPA's data API.</summary>
public class OpaQualityGate(HttpClient httpClient, IOptions<OpaOptions> options) : IQualityGate
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GateResult> EvaluateAsync(GateSummary summary, CancellationToken ct = default)
    {
        var payload = new
        {
            input = new
            {
                summary = new
                {
                    errorCount = summary.ErrorCount,
                    warningCount = summary.WarningCount,
                    byRule = summary.ByRule
                },
                thresholds = new
                {
                    maxWarnings = options.Value.MaxWarnings
                }
            }
        };

        var response = await httpClient.PostAsJsonAsync("/v1/data/eaap/gate", payload, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OpaResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("OPA returned an empty response.");

        return new GateResult(
            body.Result?.Pass ?? false,
            options.Value.PolicyName,
            body.Result?.Errors ?? []);
    }

    private sealed record OpaResponse([property: JsonPropertyName("result")] OpaResult? Result);

    private sealed record OpaResult(
        [property: JsonPropertyName("pass")] bool Pass,
        [property: JsonPropertyName("errors")] string[]? Errors);
}
