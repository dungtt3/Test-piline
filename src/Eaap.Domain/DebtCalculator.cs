namespace Eaap.Domain;

/// <summary>
/// Converts a finding into estimated remediation minutes (build spec phase 4 section 3).
/// Priority: a suppressed finding is free; then an adapter-provided debtMinutes wins; then a
/// security-severity override (Critical/High); otherwise a per-level default.
/// </summary>
public static class DebtCalculator
{
    public const int CriticalMinutes = 120;
    public const int HighMinutes = 60;
    public const int ErrorMinutes = 30;
    public const int WarningMinutes = 10;
    public const int NoteMinutes = 2;

    public static int Compute(
        WarningLevel level,
        SecuritySeverity securitySeverity,
        bool isSuppressed,
        int? explicitDebtMinutes)
    {
        if (isSuppressed)
        {
            return 0;
        }

        if (explicitDebtMinutes is { } explicitValue and >= 0)
        {
            return explicitValue;
        }

        return securitySeverity switch
        {
            SecuritySeverity.Critical => CriticalMinutes,
            SecuritySeverity.High => HighMinutes,
            _ => level switch
            {
                WarningLevel.Error => ErrorMinutes,
                WarningLevel.Warning => WarningMinutes,
                WarningLevel.Note => NoteMinutes,
                _ => 0
            }
        };
    }
}
