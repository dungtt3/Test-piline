package eaap.gate

default pass := false

pass if {
    count(errors) == 0
}

errors contains msg if {
    input.summary.errorCount > 0
    msg := sprintf("errorCount=%d, expected 0", [input.summary.errorCount])
}

errors contains msg if {
    input.summary.warningCount > input.thresholds.maxWarnings
    msg := sprintf("warningCount=%d > max %d",
        [input.summary.warningCount, input.thresholds.maxWarnings])
}
