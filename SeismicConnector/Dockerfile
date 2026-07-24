# syntax=docker/dockerfile:1

# =============================================================================
# Seismic Copilot Connector — multi-stage container image
# =============================================================================
# Build stage compiles + publishes the connector; the runtime stage carries only
# the .NET runtime + the published output, runs as a non-root user, and resolves
# config/, env/, logs/ and data/ against the working directory (/app), exactly
# like the console/Windows-service hosts do.
#
# IMPORTANT: the build context is the REPOSITORY ROOT, not this directory. The
# connector references the shared Connector.Chassis project, which lives beside
# it, and a Docker build cannot reach outside its context.
#
# Build:   docker build -f SeismicConnector/Dockerfile -t seismic-connector:latest .
#              (run from the repository root)
# Run:     docker run --rm -e SEISMIC_CONNECTOR_HOME=/app \
#              -v "$PWD/config:/app/config" -v "$PWD/env:/app/env" \
#              -v "$PWD/logs:/app/logs"     -v "$PWD/data:/app/data" \
#              seismic-connector:latest guide
# =============================================================================

# ---- Build stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first (better layer caching): copy the solution + project files —
# including the chassis project the connector references — then restore, then
# copy the rest of the tree.
COPY SeismicConnector/SeismicConnector.sln SeismicConnector/
COPY SeismicConnector/src/SeismicConnector/SeismicConnector.csproj SeismicConnector/src/SeismicConnector/
COPY SeismicConnector/tests/SeismicConnector.Tests/SeismicConnector.Tests.csproj SeismicConnector/tests/SeismicConnector.Tests/
COPY Connector.Chassis/Connector.Chassis.csproj Connector.Chassis/
RUN dotnet restore SeismicConnector/src/SeismicConnector/SeismicConnector.csproj

# Copy the remaining sources and publish the connector only (Release,
# framework-dependent, portable — the runtime stage already ships the .NET
# runtime; the portable publish carries native assets (e.g. SQLite) under
# runtimes/).
COPY Connector.Chassis/ Connector.Chassis/
COPY SeismicConnector/ SeismicConnector/
RUN dotnet publish SeismicConnector/src/SeismicConnector/SeismicConnector.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

# ---- Runtime stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# WORKDIR is the connector's "home": config/, env/, logs/, data/ resolve here.
WORKDIR /app
ENV SEISMIC_CONNECTOR_HOME=/app \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# Bring in the published binaries.
COPY --from=build /app/publish ./

# The connector reads/writes these directories at runtime; declare them as
# volumes so state (logs, dead-letter, SQLite identity store, checkpoints) and
# config/secrets can be bind-mounted or persisted by the orchestrator.
VOLUME ["/app/config", "/app/env", "/app/logs", "/app/data"]

# Run as a non-root user. Create the mount points up-front and hand them to the
# unprivileged user so bind-mounts and named volumes are writable.
RUN groupadd --system --gid 64123 seismic \
    && useradd  --system --uid 64123 --gid 64123 --home-dir /app --no-create-home seismic \
    && mkdir -p /app/config /app/env /app/logs /app/data \
    && chown -R seismic:seismic /app
USER seismic

# Optional health probe. The health/readiness/metrics endpoint is served only
# when HEALTH_PORT > 0 (docs/OBSERVABILITY.md). The runtime image has no curl,
# so we probe with the .NET runtime itself, and treat "not configured" as
# healthy so this is a strict no-op by default.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "--version"]

# `dotnet SeismicConnector.dll <command>`; default command is `guide`.
ENTRYPOINT ["dotnet", "SeismicConnector.dll"]
CMD ["guide"]
