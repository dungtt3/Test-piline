# Semgrep adapter. Build (context = this folder):
#   docker build -t eaap/adapter-semgrep:latest adapters/semgrep
FROM semgrep/semgrep:1.80.0

# Vendor a minimal offline ruleset into the image (ADR-012) so scans do not need the
# registry. --config auto stays available via SEMGREP_USE_REGISTRY=1 when network exists.
RUN mkdir -p /eaap-rules \
    && (semgrep --config p/security-audit --dump-config /eaap-rules/security-audit.yaml 2>/dev/null || true) \
    && (semgrep --config p/secrets --dump-config /eaap-rules/secrets.yaml 2>/dev/null || true)

COPY rules/ /eaap-rules/
COPY entrypoint.sh /eaap-entrypoint.sh
RUN chmod +x /eaap-entrypoint.sh

ENTRYPOINT ["/eaap-entrypoint.sh"]
