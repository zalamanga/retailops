# syntax=docker/dockerfile:1.7
FROM node:22-bookworm-slim AS web
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS api
WORKDIR /src
COPY global.json ./
COPY backend/RetailOps.Api/RetailOps.Api.csproj backend/RetailOps.Api/packages.lock.json backend/RetailOps.Api/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore backend/RetailOps.Api/RetailOps.Api.csproj --locked-mode
COPY backend/RetailOps.Api/ backend/RetailOps.Api/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish backend/RetailOps.Api/RetailOps.Api.csproj -c Release --no-restore -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS runtime
USER root
RUN apt-get update && apt-get install --yes --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o 1654 -g 1654 /home/app/.aspnet/DataProtection-Keys
WORKDIR /app
COPY --from=api --chown=1654:1654 /publish/ ./
COPY --from=web --chown=1654:1654 /src/frontend/dist/ ./wwwroot/
COPY --chown=1654:1654 docs/licenses/ ./wwwroot/licenses/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_GCHeapHardLimit=0x10000000
USER 1654:1654
EXPOSE 8080
HEALTHCHECK --interval=20s --timeout=5s --start-period=30s --retries=3 CMD curl --fail --silent http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "RetailOps.Api.dll"]
