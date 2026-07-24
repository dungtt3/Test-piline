using System.Text.Json.Serialization;

namespace Eaap.Adapters.PrometheusSlo;

/// <summary>One SLO to evaluate against Prometheus (from the repo's .eaap/config.yaml runtime section).</summary>
public record SloDefinition
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("query")] public string Query { get; init; } = string.Empty;

    /// <summary>The healthy condition operator: the SLO holds when observed &lt;op&gt; threshold.</summary>
    [JsonPropertyName("operator")] public string Operator { get; init; } = "<";
    [JsonPropertyName("threshold")] public double Threshold { get; init; }

    /// <summary>SARIF level for a violation: error | warning | note.</summary>
    [JsonPropertyName("level")] public string Level { get; init; } = "error";
}

/// <summary>The runtime config the platform serializes into EAAP_RUNTIME_CONFIG.</summary>
public record RuntimeConfig
{
    [JsonPropertyName("prometheusUrl")] public string PrometheusUrl { get; init; } = string.Empty;
    [JsonPropertyName("window")] public string Window { get; init; } = "1h";
    [JsonPropertyName("slos")] public List<SloDefinition> Slos { get; init; } = [];
}

/// <summary>The measured value of an SLO query, and whether the healthy condition held.</summary>
public record SloResult(SloDefinition Slo, double ObservedValue, bool Violated);
