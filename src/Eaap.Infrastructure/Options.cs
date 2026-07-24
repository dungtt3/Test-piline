namespace Eaap.Infrastructure;

public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public Uri ToUri() => new($"amqp://{Username}:{Password}@{Host}:{Port}/");
}

public class MinioOptions
{
    public const string SectionName = "Minio";
    public string Endpoint { get; set; } = "http://localhost:9000";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = "eaap";

    private string? _clusterEndpoint;

    /// <summary>Endpoint reachable from inside the k3d cluster (e.g. http://host.k3d.internal:9000). Falls back to Endpoint.</summary>
    public string ClusterEndpoint
    {
        get => string.IsNullOrEmpty(_clusterEndpoint) ? Endpoint : _clusterEndpoint;
        set => _clusterEndpoint = value;
    }
}

public class OpaOptions
{
    public const string SectionName = "Opa";
    public string BaseUrl { get; set; } = "http://localhost:8181";
    public string PolicyName { get; set; } = "quality-gate/default";
    public int MaxWarnings { get; set; } = 100;

    // New phase 2 thresholds. Defaults keep the gate lenient so upgrading a phase 1
    // deployment does not suddenly start failing: a repo relaxes or tightens these per-repo
    // via GatePolicyBinding. MaxTestsFailed=0 is the one strict default, and it only bites
    // when the repo actually ran tests (the tests.failed metric exists).
    public int MaxNewWarnings { get; set; } = -1;      // negative = unlimited
    public double MinCoverageLine { get; set; } = 0;   // 0 = no coverage floor
    public int MaxTestsFailed { get; set; } = 0;
}

public class ArgoOptions
{
    public const string SectionName = "Argo";
    public string BaseUrl { get; set; } = "http://localhost:2746";
    public string Namespace { get; set; } = "argo";
    public string Token { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 5;
}

/// <summary>Static adapter registry for Phase 1, mirroring each adapter's manifest.yaml.</summary>
public class AdapterOptions
{
    public const string SectionName = "Adapters";
    public Dictionary<string, AdapterEntry> Registry { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class AdapterEntry
{
    public string Image { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 1800;

    /// <summary>Mirrors manifest.yaml category: lint|test|coverage|security|dependency|runtime.</summary>
    public string Category { get; set; } = string.Empty;

    public bool IsSecurity => string.Equals(Category, "security", StringComparison.OrdinalIgnoreCase);
}
