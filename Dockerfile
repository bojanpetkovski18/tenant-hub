# syntax=docker/dockerfile:1
# The production image (M11): the API serves the SPA from wwwroot. Build: docker build -t tenant-hub .
# Settings come from the environment (see .env.example and docker-compose.yml); nothing secret is baked in.

# 1. The SPA, with the exact locked dependencies.
FROM node:24.20.0-alpine3.24 AS web
WORKDIR /src/web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/web/ ./
RUN npm run build

# 2. The API. Restore first from the project and lock files only, so code changes reuse the restore layer.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS api
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/api/TenantHub.Domain/TenantHub.Domain.csproj src/api/TenantHub.Domain/packages.lock.json src/api/TenantHub.Domain/
COPY src/api/TenantHub.Application/TenantHub.Application.csproj src/api/TenantHub.Application/packages.lock.json src/api/TenantHub.Application/
COPY src/api/TenantHub.Infrastructure/TenantHub.Infrastructure.csproj src/api/TenantHub.Infrastructure/packages.lock.json src/api/TenantHub.Infrastructure/
COPY src/api/TenantHub.Api/TenantHub.Api.csproj src/api/TenantHub.Api/packages.lock.json src/api/TenantHub.Api/
RUN dotnet restore src/api/TenantHub.Api/TenantHub.Api.csproj --locked-mode
COPY src/api/ src/api/
# The SPA becomes the API's web root; publish fingerprints and pre-compresses it (Brotli, gzip) for MapStaticAssets.
COPY --from=web /src/web/dist/web/browser/ src/api/TenantHub.Api/wwwroot/
RUN dotnet publish src/api/TenantHub.Api/TenantHub.Api.csproj --configuration Release --no-restore --output /app

# 3. Runtime: Ubuntu chiseled (no shell, no package manager), running as the image's non-root "app" user. The
# "extra" variant adds tzdata and ICU: organizations work in IANA time zones, and plain chiseled has no zoneinfo.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=api /app ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
# No curl in a chiseled image: the app's own binary asks /health (database included) and exits 0 or 1.
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=5 \
    CMD ["dotnet", "TenantHub.Api.dll", "--health-check"]
ENTRYPOINT ["dotnet", "TenantHub.Api.dll"]
