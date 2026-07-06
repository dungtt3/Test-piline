#!/bin/sh
# Minimal smoke-test adapter honoring the EAAP adapter contract (build spec section 5).
# Emits a fixed SARIF log with warnings for the first files found in /workspace.
# Used to verify the Argo orchestration pipeline without the multi-GB MegaLinter image.
set -eu

echo "[echo-adapter] job=${EAAP_JOB_ID:-?} run=${EAAP_ANALYZER_RUN_ID:-?} commit=${EAAP_SNAPSHOT_COMMIT:-?}"

mkdir -p /results /artifacts

FILE1=$(cd /workspace && find . -type f | sed 's|^\./||' | sort | head -n 1)
FILE1=${FILE1:-unknown.txt}

cat > /results/echo.sarif <<SARIF
{
  "version": "2.1.0",
  "runs": [
    {
      "tool": { "driver": { "name": "EchoAnalyzer", "version": "1.0.0" } },
      "results": [
        {
          "ruleId": "echo-file-seen",
          "level": "warning",
          "message": { "text": "Echo adapter saw file ${FILE1} in snapshot ${EAAP_SNAPSHOT_COMMIT:-unknown}." },
          "locations": [
            {
              "physicalLocation": {
                "artifactLocation": { "uri": "${FILE1}" },
                "region": { "startLine": 1 }
              }
            }
          ]
        },
        {
          "ruleId": "echo-smoke",
          "level": "warning",
          "message": { "text": "EAAP end-to-end smoke warning." },
          "locations": [
            {
              "physicalLocation": {
                "artifactLocation": { "uri": "${FILE1}" },
                "region": { "startLine": 2 }
              }
            }
          ]
        }
      ]
    }
  ]
}
SARIF

find /workspace -type f > /artifacts/echo-files.txt
echo "[echo-adapter] wrote /results/echo.sarif"
exit 0
