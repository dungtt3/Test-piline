#!/bin/sh
# Converter adapter: turns Cobertura / lcov reports under /workspace/.eaap/coverage into
# coverage.* metrics, plus warnings for files below COVERAGE_FILE_THRESHOLD (default 50%).
# Honors the EAAP adapter contract (build spec section 5): exit 0 even with findings.
set -eu

echo "[coverage-adapter] job=${EAAP_JOB_ID:-?} run=${EAAP_ANALYZER_RUN_ID:-?} commit=${EAAP_SNAPSHOT_COMMIT:-?}"

mkdir -p /results /artifacts

exec dotnet /opt/eaap-coverage/Eaap.Adapters.Coverage.dll
