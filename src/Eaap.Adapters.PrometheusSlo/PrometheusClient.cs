using System.Globalization;
using System.Text.Json;

namespace Eaap.Adapters.PrometheusSlo;

/// <summary>Runs Prometheus instant queries and returns the first scalar/vector value.</summary>
public class PrometheusClient(HttpClient httpClient, string baseUrl)
{
    public async Task<double> QueryAsync(string query, CancellationToken ct = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/v1/query?query={Uri.EscapeDataString(query)}";
        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;

        if (root.GetProperty("status").GetString() != "success")
        {
            throw new InvalidOperationException($"Prometheus query failed: {root}");
        }

        var data = root.GetProperty("data");
        var resultType = data.GetProperty("resultType").GetString();
        var result = data.GetProperty("result");

        // Scalar: data.result = [ <ts>, "<value>" ]. Vector: data.result = [ { value: [ts, "val"] }, ... ].
        var valueElement = resultType == "scalar"
            ? result[1]
            : result.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.Undefined
                ? throw new InvalidOperationException($"Prometheus returned no series for query: {query}")
                : result[0].GetProperty("value")[1];

        return double.Parse(valueElement.GetString() ?? "0", CultureInfo.InvariantCulture);
    }
}
