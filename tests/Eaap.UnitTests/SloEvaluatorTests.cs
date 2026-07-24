using Eaap.Adapters.PrometheusSlo;
using Microsoft.CodeAnalysis.Sarif;

namespace Eaap.UnitTests;

public class SloEvaluatorTests
{
    [Theory]
    // healthy condition "observed < threshold": violated when observed >= threshold
    [InlineData(0.2, "<", 0.3, false)]
    [InlineData(0.3, "<", 0.3, true)]
    [InlineData(0.4, "<", 0.3, true)]
    [InlineData(0.005, "<", 0.01, false)]
    [InlineData(99, ">", 95, false)]
    [InlineData(90, ">", 95, true)]
    [InlineData(95, ">=", 95, false)]
    [InlineData(94.9, ">=", 95, true)]
    public void IsViolation_EvaluatesTheHealthyConditionCorrectly(double observed, string op, double threshold, bool expected)
    {
        Assert.Equal(expected, SloEvaluator.IsViolation(observed, op, threshold));
    }

    [Fact]
    public void IsViolation_UnknownOperator_Throws()
    {
        Assert.Throws<ArgumentException>(() => SloEvaluator.IsViolation(1, "≈", 1));
    }

    [Fact]
    public void BuildSarif_OnlyViolationsBecomeResults_WithStableFingerprintKey()
    {
        var results = new[]
        {
            SloEvaluator.Evaluate(new SloDefinition { Id = "latency-p95", Query = "q1", Operator = "<", Threshold = 0.3, Level = "error" }, observed: 0.42),
            SloEvaluator.Evaluate(new SloDefinition { Id = "error-rate", Query = "q2", Operator = "<", Threshold = 0.01, Level = "error" }, observed: 0.002)
        };

        var run = SloEvaluator.BuildSarif(results).Runs[0];

        var result = Assert.Single(run.Results); // only latency-p95 is violated
        Assert.Equal("slo.latency-p95", result.RuleId);
        Assert.Equal(FailureLevel.Error, result.Level);
        Assert.True(result.TryGetProperty("fingerprintKey", out string key));
        Assert.Equal("latency-p95", key);
        Assert.True(result.TryGetProperty("observedValue", out double observed));
        Assert.Equal(0.42, observed);
    }

    [Fact]
    public void BuildMetrics_EmitsEverySlosValue_EvenWhenItPassed()
    {
        var results = new[]
        {
            SloEvaluator.Evaluate(new SloDefinition { Id = "latency-p95", Operator = "<", Threshold = 0.3 }, 0.42),
            SloEvaluator.Evaluate(new SloDefinition { Id = "error-rate", Operator = "<", Threshold = 0.01 }, 0.002)
        };

        var metrics = SloEvaluator.BuildMetrics(results);

        Assert.Equal(0.42, metrics["runtime.slo.latency-p95.value"]);
        Assert.Equal(0.002, metrics["runtime.slo.error-rate.value"]);
    }

    [Fact]
    public void BuildSarif_Level_HonorsConfig()
    {
        var warn = SloEvaluator.Evaluate(new SloDefinition { Id = "x", Operator = "<", Threshold = 1, Level = "warning" }, 5);
        var result = Assert.Single(SloEvaluator.BuildSarif([warn]).Runs[0].Results);
        Assert.Equal(FailureLevel.Warning, result.Level);
    }

    [Fact]
    public void BuildSarif_PassesThePlatformValidator()
    {
        var r = SloEvaluator.Evaluate(new SloDefinition { Id = "x", Query = "q", Operator = "<", Threshold = 1, Level = "error" }, 5);
        using var stream = new MemoryStream();
        Eaap.Sarif.SarifDocument.Save(SloEvaluator.BuildSarif([r]), stream);
        stream.Position = 0;
        Assert.Empty(Eaap.Sarif.SarifValidator.Validate(stream));
    }
}
