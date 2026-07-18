# syntax=docker/dockerfile:1

# =============================================================================
# BDH Hadoop Copilot Connector — multi-stage container image
# =============================================================================
# Build stage compiles + publishes the connector; the runtime stage carries only
# the .NET runtime + the published output, runs as a non-root user, and resolves
# config/, env/, logs/ and data/ against the working directory (/app), exactly
# like the console/Windows-service hosts do.
#
# Build:   docker build -t hadoopconnector:latest .
# Run:     docker run --rm -e HADOOP_CONNECTOR_HOME=/app \
#              -v "$PWD/config:/app/config" -v "$PWD/env:/app/env" \
#              -v "$PWD/logs:/app/logs"     -v "$PWD/data:/app/data" \
#              hadoopconnector:latest guide
# =============================================================================

# ---- Build stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first (better layer caching): copy the solution + project files, then
# restore, then copy the rest of the tree.
COPY HadoopConnector.sln ./
COPY src/HadoopConnector/HadoopConnector.csproj src/HadoopConnector/
COPY tests/HadoopConnector.Tests/HadoopConnector.Tests.csproj tests/HadoopConnector.Tests/
RUN dotnet restore src/HadoopConnector/HadoopConnector.csproj

# Copy the remaining sources and publish the connector only (Release,
# framework-dependent, portable — the runtime stage already ships the .NET
# runtime; the portable publish carries the native assets (e.g. SQLite)
# under runtimes/).
COPY . .
RUN dotnet publish src/HadoopConnector/HadoopConnector.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

# ---- Runtime stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# WORKDIR is the connector's "home": config/, env/, logs/, data/ resolve here.
WORKDIR /app
ENV HADOOP_CONNECTOR_HOME=/app \
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
RUN groupadd --system --gid 64124 clzconnector \
    && useradd  --system --uid 64124 --gid 64124 --home-dir /app --no-create-home clzconnector \
    && mkdir -p /app/config /app/env /app/logs /app/data \
    && chown -R clzconnector:clzconnector /app
USER clzconnector

# Optional health probe. The health/readiness/metrics endpoint is served only
# when HEALTH_PORT > 0. The runtime image has no curl, so we probe with the
# .NET runtime itself and treat "not configured" as healthy — a strict no-op
# by default. Override or disable with `docker run --no-healthcheck`.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "--version"]
# NOTE: to actually gate on the HTTP endpoint, run with e.g. -e HEALTH_PORT=8080
# and replace the probe with one against http://127.0.0.1:${HEALTH_PORT}/health.

# `dotnet HadoopConnector.dll <command>`; default command is `guide`.
ENTRYPOINT ["dotnet", "HadoopConnector.dll"]
CMD ["guide"]
