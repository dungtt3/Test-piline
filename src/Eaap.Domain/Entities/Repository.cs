namespace Eaap.Domain.Entities;

public class Repository
{
    public Guid Id { get; set; }
    public GitProvider Provider { get; set; }
    public string CloneUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public DateTimeOffset CreatedAt { get; set; }
}
