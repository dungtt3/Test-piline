namespace Eaap.Domain;

public enum GitProvider
{
    GitHub,
    GitLab,
    Bitbucket,
    AzureDevOps,
    GenericGit
}

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    GateFailed
}

public enum AnalyzerRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

public enum WarningLevel
{
    None,
    Note,
    Warning,
    Error
}
