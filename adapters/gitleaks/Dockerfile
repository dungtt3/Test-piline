# Gitleaks adapter. Build (context = this folder):
#   docker build -t eaap/adapter-gitleaks:latest adapters/gitleaks
FROM zricethezav/gitleaks:v8.18.4

COPY entrypoint.sh /eaap-entrypoint.sh
RUN chmod +x /eaap-entrypoint.sh

# Base image sets ENTRYPOINT ["gitleaks"]; override it with the EAAP standard entrypoint.
ENTRYPOINT ["/eaap-entrypoint.sh"]
