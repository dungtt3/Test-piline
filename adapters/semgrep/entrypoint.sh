#!/bin/sh
# Semgrep adapter (native-sarif): static analysis (SAST). Honors the EAAP adapter
# contract (build spec section 5): /workspace read-only, SARIF into /results, exit 0
# with findings. --error is deliberately NOT used so a finding is not a failure.
set -eu

echo "[semgrep-adapter] job=${EAAP_JOB_ID:-?} run=${EAAP_ANALYZER_RUN_ID:-?} commit=${EAAP_SNAPSHOT_COMMIT:-?}"

mkdir -p /results /artifacts

# Prefer the rules vendored into the image at build time (offline, ADR-012). Only when a
# caller explicitly opts into the registry with SEMGREP_USE_REGISTRY=1 do we use --config auto.
if [ "${SEMGREP_USE_REGISTRY:-0}" = "1" ]; then
  CONFIG="${SEMGREP_RULES:-auto}"
else
  CONFIG="${SEMGREP_RULES:-/eaap-rules}"
fi
echo "[semgrep-adapter] rules config: $CONFIG"

semgrep scan /workspace \
  --config "$CONFIG" \
  --sarif \
  --output /results/semgrep.sarif \
  --metrics off \
  --disable-version-check || echo "[semgrep-adapter] semgrep returned non-zero, continuing"

# Ensure a valid (possibly empty) SARIF exists even if semgrep produced nothing.
if [ ! -s /results/semgrep.sarif ]; then
  echo '{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"Semgrep"}},"results":[]}]}' > /results/semgrep.sarif
  echo "[semgrep-adapter] no output; wrote empty SARIF"
fi

echo "[semgrep-adapter] wrote /results/semgrep.sarif"
exit 0
