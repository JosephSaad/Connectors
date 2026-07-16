# syntax=docker/dockerfile:1

# =============================================================================
# Altrata Copilot Connector — multi-stage container image
# =============================================================================
# Build stage compiles + publishes the connector; the runtime stage carries only
# the .NET runtime + the published output, runs as a non-root user, and resolves
# config/, env/, logs/, data/ and feeds/ against the working directory (/app),
# exactly like the console/Windows-service hosts do.
#
# Build:   docker build -t altrata-connector:latest .
# Run:     docker run --rm -e ALTRATA_CONNECTOR_HOME=/app \
#              -v "$PWD/config:/app/config" -v "$PWD/env:/app/env" \
#              -v "$PWD/logs:/app/logs"     -v "$PWD/data:/app/data" \
#              -v /srv/altrata/feeds:/app/feeds \
#              altrata-connector:latest guide
# =============================================================================

# ---- Build stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first (better layer caching): copy the solution + project files, then
# restore, then copy the rest of the tree.
COPY AltrataConnector.sln ./
COPY src/AltrataConnector/AltrataConnector.csproj src/AltrataConnector/
COPY tests/AltrataConnector.Tests/AltrataConnector.Tests.csproj tests/AltrataConnector.Tests/
RUN dotnet restore src/AltrataConnector/AltrataConnector.csproj

# Copy the remaining sources and publish the connector only (Release,
# framework-dependent, portable — the runtime stage already ships the .NET
# runtime; the portable publish carries the native assets (SQLite) under runtimes/).
COPY . .
RUN dotnet publish src/AltrataConnector/AltrataConnector.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

# ---- Runtime stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# WORKDIR is the connector's "home": config/, env/, logs/, data/, feeds/ resolve here.
WORKDIR /app
ENV ALTRATA_CONNECTOR_HOME=/app \
    FEED_PATH=/app/feeds \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# Bring in the published binaries.
COPY --from=build /app/publish ./

# The connector reads/writes these directories at runtime; declare them as
# volumes so state (logs, dead-letter, SQLite identity store, checkpoints,
# audit trail) and config/secrets/feeds can be bind-mounted or persisted.
VOLUME ["/app/config", "/app/env", "/app/logs", "/app/data", "/app/feeds"]

# Run as a non-root user. Create the mount points up-front and hand them to the
# unprivileged user so bind-mounts and named volumes are writable.
RUN groupadd --system --gid 64124 altrata \
    && useradd  --system --uid 64124 --gid 64124 --home-dir /app --no-create-home altrata \
    && mkdir -p /app/config /app/env /app/logs /app/data /app/feeds \
    && chown -R altrata:altrata /app
USER altrata

# Optional health probe. The health/readiness/metrics endpoint is served only
# when HEALTH_PORT > 0. The runtime image has no curl, so we probe with the
# .NET runtime itself and treat "not configured" as healthy — a strict no-op
# by default. Override or disable with `docker run --no-healthcheck`.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "--version"]

# `dotnet AltrataConnector.dll <command>`; default command is `guide`.
ENTRYPOINT ["dotnet", "AltrataConnector.dll"]
CMD ["guide"]
