# Build context is the REPOSITORY ROOT so the adapter can share src/Eaap.Sarif:
#   docker build -t eaap/adapter-test-report:latest -f adapters/test-report/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Eaap.Sarif/ src/Eaap.Sarif/
COPY src/Eaap.Adapters.TestReport/ src/Eaap.Adapters.TestReport/

RUN dotnet publish src/Eaap.Adapters.TestReport/Eaap.Adapters.TestReport.csproj \
      -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY --from=build /app /opt/eaap-test-report

# EAAP adapters standardize their entrypoint at /eaap-entrypoint.sh so Argo's emissary
# executor does not need registry access to resolve the image config (see phase 1 ADR-006).
COPY adapters/test-report/entrypoint.sh /eaap-entrypoint.sh
RUN chmod +x /eaap-entrypoint.sh

ENTRYPOINT ["/eaap-entrypoint.sh"]
