using System.Diagnostics;

namespace Eaap.IntegrationTests;

/// <summary>Creates a small throwaway git repository on disk to act as a clone source.</summary>
public static class GitFixture
{
    public static string CreateRepositoryWithFiles(IDictionary<string, string> files)
    {
        var path = Path.Combine(Path.GetTempPath(), "eaap-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        RunGit(path, "init --initial-branch=main");
        RunGit(path, "config user.email test@eaap.local");
        RunGit(path, "config user.name EaapTest");
        RunGit(path, "add .");
        RunGit(path, "commit -m fixture");
        return path;
    }

    public static string GetHeadSha(string repositoryPath) => RunGit(repositoryPath, "rev-parse HEAD").Trim();

    private static string RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");
        }
        return stdout;
    }
}
