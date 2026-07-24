using Eaap.Sarif;

namespace Eaap.UnitTests;

public class WarningFingerprintTests
{
    [Fact]
    public void Compute_IsDeterministic()
    {
        var first = WarningFingerprint.Compute("semi", "src/index.js", 1, "Missing semicolon.");
        var second = WarningFingerprint.Compute("semi", "src/index.js", 1, "Missing semicolon.");
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length); // SHA256 hex
    }

    [Fact]
    public void ComputeFromKey_IsStable_AcrossChangingMessages()
    {
        // A runtime SLO warning's message carries the observed value, which changes every run;
        // the fingerprintKey (the SLO id) keeps the fingerprint constant so the baseline works.
        var a = WarningFingerprint.ComputeFromKey("slo.latency-p95", "latency-p95");
        var b = WarningFingerprint.ComputeFromKey("slo.latency-p95", "latency-p95");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void ComputeFromKey_DiffersByRuleAndKey()
    {
        var baseFp = WarningFingerprint.ComputeFromKey("slo.latency-p95", "latency-p95");
        Assert.NotEqual(baseFp, WarningFingerprint.ComputeFromKey("slo.error-rate", "latency-p95"));
        Assert.NotEqual(baseFp, WarningFingerprint.ComputeFromKey("slo.latency-p95", "error-rate"));
    }

    [Fact]
    public void ComputeFromKey_DiffersFromPathBasedFormula()
    {
        // The two formulas must not collide for the same ruleId.
        Assert.NotEqual(
            WarningFingerprint.Compute("slo.latency-p95", null, null, "latency-p95"),
            WarningFingerprint.ComputeFromKey("slo.latency-p95", "latency-p95"));
    }

    [Fact]
    public void Compute_IsCaseInsensitive()
    {
        var lower = WarningFingerprint.Compute("semi", "src/index.js", 1, "missing semicolon.");
        var upper = WarningFingerprint.Compute("SEMI", "SRC/INDEX.JS", 1, "MISSING SEMICOLON.");
        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Compute_UsesOnlyFirst80MessageChars()
    {
        var prefix = new string('m', 80);
        var a = WarningFingerprint.Compute("rule", "a.js", 5, prefix + " tail one");
        var b = WarningFingerprint.Compute("rule", "a.js", 5, prefix + " different tail");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_MissingStartLine_TreatedAsZero()
    {
        var withNull = WarningFingerprint.Compute("rule", "a.js", null, "msg");
        var withZero = WarningFingerprint.Compute("rule", "a.js", 0, "msg");
        Assert.Equal(withNull, withZero);
    }

    [Theory]
    [InlineData("src/index.js", "src/index.js")]
    [InlineData("src\\index.js", "src/index.js")]
    [InlineData("/workspace/src/index.js", "src/index.js")]
    [InlineData("workspace/src/index.js", "src/index.js")]
    [InlineData("file:///workspace/src/index.js", "src/index.js")]
    [InlineData(null, "")]
    public void NormalizePath_ProducesWorkspaceRelativeForwardSlashes(string? input, string expected)
    {
        Assert.Equal(expected, WarningFingerprint.NormalizePath(input));
    }

    [Fact]
    public void Compute_DifferentInputs_ProduceDifferentHashes()
    {
        var a = WarningFingerprint.Compute("rule-a", "a.js", 1, "msg");
        var b = WarningFingerprint.Compute("rule-b", "a.js", 1, "msg");
        Assert.NotEqual(a, b);
    }
}
