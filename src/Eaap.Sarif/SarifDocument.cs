using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.Sarif;

/// <summary>Thin wrapper over Sarif.Sdk for loading, saving and merging SARIF 2.1.0 logs.</summary>
public static class SarifDocument
{
    public static SarifLog Load(Stream stream) => SarifLog.Load(stream);

    public static void Save(SarifLog log, Stream stream) => log.Save(stream);

    /// <summary>Merges multiple SARIF logs into one log containing all runs.</summary>
    public static SarifLog Merge(IEnumerable<SarifLog> logs)
    {
        var merged = new SarifLog
        {
            Version = SarifVersion.Current,
            Runs = []
        };
        foreach (var log in logs)
        {
            if (log.Runs is null)
            {
                continue;
            }
            foreach (var run in log.Runs)
            {
                merged.Runs.Add(run);
            }
        }
        return merged;
    }
}
