namespace Eaap.Application;

/// <summary>
/// Optional test step declared by a repository in <c>.eaap/config.yaml</c>.
/// The workflow only runs tests when the repo asked for it and said how.
/// </summary>
public record RepoTestConfig(bool Enabled, string? Image, string? Command)
{
    public bool IsRunnable =>
        Enabled && !string.IsNullOrWhiteSpace(Image) && !string.IsNullOrWhiteSpace(Command);
}

/// <summary>
/// The <c>.eaap/config.yaml</c> convention file read from a repository snapshot.
/// A repository without the file simply gets <see cref="None"/> and no test step.
/// </summary>
public record EaapRepoConfig(RepoTestConfig? Test)
{
    public static EaapRepoConfig None { get; } = new(Test: null);

    public bool RunsTests => Test?.IsRunnable == true;
}

/// <summary>Reads a repository's EAAP configuration out of its snapshot tarball.</summary>
public interface IRepoConfigReader
{
    /// <summary>
    /// Returns <see cref="EaapRepoConfig.None"/> when the snapshot has no config file or the
    /// file cannot be parsed — a broken config must not block analysis.
    /// </summary>
    Task<EaapRepoConfig> ReadAsync(string snapshotStorageKey, CancellationToken ct = default);
}
