# Trivy adapter. Build (context = this folder):
#   docker build -t eaap/adapter-trivy:latest adapters/trivy
FROM aquasec/trivy:0.53.0

# Freeze the vulnerability DB into the image so scans run fully offline (ADR-011).
RUN trivy image --download-db-only && trivy image --download-java-db-only || true

COPY entrypoint.sh /eaap-entrypoint.sh
RUN chmod +x /eaap-entrypoint.sh

ENTRYPOINT ["/eaap-entrypoint.sh"]
