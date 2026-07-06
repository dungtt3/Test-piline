using System.Diagnostics;
using Eaap.Application;
using Microsoft.Extensions.Logging;

namespace Eaap.Infrastructure.Git;

/// <summary>Clones repositories by shelling out to the git CLI.</summary>
public class GitCliClient(ILogger<GitCliClient> logger) : IGitClient
{
    public async Task<GitCloneResult> CloneAsync(string cloneUrl, string branch, string? commitSha, CancellationToken ct = default)
    {
        var localPath = Path.Combine(Path.GetTempPath(), "eaap-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localPath);

        if (string.IsNullOrEmpty(commitSha))
        {
            // Latest commit of the branch: a shallow clone is enough.
            await RunGitAsync($"clone --depth 1 --branch \"{branch}\" -- \"{cloneUrl}\" \"{localPath}\"", null, ct);
        }
        else
        {
            // Specific commit: fetch it directly and check it out.
            await RunGitAsync("init", localPath, ct);
            await RunGitAsync($"remote add origin \"{cloneUrl}\"", localPath, ct);
            await RunGitAsync($"fetch --depth 1 origin {commitSha}", localPath, ct);
            await RunGitAsync("checkout --detach FETCH_HEAD", localPath, ct);
        }

        var resolvedSha = (await RunGitAsync("rev-parse HEAD", localPath, ct)).Trim();
        logger.LogInformation("Cloned {CloneUrl}@{Sha} into {Path}", cloneUrl, resolvedSha, localPath);
        return new GitCloneResult(localPath, resolvedSha);
    }

    private static async Task<string> RunGitAsync(string arguments, string? workingDirectory, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }
}
