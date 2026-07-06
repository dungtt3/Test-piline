namespace Eaap.Application;

public record ArgoSubmitRequest(
    Guid JobId,
    Guid AnalyzerRunId,
    string AnalyzerId,
    string AdapterImage,
    string SnapshotKey,
    string CommitSha);

/// <summary>Argo workflow phase: Pending, Running, Succeeded, Failed, Error.</summary>
public record ArgoWorkflowStatus(string Phase)
{
    public bool IsFinished => Phase is "Succeeded" or "Failed" or "Error";
    public bool IsSucceeded => Phase == "Succeeded";
}

/// <summary>Argo Workflows REST client.</summary>
public interface IArgoClient
{
    /// <summary>Submits the analysis WorkflowTemplate and returns the created workflow name.</summary>
    Task<string> SubmitAnalysisWorkflowAsync(ArgoSubmitRequest request, CancellationToken ct = default);

    Task<ArgoWorkflowStatus> GetWorkflowStatusAsync(string workflowName, CancellationToken ct = default);
}
