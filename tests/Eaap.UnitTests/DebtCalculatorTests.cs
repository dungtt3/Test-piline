using Eaap.Domain;

namespace Eaap.UnitTests;

public class DebtCalculatorTests
{
    [Theory]
    [InlineData(WarningLevel.Error, 30)]
    [InlineData(WarningLevel.Warning, 10)]
    [InlineData(WarningLevel.Note, 2)]
    [InlineData(WarningLevel.None, 0)]
    public void Compute_UsesLevelDefaults_WhenNoSecurityOrExplicit(WarningLevel level, int expected)
    {
        Assert.Equal(expected, DebtCalculator.Compute(level, SecuritySeverity.None, isSuppressed: false, null));
    }

    [Theory]
    [InlineData(SecuritySeverity.Critical, 120)]
    [InlineData(SecuritySeverity.High, 60)]
    public void Compute_SecurityCriticalAndHigh_OverrideLevel(SecuritySeverity severity, int expected)
    {
        // A warning-level finding that is Critical should cost the Critical rate, not the warning rate.
        Assert.Equal(expected, DebtCalculator.Compute(WarningLevel.Warning, severity, isSuppressed: false, null));
    }

    [Theory]
    [InlineData(SecuritySeverity.Medium)]
    [InlineData(SecuritySeverity.Low)]
    public void Compute_SecurityMediumAndLow_FallBackToLevel(SecuritySeverity severity)
    {
        Assert.Equal(DebtCalculator.ErrorMinutes,
            DebtCalculator.Compute(WarningLevel.Error, severity, isSuppressed: false, null));
    }

    [Fact]
    public void Compute_ExplicitDebtMinutes_WinsOverDefaults()
    {
        Assert.Equal(45, DebtCalculator.Compute(WarningLevel.Error, SecuritySeverity.Critical, isSuppressed: false, 45));
    }

    [Fact]
    public void Compute_Suppressed_IsAlwaysZero()
    {
        Assert.Equal(0, DebtCalculator.Compute(WarningLevel.Error, SecuritySeverity.Critical, isSuppressed: true, 999));
    }

    [Fact]
    public void Compute_NegativeExplicit_IsIgnored()
    {
        // A malformed negative value falls back to the default table rather than reducing debt.
        Assert.Equal(DebtCalculator.ErrorMinutes,
            DebtCalculator.Compute(WarningLevel.Error, SecuritySeverity.None, isSuppressed: false, -5));
    }
}
