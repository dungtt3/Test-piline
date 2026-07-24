# Build context is the REPOSITORY ROOT so the adapter can share src/Eaap.Sarif (ADR-007):
#   docker build -t eaap/adapter-coverage:latest -f adapters/coverage/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Eaap.Sarif/ src/Eaap.Sarif/
COPY src/Eaap.Adapters.Coverage/ src/Eaap.Adapters.Coverage/

RUN dotnet publish src/Eaap.Adapters.Coverage/Eaap.Adapters.Coverage.csproj \
      -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY --from=build /app /opt/eaap-coverage

COPY adapters/coverage/entrypoint.sh /eaap-entrypoint.sh
RUN chmod +x /eaap-entrypoint.sh

ENTRYPOINT ["/eaap-entrypoint.sh"]
