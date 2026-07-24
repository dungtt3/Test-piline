#!/bin/sh
# Query-mode adapter: evaluates runtime SLOs against Prometheus. No /workspace needed
# (manifest requiresWorkspace: false). Config comes from EAAP_RUNTIME_CONFIG (JSON of the
# repo's .eaap/config.yaml runtime section). SARIF + metrics.json go to /results.
set -eu

echo "[prometheus-slo-adapter] job=${EAAP_JOB_ID:-?} run=${EAAP_ANALYZER_RUN_ID:-?}"

mkdir -p /results

exec dotnet /opt/eaap-prometheus-slo/Eaap.Adapters.PrometheusSlo.dll
