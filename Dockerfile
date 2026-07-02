# syntax=docker/dockerfile:1

# =============================================================================
# Salesforce Copilot Connector — multi-stage container image
# =============================================================================
# Build stage compiles + publishes the connector; the runtime stage carries only
# the .NET runtime + the published output, runs as a non-root user, and resolves
# config/, env/, logs/ and data/ against the working directory (/app), exactly
# like the console/Windows-service hosts do.
#
# Build:   docker build -t sfconnector:latest .
# Run:     docker run --rm -e SFCONNECTOR_HOME=/app \
#              -v "$PWD/config:/app/config" -v "$PWD/env:/app/env" \
#              -v "$PWD/logs:/app/logs"     -v "$PWD/data:/app/data" \
#              sfconnector:latest guide
# =============================================================================

# ---- Build stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first (better layer caching): copy the solution + project files, then
# restore, then copy the rest of the tree.
COPY SalesforceCopilotConnector.sln ./
COPY src/SalesforceCopilotConnector/SalesforceCopilotConnector.csproj src/SalesforceCopilotConnector/
COPY tests/SalesforceCopilotConnector.Tests/SalesforceCopilotConnector.Tests.csproj tests/SalesforceCopilotConnector.Tests/
COPY tools/StressHarness/StressHarness.csproj tools/StressHarness/
RUN dotnet restore src/SalesforceCopilotConnector/SalesforceCopilotConnector.csproj

# Copy the remaining sources and publish the connector only (Release, linux-x64,
# framework-dependent — the runtime stage already ships the .NET runtime).
COPY . .
RUN dotnet publish src/SalesforceCopilotConnector/SalesforceCopilotConnector.csproj \
        -c Release \
        -r linux-x64 \
        --no-self-contained \
        --no-restore \
        -o /app/publish

# ---- Runtime stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# WORKDIR is the connector's "home": config/, env/, logs/, data/ resolve here.
WORKDIR /app
ENV SFCONNECTOR_HOME=/app \
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
RUN groupadd --system --gid 64123 sfconnector \
    && useradd  --system --uid 64123 --gid 64123 --home-dir /app --no-create-home sfconnector \
    && mkdir -p /app/config /app/env /app/logs /app/data \
    && chown -R sfconnector:sfconnector /app
USER sfconnector

# Optional health probe. The health/readiness/metrics endpoint is served only
# when HEALTH_PORT > 0 (see docs/IMPROVEMENTS_CONTRACT.md #9). The runtime image
# has no curl, so we probe with the .NET runtime itself, and treat "not
# configured" (HEALTH_PORT unset/0) as healthy so this is a strict no-op by
# default. Override or disable with `docker run --no-healthcheck` if desired.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "--version"]
# NOTE: to actually gate on the HTTP endpoint, run with e.g.
#   -e HEALTH_PORT=8080
# and replace the line above with a probe against
#   http://127.0.0.1:${HEALTH_PORT}/health
# (add curl to the image, or use a tiny probe binary). Kept as a best-effort
# liveness check here because the image intentionally stays curl-free and the
# port is off by default.

# `dotnet SalesforceCopilotConnector.dll <command>`; default command is `guide`.
ENTRYPOINT ["dotnet", "SalesforceCopilotConnector.dll"]
CMD ["guide"]
