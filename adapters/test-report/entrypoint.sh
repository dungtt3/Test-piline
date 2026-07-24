#!/bin/sh
# Converter adapter: parses test reports produced by the workflow's run-tests step
# (JUnit XML / TRX under /workspace/.eaap/test-results) into SARIF + metrics.json.
# Honors the EAAP adapter contract (build spec section 5): /workspace is read-only,
# results go to /results, original reports to /artifacts, and exit 0 even with findings.
set -eu

echo "[test-report-adapter] job=${EAAP_JOB_ID:-?} run=${EAAP_ANALYZER_RUN_ID:-?} commit=${EAAP_SNAPSHOT_COMMIT:-?}"

mkdir -p /results /artifacts

exec dotnet /opt/eaap-test-report/Eaap.Adapters.TestReport.dll
