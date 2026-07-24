using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Eaap.Application;
using Eaap.Infrastructure.RepoConfig;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eaap.UnitTests;

public class RepoConfigReaderTests
{
    private const string FullConfig = """
        test:
          enabled: true
          image: mcr.microsoft.com/dotnet/sdk:8.0
          command: >
            dotnet test --logger trx
            --results-directory /workspace/.eaap/test-results
            /p:CollectCoverage=true
        """;

    [Fact]
    public void Parse_SpecExample_ReadsTestStepAndFoldsCommandOntoOneLine()
    {
        var config = RepoConfigReader.Parse(FullConfig);

        Assert.True(config.RunsTests);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:8.0", config.Test!.Image);
        Assert.DoesNotContain('\n', config.Test.Command!);
        Assert.Contains("dotnet test --logger trx", config.Test.Command);
        Assert.Contains("--results-directory /workspace/.eaap/test-results", config.Test.Command);
    }

    [Fact]
    public void Parse_NoTestSection_YieldsNoConfig()
    {
        var config = RepoConfigReader.Parse("someOtherKey: 1\n");

        Assert.False(config.RunsTests);
        Assert.Null(config.Test);
    }

    [Fact]
    public void Parse_TestDisabled_DoesNotRunTests()
    {
        var config = RepoConfigReader.Parse("test:\n  enabled: false\n  image: alpine\n  command: echo hi\n");

        Assert.False(config.RunsTests);
        Assert.NotNull(config.Test);
    }

    [Fact]
    public void Parse_EnabledButMissingCommand_IsNotRunnable()
    {
        // Half a configuration is a configuration error, not something to guess at.
        var config = RepoConfigReader.Parse("test:\n  enabled: true\n  image: alpine\n");

        Assert.False(config.RunsTests);
    }

    [Fact]
    public void Parse_MalformedYaml_DegradesToNoConfig()
    {
        var config = RepoConfigReader.Parse("test:\n  enabled: true\n   image: broken-indent\n\tmore: bad\n");

        Assert.False(config.RunsTests);
    }

    [Fact]
    public async Task ReadAsync_FindsConfigInsideTheSnapshotTarball()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.DownloadAsync("snapshots/x.tar.gz", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(BuildTarball(("./.eaap/config.yaml", FullConfig))));
        var reader = new RepoConfigReader(storage, NullLogger<RepoConfigReader>.Instance);

        var config = await reader.ReadAsync("snapshots/x.tar.gz");

        Assert.True(config.RunsTests);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:8.0", config.Test!.Image);
    }

    [Fact]
    public async Task ReadAsync_FindsConfigWrittenWithoutTheDotSlashPrefix()
    {
        // SnapshotService writes relative paths like ".eaap/config.yaml"; naive prefix trimming
        // would strip the leading dot of ".eaap" and silently never match.
        var storage = Substitute.For<IObjectStorage>();
        storage.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(BuildTarball((".eaap/config.yaml", FullConfig))));
        var reader = new RepoConfigReader(storage, NullLogger<RepoConfigReader>.Instance);

        Assert.True((await reader.ReadAsync("snapshots/x.tar.gz")).RunsTests);
    }

    [Fact]
    public async Task ReadAsync_SnapshotWithoutConfig_YieldsNoConfig()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(BuildTarball(("README.md", "# hello"))));
        var reader = new RepoConfigReader(storage, NullLogger<RepoConfigReader>.Instance);

        Assert.False((await reader.ReadAsync("snapshots/x.tar.gz")).RunsTests);
    }

    [Fact]
    public async Task ReadAsync_StorageFailure_DoesNotThrow()
    {
        // A snapshot we cannot read must not stop the analysis from being submitted.
        var storage = Substitute.For<IObjectStorage>();
        storage.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<Stream>>(_ => throw new InvalidOperationException("minio down"));
        var reader = new RepoConfigReader(storage, NullLogger<RepoConfigReader>.Instance);

        Assert.Equal(EaapRepoConfig.None, await reader.ReadAsync("snapshots/x.tar.gz"));
    }

    /// <summary>Builds a .tar.gz shaped like SnapshotService writes them.</summary>
    private static MemoryStream BuildTarball(params (string Path, string Content)[] entries)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (path, content) in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(bytes)
                });
            }
        }
        output.Position = 0;
        return output;
    }
}
