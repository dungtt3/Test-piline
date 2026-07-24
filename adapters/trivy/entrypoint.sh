#!/bin/sh
# Trivy adapter (native-sarif): filesystem scan for vulnerabilities, secrets and
# misconfiguration. Honors the EAAP adapter contract (build spec section 5):
# /workspace read-only, SARIF into /results, raw JSON into /artifacts, exit 0 with findings.
set -eu

echo "[trivy-adapter] job=${EAAP_JOB_ID:-?} run=${EAAP_ANALYZER_RUN_ID:-?} commit=${EAAP_SNAPSHOT_COMMIT:-?}"

mkdir -p /results /artifacts

# The DB is baked into the image at build time and frozen (ADR-011); never hit the network.
export TRIVY_SKIP_DB_UPDATE="${TRIVY_SKIP_DB_UPDATE:-true}"
export TRIVY_SKIP_JAVA_DB_UPDATE="${TRIVY_SKIP_JAVA_DB_UPDATE:-true}"
SEVERITY="${TRIVY_SEVERITY:-UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL}"

# Two passes over the same cached DB: SARIF for the platform, raw JSON for the artifact store.
trivy fs /workspace \
  --scanners vuln,secret,misconfig \
  --severity "$SEVERITY" \
  --format sarif \
  --output /results/trivy.sarif \
  --exit-code 0

trivy fs /workspace \
  --scanners vuln,secret,misconfig \
  --severity "$SEVERITY" \
  --format json \
  --output /artifacts/trivy.json \
  --exit-code 0 || echo "[trivy-adapter] raw JSON report skipped"

echo "[trivy-adapter] wrote /results/trivy.sarif"
exit 0
