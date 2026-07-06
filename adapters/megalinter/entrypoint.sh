#!/bin/sh
# EAAP adapter contract (build spec section 5):
#   /workspace (ro) source, /results (rw) *.sarif, /artifacts (rw) raw reports.
#   Exit 0 when the analyzer ran (even with findings); non-zero only on crash.
set -u

export DEFAULT_WORKSPACE=/workspace
export SARIF_REPORTER=true
export REPORT_OUTPUT_FOLDER=/artifacts/megalinter
# Lint findings must not fail the adapter; only a MegaLinter crash may.
export DISABLE_ERRORS=true

mkdir -p /artifacts/megalinter /results

echo "[eaap-adapter] starting MegaLinter for job=${EAAP_JOB_ID:-?} run=${EAAP_ANALYZER_RUN_ID:-?} commit=${EAAP_SNAPSHOT_COMMIT:-?}"

# Run MegaLinter via the image's own entrypoint when present.
if [ -x /entrypoint.sh ]; then
    /entrypoint.sh || echo "[eaap-adapter] MegaLinter exited non-zero (ignored, findings are expected)"
else
    python -m megalinter.run || echo "[eaap-adapter] MegaLinter exited non-zero (ignored, findings are expected)"
fi

if [ -f /artifacts/megalinter/megalinter-report.sarif ]; then
    cp /artifacts/megalinter/megalinter-report.sarif /results/megalinter.sarif
    echo "[eaap-adapter] SARIF copied to /results/megalinter.sarif"
    exit 0
fi

echo "[eaap-adapter] ERROR: MegaLinter did not produce a SARIF report" >&2
exit 1
