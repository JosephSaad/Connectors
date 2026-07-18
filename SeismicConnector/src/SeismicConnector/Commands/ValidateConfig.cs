// Commands/ValidateConfig.cs
// --------------------------
// Preflight: required env vars, config file shape (schema.json,
// graph-schema.json, exclusions.json), state-backend gating (HA/SQL), and
// best-effort Seismic + Graph connectivity probes. --strict turns
// connectivity warnings into failures.

using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Commands;

public static class ValidateConfig
{
    public static async Task<object?> CmdValidateConfig(ParsedArgs args)
    {
        var strict = args.GetFlag("strict");
        CommandRegistry.SetupLogging("validate-config", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var failures = 0;
        var warnings = 0;

        void Pass(string what) => progress.Info($"  [PASS] {what}");
        void Fail(string what)
        {
            failures++;
            progress.Info($"  [FAIL] {what}");
        }
        void Warn(string what)
        {
            warnings++;
            progress.Info($"  [WARN] {what}");
        }

        progress.Info("Validating configuration...");

        // 1. Core config load (env vars + config files).
        AppConfig? config = null;
        try
        {
            config = AppConfig.Load();
            Pass($"environment + config files loaded (connector '{config.Connector.Id}')");
        }
        catch (ConfigException ex)
        {
            Fail(ex.Message);
        }

        if (config is not null)
        {
            // 2. Schema objects sanity.
            if (config.EnabledObjects.Count == 0)
                Fail("config/schema.json: no enabled objects");
            else
                Pass($"schema objects: {string.Join(", ", config.EnabledObjects)}");

            // 3. graph-schema.json shape.
            try
            {
                var graphSchema = new ConnectionManager(new GraphClient(config.Graph), config).LoadGraphSchema();
                var count = graphSchema["properties"]?.AsArray()?.Count ?? 0;
                if (count == 0)
                    Fail("config/graph-schema.json: no properties defined");
                else
                    Pass($"graph schema: {count} properties");
            }
            catch (Exception ex)
            {
                Fail($"config/graph-schema.json: {ex.Message}");
            }

            // 4. Exclusion rules (the No-MNE filter). LoadFile already fails
            // CLOSED on a missing/empty/malformed/rule-less file (step 1 catches
            // it), so reaching here with no rules means acknowledgeNoExclusions:true
            // was set — a deliberately rule-less MNPI posture. Surface it, and
            // under --strict treat that no-rules posture as a hard FAIL.
            var rules = config.Exclusions;
            if (rules.HasNoRules)
            {
                if (strict)
                    Fail("config/exclusions.json defines NO rules (acknowledgeNoExclusions:true) — every "
                        + "item is eligible for ingest; --strict rejects a rule-less No-MNE posture");
                else
                    Warn("config/exclusions.json defines NO rules (acknowledgeNoExclusions:true) — "
                        + "every item is eligible for ingest");
            }
            else
            {
                Pass(
                    $"exclusion rules: {rules.ExcludedFlags.Count} flag(s), "
                    + $"{rules.RestrictedLibraries.Count + rules.RestrictedTeamsiteIds.Count} restricted librar(ies), "
                    + $"{rules.PropertyRules.Count} property rule(s)");
                if (rules.ExcludedFlags.Count > 0 && rules.FlagProperties.Count == 0)
                    Fail("exclusions.json: excludedFlags set but flagProperties is empty — flags can never match");
            }

            // 4b. Connection sharding (GRAPH_CONNECTION_SHARDS).
            if (ShardingConfig.IsEnabled)
            {
                if (ShardingConfig.TryLoad(config, out var shards, out var shardError))
                {
                    Pass($"connection sharding: {shards.Count} shard(s) — "
                        + string.Join("; ", shards.Select(s => $"{s.ConnectionId}=[{string.Join(",", s.ObjectTypes)}]")));
                }
                else
                {
                    Fail(shardError ?? "GRAPH_CONNECTION_SHARDS invalid");
                }
            }

            // 5. State backend gating.
            if (Settings.BoolEnv("HA_MODE") && !Settings.BoolEnv("USE_SQL_SERVER"))
                Fail("HA_MODE=true requires USE_SQL_SERVER=true");
            else if (Settings.BoolEnv("USE_SQL_SERVER"))
                Pass("SQL Server state backend enabled");

            if (Settings.BoolEnv("USE_KEY_VAULT")
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEY_VAULT_URI")))
            {
                Fail("USE_KEY_VAULT=true requires KEY_VAULT_URI");
            }

            // 5b. Distributed tracing (OpenTelemetry / OTLP export).
            var otelEndpoint = Environment.GetEnvironmentVariable(Tracing.EndpointEnvVar);
            if (string.IsNullOrWhiteSpace(otelEndpoint))
            {
                Warn("distributed tracing disabled (OTEL_EXPORTER_OTLP_ENDPOINT unset) — "
                    + "correlation ids still stamp logs/dead-letter/reports");
            }
            else if (Uri.TryCreate(otelEndpoint, UriKind.Absolute, out _))
            {
                var serviceName = Environment.GetEnvironmentVariable(Tracing.ServiceNameEnvVar);
                serviceName = string.IsNullOrWhiteSpace(serviceName) ? config.Connector.Name : serviceName;
                Pass($"tracing enabled — OTLP export to {otelEndpoint} (service '{serviceName}')");
            }
            else
            {
                Fail($"OTEL_EXPORTER_OTLP_ENDPOINT is not a valid absolute URI: '{otelEndpoint}'");
            }

            // 5c. Circuit breakers (resilience / degraded mode).
            var breakerOptions = CircuitBreakerOptions.FromEnv();
            if (!breakerOptions.Enabled)
            {
                Warn("circuit breakers DISABLED (CIRCUIT_BREAKER=false) — pure passthrough, "
                    + "no fail-fast / degraded-mode protection");
            }
            else
            {
                Pass($"circuit breakers enabled — threshold {breakerOptions.FailureThreshold} failures / "
                    + $"{breakerOptions.Window.TotalSeconds:F0}s window, open {breakerOptions.OpenDuration.TotalSeconds:F0}s, "
                    + $"{breakerOptions.HalfOpenTrials} half-open trial(s) (deps: seismic, graph)");
            }

            // 6. Best-effort connectivity probes.
            var seismic = new SeismicClient(config.Seismic);
            if (await seismic.ProbeAsync(ServiceStop.Token))
            {
                Pass("Seismic API reachable (teamsites listed)");
            }
            else if (strict)
            {
                Fail("Seismic API probe failed (--strict)");
            }
            else
            {
                Warn("Seismic API probe failed (network / credentials) — rerun with --strict to enforce");
            }

            var graph = new GraphClient(config.Graph);
            try
            {
                var state = await new ConnectionManager(graph, config)
                    .GetConnectionStateAsync(ServiceStop.Token);
                Pass(state is null
                    ? "Graph reachable (connection not created yet)"
                    : $"Graph reachable (connection state: {state})");
            }
            catch (Exception ex)
            {
                if (strict)
                    Fail($"Graph probe failed: {ex.Message}");
                else
                    Warn($"Graph probe failed: {ex.Message} — rerun with --strict to enforce");
            }
        }

        progress.Info(failures == 0
            ? $"Validation PASSED ({warnings} warning(s))."
            : $"Validation FAILED: {failures} failure(s), {warnings} warning(s).");
        return failures == 0;
    }
}
