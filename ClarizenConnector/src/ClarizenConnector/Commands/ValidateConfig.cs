// Commands/ValidateConfig.cs
// --------------------------
// `validate-config [--strict]` — preflight checks before a long crawl:
//
//   • required env vars present, CONNECTOR_ID shape valid
//   • config/schema.json and config/graph-schema.json parse and are sane
//   • cross-flag consistency (HA needs SQL, acl mode needs the finance group,
//     TDW path exists when set, ...)
//   • --strict additionally requires Clarizen login and a Graph token to
//     succeed (best-effort connectivity), and turns warnings into failures.

using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Commands;

public static class ValidateConfig
{
    internal static readonly string[] RequiredEnvVars =
    {
        "CONNECTOR_ID",
        "CLARIZEN_USERNAME",
        "SECRET_CLARIZEN_PASSWORD",
        "AAD_APP_TENANT_ID",
        "AAD_APP_CLIENT_ID",
        "SECRET_AAD_APP_CLIENT_SECRET",
    };

    public sealed class Result
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        public bool Ok(bool strict) => Errors.Count == 0 && (!strict || Warnings.Count == 0);
    }

    /// <summary>Offline validation (no network). Testable.</summary>
    internal static Result ValidateCore(string? schemaPath = null, string? graphSchemaPath = null)
    {
        var result = new Result();

        foreach (var name in RequiredEnvVars)
        {
            var value = name.StartsWith("SECRET_", StringComparison.Ordinal)
                ? SecretProvider.GetSecret(name)
                : Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                result.Errors.Add($"Missing required environment variable: {name}");
        }

        var connectorId = Environment.GetEnvironmentVariable("CONNECTOR_ID");
        if (!string.IsNullOrWhiteSpace(connectorId)
            && AppConfig.ValidateConnectorId(connectorId) is { } idError)
        {
            result.Errors.Add(idError);
        }

        // schema.json
        var schemaFile = schemaPath ?? SchemaConfig.DefaultPath;
        if (!File.Exists(schemaFile))
        {
            result.Errors.Add($"Missing config file: {schemaFile}");
        }
        else
        {
            try
            {
                var schema = SchemaConfig.Load(schemaFile);
                foreach (var obj in schema.ObjectList)
                {
                    if (obj.AclMode is not ("projectMembers" or "ownerOnly" or "public"))
                        result.Errors.Add(
                            $"schema.json: object '{obj.ObjectName}' has invalid aclMode '{obj.AclMode}' "
                            + "(projectMembers | ownerOnly | public).");
                }

                // GRAPH_CONNECTION_SHARDS must be a valid exact partition of the
                // object list — a bad shard map must fail preflight, not mid-crawl.
                if (ShardingConfig.IsEnabled
                    && !ShardingConfig.TryLoad(schema, out _, out var shardError)
                    && shardError is not null)
                {
                    result.Errors.Add(shardError);
                }
            }
            catch (Exception exc)
            {
                result.Errors.Add($"schema.json invalid: {exc.Message}");
            }
        }

        // graph-schema.json
        var graphSchemaFile = graphSchemaPath ?? ConnectionManager.DefaultGraphSchemaPath;
        if (!File.Exists(graphSchemaFile))
        {
            result.Errors.Add($"Missing config file: {graphSchemaFile}");
        }
        else
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(graphSchemaFile)) is not JsonArray properties)
                {
                    result.Errors.Add("graph-schema.json must be a JSON array of property definitions.");
                }
                else
                {
                    foreach (var property in properties)
                    {
                        if (property?["name"] is null || property["type"] is null)
                        {
                            result.Errors.Add(
                                "graph-schema.json: every property needs 'name' and 'type'.");
                            break;
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                result.Errors.Add($"graph-schema.json invalid: {exc.Message}");
            }
        }

        // Cross-flag consistency.
        if (EnvFlags.HaMode && !EnvFlags.UseSqlServer)
            result.Errors.Add("HA_MODE=true requires USE_SQL_SERVER=true and SQL_CONNECTION_STRING.");

        if (EnvFlags.IsTrue("USE_KEY_VAULT")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEY_VAULT_URI")))
        {
            result.Errors.Add("USE_KEY_VAULT=true requires KEY_VAULT_URI.");
        }

        var financialMode = EnvFlags.GetString("FINANCIAL_DATA_MODE", "tag").ToLowerInvariant();
        if (financialMode is not ("tag" or "filter" or "acl"))
            result.Errors.Add("FINANCIAL_DATA_MODE must be one of tag | filter | acl.");
        if (financialMode == "acl"
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FINANCIAL_DATA_GROUP_ID")))
        {
            result.Errors.Add("FINANCIAL_DATA_MODE=acl requires FINANCIAL_DATA_GROUP_ID.");
        }

        var tdwPath = Environment.GetEnvironmentVariable("TDW_EXPORT_PATH");
        if (!string.IsNullOrWhiteSpace(tdwPath) && !Directory.Exists(tdwPath))
            result.Warnings.Add($"TDW_EXPORT_PATH '{tdwPath}' does not exist; full crawls will use the REST API.");

        var logFormat = Environment.GetEnvironmentVariable("LOG_FORMAT");
        if (!string.IsNullOrEmpty(logFormat)
            && !logFormat.Equals("json", StringComparison.OrdinalIgnoreCase)
            && !logFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add($"LOG_FORMAT '{logFormat}' is not 'text' or 'json'; treating as text.");
        }

        var budget = EnvFlags.GetInt("CLARIZEN_API_CALLS_PER_DAY", 25_000);
        if (budget < 1000)
            result.Warnings.Add(
                $"CLARIZEN_API_CALLS_PER_DAY={budget} is very low; crawls may pause on quota often.");

        // Webhook receiver: a configured port with no secret is fail-closed
        // (the receiver refuses to start). Surface it as an error at preflight.
        var webhookPort = Environment.GetEnvironmentVariable(Webhook.WebhookReceiver.PortEnvVar);
        if (!string.IsNullOrWhiteSpace(webhookPort)
            && int.TryParse(webhookPort, out var wp) && wp > 0
            && string.IsNullOrWhiteSpace(SecretProvider.GetSecret(Webhook.WebhookReceiver.SecretEnvVar)))
        {
            result.Errors.Add(
                $"{Webhook.WebhookReceiver.PortEnvVar}={wp} requires {Webhook.WebhookReceiver.SecretEnvVar} "
                + "(the receiver refuses to start unauthenticated).");
        }

        return result;
    }

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var strict = args.HasFlag("--strict");
        EnvLoader.LoadLayered();
        Logging.Initialize("validate_config", args.Verbose);
        Dashboard.Banner("Validate config" + (strict ? " (strict)" : string.Empty));

        var result = ValidateCore();

        // Report the circuit-breaker configuration (informational).
        var cb = CircuitBreakerOptions.FromEnv();
        if (!cb.Enabled)
        {
            Dashboard.Line("Circuit breakers: disabled (CIRCUIT_BREAKER=false — pure passthrough).");
        }
        else
        {
            Dashboard.Line(
                $"Circuit breakers: enabled — threshold {cb.FailureThreshold} failures / "
                + $"{cb.Window.TotalSeconds:0}s window, open {cb.OpenDuration.TotalSeconds:0}s, "
                + $"{cb.HalfOpenTrials} half-open trial(s). Guarded: clarizen, graph.");
        }

        // Report the tracing target (informational — never a failure).
        var otelEndpoint = Environment.GetEnvironmentVariable(Tracing.EndpointEnvVar);
        if (string.IsNullOrWhiteSpace(otelEndpoint))
        {
            Dashboard.Line("OpenTelemetry tracing: disabled (OTEL_EXPORTER_OTLP_ENDPOINT not set).");
        }
        else
        {
            var serviceName = Tracing.ResolveServiceName(
                Environment.GetEnvironmentVariable("CONNECTOR_NAME") ?? "Clarizen AdaptiveWork");
            Dashboard.Line(
                $"OpenTelemetry tracing: exporting to '{otelEndpoint}' as service '{serviceName}'.");
        }

        // Best-effort connectivity — required to pass only under --strict.
        if (result.Errors.Count == 0)
        {
            try
            {
                var config = AppConfig.Load();
                var clarizen = new ClarizenClient(config);
                var clarizenOk = await clarizen.PingAsync(ServiceStop.Token);
                if (!clarizenOk)
                    (strict ? result.Errors : result.Warnings).Add("Clarizen connectivity check failed.");
                else
                    Dashboard.Line("Clarizen connectivity: OK");

                try
                {
                    var graph = new GraphClient(config);
                    await graph.GetTokenAsync(ServiceStop.Token);
                    Dashboard.Line("Graph token acquisition: OK");
                }
                catch (Exception exc)
                {
                    (strict ? result.Errors : result.Warnings).Add(
                        $"Graph token acquisition failed: {exc.Message}");
                }
            }
            catch (Exception exc)
            {
                (strict ? result.Errors : result.Warnings).Add($"Connectivity checks skipped: {exc.Message}");
            }
        }

        foreach (var warning in result.Warnings)
            Dashboard.Line($"WARNING: {warning}");
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"ERROR: {error}");

        var ok = result.Ok(strict);
        Dashboard.Line(ok
            ? "Configuration looks good."
            : $"Configuration is NOT valid ({result.Errors.Count} error(s), {result.Warnings.Count} warning(s)).");
        return ok;
    }
}
