// Commands/ValidateConfig.cs
// --------------------------
// Preflight validation:
//   * required env vars & value shapes (AppConfig.Load)
//   * config files parse: config/schema.json, config/graph-schema.json,
//     seat list (or SEAT_GROUP_ID)
//   * feed path exists and manifests parse (checksums spot-checked)
//   * best-effort Graph connectivity (token + connection GET)
//   * best-effort Altrata API token when configured
//
// Non-strict: connectivity problems are warnings. --strict: any warning fails.

using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.Commands;

public static class ValidateConfig
{
    public static async Task<object?> RunAsync(bool strict)
    {
        Logging.Verbose = true;
        var errors = new List<string>();
        var warnings = new List<string>();

        Console.WriteLine("Validating configuration" + (strict ? " (strict)" : "") + "...");

        // 1. Env vars.
        AppConfig? config = null;
        try
        {
            config = AppConfig.Load();
            Pass("Environment variables");
        }
        catch (ConfigurationError exc)
        {
            errors.Add(exc.Message);
            Fail(exc.Message);
        }

        // 2. Config files.
        try
        {
            var schemaPath = Path.Combine("config", "schema.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
            Pass($"config/schema.json parses");
        }
        catch (Exception exc)
        {
            errors.Add($"config/schema.json: {exc.Message}");
            Fail($"config/schema.json: {exc.Message}");
        }

        try
        {
            var schema = CommandRegistry.LoadGraphSchema();
            Pass($"config/graph-schema.json parses ({schema.Properties.Count} properties)");
        }
        catch (Exception exc)
        {
            errors.Add($"config/graph-schema.json: {exc.Message}");
            Fail($"config/graph-schema.json: {exc.Message}");
        }

        if (config != null)
        {
            // 3. Seats.
            try
            {
                if (!string.IsNullOrEmpty(config.SeatGroupId))
                {
                    Pass($"Seat source: SEAT_GROUP_ID={config.SeatGroupId}");
                }
                else
                {
                    var seats = SeatService.ParseSeatFile(File.ReadAllText(config.SeatListPath));
                    if (seats.Count == 0)
                        throw new EntitlementViolationException("seat list is empty (ingestion would fail closed)");
                    Pass($"Seat list: {seats.Count} principal(s) from {config.SeatListPath}");
                }
            }
            catch (Exception exc)
            {
                errors.Add($"Seat list: {exc.Message}");
                Fail($"Seat list: {exc.Message}");
            }

            // 3b. Connection sharding map (GRAPH_CONNECTION_SHARDS).
            if (ShardingConfig.IsEnabled)
            {
                if (ShardingConfig.TryLoad(out var shards, out var shardError))
                {
                    Pass($"GRAPH_CONNECTION_SHARDS: {shards.Count} shard(s) partition all " +
                         $"{Altrata.Datasets.All.Length} datasets " +
                         $"({string.Join(", ", shards.Select(s => s.ConnectionId))})");
                }
                else
                {
                    errors.Add(shardError!);
                    Fail(shardError!);
                }
            }

            // 4. Feed path + manifests.
            if (string.IsNullOrEmpty(config.FeedPath))
            {
                warnings.Add("FEED_PATH not set — bulk ingestion is disabled");
                Warn("FEED_PATH not set — bulk ingestion is disabled");
            }
            else if (!Directory.Exists(config.FeedPath))
            {
                errors.Add($"FEED_PATH '{config.FeedPath}' does not exist");
                Fail($"FEED_PATH '{config.FeedPath}' does not exist");
            }
            else
            {
                var deliveries = FeedReader.DiscoverDeliveries(config.FeedPath);
                Pass($"FEED_PATH ok — {deliveries.Count} delivery(ies) found");
                foreach (var delivery in deliveries.TakeLast(1))
                {
                    try
                    {
                        FeedReader.ValidateChecksums(delivery);
                        Pass($"Checksums valid for latest delivery '{delivery.Id}'");
                    }
                    catch (Exception exc)
                    {
                        warnings.Add($"Delivery '{delivery.Id}': {exc.Message}");
                        Warn($"Delivery '{delivery.Id}': {exc.Message}");
                    }
                }
            }

            // 5. Graph connectivity (best effort).
            try
            {
                var graph = new GraphClient(config);
                var exists = await graph.ConnectionExistsAsync();
                Pass(exists
                    ? $"Graph reachable — connection '{config.ConnectorId}' exists"
                    : $"Graph reachable — connection '{config.ConnectorId}' not created yet (run setup-connection)");
            }
            catch (Exception exc)
            {
                warnings.Add($"Graph connectivity: {exc.Message}");
                Warn($"Graph connectivity: {exc.Message}");
            }

            // 6. Altrata API (best effort, only when configured).
            if (config.ApiConfigured)
            {
                var api = new AltrataApiClient(config, new State.FileStateStore(config.ConnectorId),
                    new AuditLog(config.ConnectorId));
                if (await api.ProbeAsync())
                    Pass("Altrata API token acquired");
                else
                {
                    warnings.Add("Altrata API token could not be acquired");
                    Warn("Altrata API token could not be acquired");
                }
            }
            else
            {
                Warn("Altrata API not configured — ingest-item is disabled (feeds still work)");
            }

            // 7. Distributed tracing status (OpenTelemetry).
            var traceStatus = Infrastructure.Telemetry.StatusLine(config.ConnectorName);
            if (Infrastructure.Telemetry.TracingEnabled)
                Pass($"OpenTelemetry {traceStatus}");
            else
                Warn($"OpenTelemetry {traceStatus}");

            // 8. Circuit breakers (degraded-mode / fail-safe).
            if (config.CircuitBreakerEnabled)
                Pass($"Circuit breakers on: threshold {config.CircuitBreakerFailureThreshold} " +
                     $"in {config.CircuitBreakerWindowSeconds}s, open {config.CircuitBreakerOpenSeconds}s, " +
                     $"{config.CircuitBreakerHalfOpenTrials} half-open trial(s) " +
                     "[graph=critical, altrata-api=non-critical]");
            else
                Warn("Circuit breakers OFF (CIRCUIT_BREAKER=false) — passthrough, no fail-fast on outages");

            // 9. Data classification & sensitivity labeling.
            if (config.Classification)
                Pass($"Classification on: sensitivityLabel + detectedCategories" +
                     (string.IsNullOrWhiteSpace(config.DataResidency)
                         ? " (no DATA_RESIDENCY set)"
                         : $", residency={config.DataResidency}") +
                     (config.ClassificationManifest ? ", per-delivery manifest" : ""));
            else
                Warn("Classification OFF (CLASSIFICATION=false) — no sensitivity/residency properties");
        }

        Console.WriteLine();
        Console.WriteLine($"Result: {errors.Count} error(s), {warnings.Count} warning(s)");
        var ok = errors.Count == 0 && (!strict || warnings.Count == 0);
        Console.WriteLine(ok ? "Configuration is VALID." : "Configuration is INVALID.");
        return ok;
    }

    private static void Pass(string message) => Console.WriteLine($"  [ok]   {message}");
    private static void Warn(string message) => Console.WriteLine($"  [warn] {message}");
    private static void Fail(string message) => Console.WriteLine($"  [FAIL] {message}");
}
