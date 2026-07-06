using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eaap.Application;
using Microsoft.Extensions.Options;

namespace Eaap.Infrastructure.Argo;

/// <summary>Submits and inspects Argo workflows through the Argo server REST API.</summary>
public class ArgoClient : IArgoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ArgoOptions _options;
    private readonly MinioOptions _minioOptions;

    public ArgoClient(HttpClient httpClient, IOptions<ArgoOptions> options, IOptions<MinioOptions> minioOptions)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _minioOptions = minioOptions.Value;
        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        if (!string.IsNullOrEmpty(_options.Token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        }
    }

    public async Task<string> SubmitAnalysisWorkflowAsync(ArgoSubmitRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            resourceKind = "WorkflowTemplate",
            resourceName = "eaap-analysis-job",
            submitOptions = new
            {
                generateName = $"eaap-{request.AnalyzerId}-",
                labels = $"eaap.io/job-id={request.JobId},eaap.io/analyzer-run-id={request.AnalyzerRunId}",
                parameters = new[]
                {
                    $"job-id={request.JobId}",
                    $"analyzer-run-id={request.AnalyzerRunId}",
                    $"analyzer-id={request.AnalyzerId}",
                    $"adapter-image={request.AdapterImage}",
                    $"snapshot-key={request.SnapshotKey}",
                    $"commit-sha={request.CommitSha}",
                    $"minio-endpoint={_minioOptions.ClusterEndpoint}",
                    $"minio-access-key={_minioOptions.AccessKey}",
                    $"minio-secret-key={_minioOptions.SecretKey}",
                    $"minio-bucket={_minioOptions.Bucket}"
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/workflows/{_options.Namespace}/submit", payload, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var workflow = await response.Content.ReadFromJsonAsync<WorkflowResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Argo returned an empty workflow response.");
        return workflow.Metadata.Name;
    }

    public async Task<ArgoWorkflowStatus> GetWorkflowStatusAsync(string workflowName, CancellationToken ct = default)
    {
        var workflow = await _httpClient.GetFromJsonAsync<WorkflowResponse>(
            $"/api/v1/workflows/{_options.Namespace}/{workflowName}?fields=metadata.name,status.phase",
            JsonOptions, ct)
            ?? throw new InvalidOperationException($"Workflow {workflowName} not found.");
        return new ArgoWorkflowStatus(workflow.Status?.Phase ?? "Pending");
    }

    private sealed record WorkflowResponse(
        [property: JsonPropertyName("metadata")] WorkflowMetadata Metadata,
        [property: JsonPropertyName("status")] WorkflowStatus? Status);

    private sealed record WorkflowMetadata([property: JsonPropertyName("name")] string Name);

    private sealed record WorkflowStatus([property: JsonPropertyName("phase")] string? Phase);
}
