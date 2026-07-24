package eaap.gate

# The gate passes when there are no hard failures. "violations" returned to the
# caller also carries non-failing notes (e.g. a coverage check that was skipped
# because no coverage was reported), so the result explains itself either way.

default pass := false

pass if {
    count(failures) == 0
}

violations := failures | notes

# --- Warnings (phase 1) ---------------------------------------------------

failures contains msg if {
    input.summary.errorCount > 0
    msg := sprintf("errorCount=%d, expected 0", [input.summary.errorCount])
}

failures contains msg if {
    input.summary.warningCount > input.thresholds.maxWarnings
    msg := sprintf("warningCount=%d > max %d",
        [input.summary.warningCount, input.thresholds.maxWarnings])
}

# --- New warnings (phase 2) -----------------------------------------------
# A negative threshold disables the rule so a repo's first scan, where every
# finding is new by definition, does not fail.

failures contains msg if {
    input.thresholds.maxNewWarnings >= 0
    input.summary.newWarningCount > input.thresholds.maxNewWarnings
    msg := sprintf("newWarningCount=%d > max %d",
        [input.summary.newWarningCount, input.thresholds.maxNewWarnings])
}

# --- Tests (phase 2) ------------------------------------------------------
# Only when the repo actually ran tests (the metric exists).

failures contains msg if {
    failed := input.metrics["tests.failed"]
    failed > input.thresholds.maxTestsFailed
    msg := sprintf("tests.failed=%d > max %d", [failed, input.thresholds.maxTestsFailed])
}

# --- Coverage (phase 2) ---------------------------------------------------
# Only when a coverage floor is configured (> 0) AND coverage was measured.

failures contains msg if {
    input.thresholds.minCoverageLine > 0
    line := input.metrics["coverage.line"]
    line < input.thresholds.minCoverageLine
    # %v, not %f: OPA types a JSON 90 as int and %f then prints %!f(int=90).
    msg := sprintf("coverage.line=%v < min %v", [line, input.thresholds.minCoverageLine])
}

# A configured coverage floor with no coverage metric does not fail the gate,
# but it is surfaced as a note so the result explains why the check was skipped.

notes contains msg if {
    input.thresholds.minCoverageLine > 0
    not input.metrics["coverage.line"]
    msg := "skipped: coverage.line check has no coverage metric reported"
}
