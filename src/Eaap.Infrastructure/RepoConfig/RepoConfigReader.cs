using System.Formats.Tar;
using System.IO.Compression;
using Eaap.Application;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Eaap.Infrastructure.RepoConfig;

/// <summary>
/// Reads <c>.eaap/config.yaml</c> straight out of the snapshot tarball on object storage,
/// so no extra clone is needed to find out whether the repository wants a test step.
/// </summary>
public class RepoConfigReader(IObjectStorage storage, ILogger<RepoConfigReader> logger) : IRepoConfigReader
{
    public const string ConfigEntryPath = ".eaap/config.yaml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<EaapRepoConfig> ReadAsync(string snapshotStorageKey, CancellationToken ct = default)
    {
        try
        {
            await using var tarball = await storage.DownloadAsync(snapshotStorageKey, ct);
            var yaml = await ExtractConfigAsync(tarball, ct);
            if (yaml is null)
            {
                return EaapRepoConfig.None;
            }

            return Parse(yaml);
        }
        catch (Exception e)
        {
            // A missing or unreadable config must never block analysis.
            logger.LogWarning(e, "Could not read {ConfigPath} from snapshot {Key}; continuing without it",
                ConfigEntryPath, snapshotStorageKey);
            return EaapRepoConfig.None;
        }
    }

    /// <summary>Parses the YAML document; malformed content degrades to no configuration.</summary>
    public static EaapRepoConfig Parse(string yaml)
    {
        try
        {
            var document = Deserializer.Deserialize<ConfigDocument>(yaml);
            if (document?.Test is null)
            {
                return EaapRepoConfig.None;
            }

            return new EaapRepoConfig(new RepoTestConfig(
                document.Test.Enabled,
                Clean(document.Test.Image),
                Clean(document.Test.Command)));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return EaapRepoConfig.None;
        }
    }

    /// <summary>Folded YAML scalars keep trailing newlines; collapse them into a single command line.</summary>
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : string.Join(' ', value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static async Task<string?> ExtractConfigAsync(Stream tarball, CancellationToken ct)
    {
        await using var gzip = new GZipStream(tarball, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);

        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (!IsConfigEntry(entry.Name) || entry.DataStream is null)
            {
                continue;
            }

            using var text = new StreamReader(entry.DataStream);
            return await text.ReadToEndAsync(ct);
        }

        return null;
    }

    /// <summary>
    /// Tar entries may be written as "./.eaap/config.yaml"; match either spelling. Only the
    /// "./" prefix is stripped — trimming a '.' character set would also eat the dot of ".eaap".
    /// </summary>
    private static bool IsConfigEntry(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        return normalized.TrimStart('/').Equals(ConfigEntryPath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ConfigDocument
    {
        public TestDocument? Test { get; set; }
    }

    private sealed class TestDocument
    {
        public bool Enabled { get; set; }
        public string? Image { get; set; }
        public string? Command { get; set; }
    }
}
