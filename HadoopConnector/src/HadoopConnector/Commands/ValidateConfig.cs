// Commands/ValidateConfig.cs
// --------------------------
// `validate-config [--strict]` — preflight checks before a long crawl:
//
//   • required env vars present, CONNECTOR_ID shape valid
//   • HDFS_MODE consistency (webhdfs needs HDFS_NAMENODE_URL, localpath
//     needs BDH_EXPORT_PATH)
//   • config/schema.json, config/graph-schema.json AND config/filters.json
//     parse and are sane (a malformed filter is an ERROR, never ignored)
//   • the fail-closed scale guard: objects with NO filter and no exemption
//     are a warning — and an ERROR under --strict (deploying an unfiltered
//     150M-row object is an outage waiting to happen)
//   • cross-flag consistency (HA needs SQL, Key Vault needs URI, ...)
//   • --strict additionally requires BDH source connectivity and a Graph
//     token to succeed, and turns warnings into failures.

using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Commands;

public static class ValidateConfig
{
    internal static readonly string[] RequiredEnvVars =
    {
        "CONNECTOR_ID",
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
    internal static Result ValidateCore(
        string? schemaPath = null, string? graphSchemaPath = null, string? filtersPath = null,
        bool strict = false)
    {
        var result = new Result();

        foreach (var name in RequiredEnvVars)
        {
            string? value;
            try
            {
                value = name.StartsWith("SECRET_", StringComparison.Ordinal)
                    ? SecretProvider.GetSecret(name)
                    : Environment.GetEnvironmentVariable(name);
            }
            catch (ArgumentException exc)
            {
                // e.g. USE_KEY_VAULT=true without KEY_VAULT_URI — report and
                // keep validating instead of aborting preflight.
                result.Errors.Add(exc.Message.Replace("Invalid configuration: ", string.Empty));
                continue;
            }
            if (string.IsNullOrWhiteSpace(value))
                result.Errors.Add($"Missing required environment variable: {name}");
        }

        var connectorId = Environment.GetEnvironmentVariable("CONNECTOR_ID");
        if (!string.IsNullOrWhiteSpace(connectorId)
            && AppConfig.ValidateConnectorId(connectorId) is { } idError)
        {
            result.Errors.Add(idError);
        }

        // HDFS_MODE consistency.
        var hdfsMode = EnvFlags.GetString("HDFS_MODE", "webhdfs").ToLowerInvariant();
        if (hdfsMode is not ("webhdfs" or "localpath"))
        {
            result.Errors.Add($"HDFS_MODE '{hdfsMode}' is invalid (webhdfs | localpath).");
        }
        else if (hdfsMode == "webhdfs"
                 && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HDFS_NAMENODE_URL")))
        {
            result.Errors.Add("HDFS_MODE=webhdfs requires HDFS_NAMENODE_URL "
                + "(e.g. http://namenode:9870/webhdfs/v1).");
        }
        else if (hdfsMode == "localpath")
        {
            var exportPath = Environment.GetEnvironmentVariable("BDH_EXPORT_PATH");
            if (string.IsNullOrWhiteSpace(exportPath))
                result.Errors.Add("HDFS_MODE=localpath requires BDH_EXPORT_PATH.");
            else if (!Directory.Exists(exportPath))
                result.Warnings.Add($"BDH_EXPORT_PATH '{exportPath}' does not exist.");
        }

        // schema.json
        SchemaConfig? schema = null;
        var schemaFile = schemaPath ?? SchemaConfig.DefaultPath;
        if (!File.Exists(schemaFile))
        {
            result.Errors.Add($"Missing config file: {schemaFile}");
        }
        else
        {
            try
            {
                schema = SchemaConfig.Load(schemaFile);

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

        // filters.json — THE scale control. Malformed → error. Missing → every
        // object is unfiltered (guard applies below).
        FilterSet filters = FilterSet.Empty;
        var filtersFile = filtersPath
            ?? Environment.GetEnvironmentVariable("BDH_FILTERS_PATH")
            ?? FilterSet.DefaultPath;
        if (!File.Exists(filtersFile))
        {
            result.Warnings.Add(
                $"Missing filter config: {filtersFile} — every object will trip the "
                + "fail-closed full-scan guard unless exempted.");
        }
        else
        {
            try
            {
                filters = FilterSet.Load(filtersFile);
            }
            catch (Exception exc)
            {
                result.Errors.Add($"filters.json invalid: {exc.Message}");
            }
        }

        // Fail-closed scale guard preflight: unfiltered objects are warnings,
        // errors under --strict (unless ALLOW_FULL_SCAN=true).
        if (schema is not null && !EnvFlags.IsTrue("ALLOW_FULL_SCAN"))
        {
            foreach (var obj in schema.ObjectList)
            {
                var filter = filters.For(obj.ObjectName);
                if (filter is { IsEffectivelyFiltered: true } || filters.IsFullScanAllowed(obj.ObjectName))
                    continue;
                // Distinguish "no filter at all" from "a filter that prunes
                // nothing" (only a non-dt partition predicate on a key that
                // MatchesPartition never prunes on) — both trip the guard.
                var hasInertFilter = filter is { HasAnyFilter: true };
                var message = hasInertFilter
                    ? $"Object '{obj.ObjectName}' is only 'filtered' by a non-pruning partition "
                      + "predicate (no record predicate and no 'dt' partition predicate) — it does "
                      + "NOT prune and the crawl will refuse it (fail-closed scale guard). Add a "
                      + "record predicate or a 'dt' partition predicate, or an explicit exemption."
                    : $"Object '{obj.ObjectName}' has NO filter in filters.json and is not in "
                      + "fullScanAllowed — the crawl will refuse it (fail-closed scale guard). "
                      + "Add partition/record filters or an explicit exemption.";
                (strict ? result.Errors : result.Warnings).Add(message);
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

        var logFormat = Environment.GetEnvironmentVariable("LOG_FORMAT");
        if (!string.IsNullOrEmpty(logFormat)
            && !logFormat.Equals("json", StringComparison.OrdinalIgnoreCase)
            && !logFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add($"LOG_FORMAT '{logFormat}' is not 'text' or 'json'; treating as text.");
        }

        var rowCap = EnvFlags.GetInt("BDH_MAX_RECORDS_PER_OBJECT", 500_000);
        if (rowCap == 0)
            result.Warnings.Add(
                "BDH_MAX_RECORDS_PER_OBJECT=0 disables the row-cap safety valve entirely.");

        var lagHours = EnvFlags.GetInt("BDH_LAG_HOURS", 24);
        if (lagHours < 24)
            result.Warnings.Add(
                $"BDH_LAG_HOURS={lagHours} is below the nightly sync lag (24h); incremental "
                + "crawls may miss late-arriving partitions.");

        return result;
    }

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var strict = args.HasFlag("--strict");
        EnvLoader.LoadLayered();
        Logging.Initialize("validate_config", args.Verbose);
        Dashboard.Banner("Validate config" + (strict ? " (strict)" : string.Empty));

        var result = ValidateCore(strict: strict);

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
                + $"{cb.HalfOpenTrials} half-open trial(s). Guarded: hdfs, graph.");
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
                Environment.GetEnvironmentVariable("CONNECTOR_NAME") ?? "BDH Hadoop Data Mart");
            Dashboard.Line(
                $"OpenTelemetry tracing: exporting to '{otelEndpoint}' as service '{serviceName}'.");
        }

        // Best-effort connectivity — required to pass only under --strict.
        if (result.Errors.Count == 0)
        {
            try
            {
                var config = AppConfig.Load();
                using var source = Runtime.CreateSource(config, CircuitBreaker.Disabled);
                bool sourceOk;
                string? sourceFailReason = null;
                try
                {
                    sourceOk = await source.ExistsAsync(string.Empty, ServiceStop.Token);
                    if (!sourceOk)
                        sourceFailReason = "the BDH root path does not exist";
                }
                catch (Exception exc)
                {
                    // Preflight is exactly where the failure REASON matters —
                    // "connection refused" vs "404" vs "path escapes root" each
                    // point at a different setting. Never discard it.
                    sourceOk = false;
                    sourceFailReason = $"{exc.GetType().Name}: {exc.Message}";
                }
                if (!sourceOk)
                    (strict ? result.Errors : result.Warnings).Add(
                        $"BDH source connectivity check failed ({source.Description}): {sourceFailReason}");
                else
                    Dashboard.Line($"BDH source connectivity: OK ({source.Description})");

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
