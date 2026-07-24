#!/bin/sh
# Gitleaks adapter (native-sarif): secret scanning. Honors the EAAP adapter contract
# (build spec section 5): /workspace read-only, SARIF into /results, exit 0 with findings.
# --no-git because the snapshot is a tarball with no .git (phase 1 design); this scans the
# current working tree only, not commit history.
set -eu

echo "[gitleaks-adapter] job=${EAAP_JOB_ID:-?} run=${EAAP_ANALYZER_RUN_ID:-?} commit=${EAAP_SNAPSHOT_COMMIT:-?}"

mkdir -p /results /artifacts

gitleaks detect \
  --source /workspace \
  --no-git \
  --report-format sarif \
  --report-path /results/gitleaks.sarif \
  --exit-code 0 \
  --redact

# gitleaks omits the report file when it finds nothing; emit a valid empty SARIF instead.
if [ ! -s /results/gitleaks.sarif ]; then
  echo '{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"gitleaks"}},"results":[]}]}' > /results/gitleaks.sarif
  echo "[gitleaks-adapter] no findings; wrote empty SARIF"
fi

echo "[gitleaks-adapter] wrote /results/gitleaks.sarif"
exit 0
