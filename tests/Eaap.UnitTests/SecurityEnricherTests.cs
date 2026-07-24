using Eaap.Domain;
using Eaap.Sarif;
using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.UnitTests;

public class SecurityEnricherTests
{
    private static (Result Result, Run Run) LoadFirst(string fixture)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var log = SarifDocument.Load(stream);
        var run = log.Runs[0];
        return (run.Results[0], run);
    }

    [Theory]
    [InlineData(0.0, SecuritySeverity.None)]
    [InlineData(3.9, SecuritySeverity.Low)]
    [InlineData(4.0, SecuritySeverity.Medium)]
    [InlineData(6.9, SecuritySeverity.Medium)]
    [InlineData(7.0, SecuritySeverity.High)]
    [InlineData(8.9, SecuritySeverity.High)]
    [InlineData(9.0, SecuritySeverity.Critical)]
    [InlineData(10.0, SecuritySeverity.Critical)]
    public void FromCvss_MapsBandsPerSpec(double cvss, SecuritySeverity expected)
    {
        Assert.Equal(expected, SecurityEnricher.FromCvss(cvss));
    }

    [Fact]
    public void Trivy_UsesCvssScore_AndExtractsCveAndCwe()
    {
        var (result, run) = LoadFirst("trivy.sarif");

        var info = SecurityEnricher.Enrich(result, run, isSecurityCategory: true);

        Assert.Equal(SecuritySeverity.Critical, info.Severity); // security-severity 10.0
        Assert.Equal("CVE-2021-44228", info.Cve);
        Assert.Equal("CWE-502", info.Cwe);
    }

    [Fact]
    public void Semgrep_NoCvss_MapsFromLevel_AndReadsCweTag()
    {
        var (result, run) = LoadFirst("semgrep.sarif");

        var info = SecurityEnricher.Enrich(result, run, isSecurityCategory: true);

        // warning level on a security adapter -> Medium; CWE from the "CWE-78: ..." tag.
        Assert.Equal(SecuritySeverity.Medium, info.Severity);
        Assert.Equal("CWE-78", info.Cwe);
        Assert.Null(info.Cve);
    }

    [Fact]
    public void Gitleaks_NoCvssNoCwe_MapsFromLevel()
    {
        var (result, run) = LoadFirst("gitleaks.sarif");

        var info = SecurityEnricher.Enrich(result, run, isSecurityCategory: true);

        Assert.Equal(SecuritySeverity.High, info.Severity); // error level on a security adapter
        Assert.Null(info.Cve);
        Assert.Null(info.Cwe);
    }

    [Fact]
    public void NonSecurityAdapter_WithoutCvss_StaysNone_ButStillExtractsCweFromText()
    {
        // A lint finding is never given a security severity from its level, but a CWE reference
        // in its text is still recorded.
        var (result, run) = LoadFirst("gitleaks.sarif");

        var info = SecurityEnricher.Enrich(result, run, isSecurityCategory: false);

        Assert.Equal(SecuritySeverity.None, info.Severity);
    }

    [Fact]
    public void NonSecurityAdapter_WithExplicitCvss_StillUsesTheScore()
    {
        // An explicit CVSS score wins regardless of category: a lint tool that reports one is honored.
        var (result, run) = LoadFirst("trivy.sarif");

        var info = SecurityEnricher.Enrich(result, run, isSecurityCategory: false);

        Assert.Equal(SecuritySeverity.Critical, info.Severity);
    }
}
