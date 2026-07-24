# Prometheus SLO adapter (query mode). Build context = repo root (ADR-007):
#   docker build -t eaap/adapter-prometheus-slo:latest -f adapters/prometheus-slo/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Eaap.Sarif/ src/Eaap.Sarif/
COPY src/Eaap.Adapters.PrometheusSlo/ src/Eaap.Adapters.PrometheusSlo/

RUN dotnet publish src/Eaap.Adapters.PrometheusSlo/Eaap.Adapters.PrometheusSlo.csproj \
      -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY --from=build /app /opt/eaap-prometheus-slo

COPY adapters/prometheus-slo/entrypoint.sh /eaap-entrypoint.sh
RUN chmod +x /eaap-entrypoint.sh

ENTRYPOINT ["/eaap-entrypoint.sh"]
