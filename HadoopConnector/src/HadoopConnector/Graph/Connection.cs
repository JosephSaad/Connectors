// Graph/Connection.cs
// -------------------
// External connection + schema provisioning.
//
//   EnsureConnectionAsync — GET the connection; create it when missing; wait
//     until its state is "ready" (CONNECTION_TIMEOUT_SECONDS, polling every
//     CONNECTION_RETRY_INTERVAL_SECONDS).
//   RegisterSchemaAsync — PATCH the schema built from config/graph-schema.json
//     and poll the async operation (Location header) until completed. Schema
//     registration is idempotent; re-registering an identical schema is a
//     fast no-op server-side.

using System.Net;
using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Graph;

public sealed class ConnectionManager
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.graph");

    private readonly AppConfig _config;
    private readonly GraphClient _client;

    /// <summary>Delay seam so tests never really sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    public ConnectionManager(AppConfig config, GraphClient client)
    {
        _config = config;
        _client = client;
    }

    /// <summary>Ensure the external connection exists and is ready. Returns its state.</summary>
    public async Task<string> EnsureConnectionAsync(CancellationToken ct = default)
    {
        var existing = await _client.GetAsync($"external/connections/{_config.ConnectorId}", ct)
            .ConfigureAwait(false);
        if (existing.StatusCode == HttpStatusCode.NotFound)
        {
            Logger.Info($"Creating external connection '{_config.ConnectorId}'...");
            var create = await _client.PostAsync("external/connections", new JsonObject
            {
                ["id"] = _config.ConnectorId,
                ["name"] = _config.ConnectorName,
                ["description"] = _config.ConnectorDescription,
            }, ct).ConfigureAwait(false);
            if (!create.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Failed to create connection (HTTP {(int)create.StatusCode}): {create.RawBody}");
            }
        }
        else if (!existing.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to read connection (HTTP {(int)existing.StatusCode}): {existing.RawBody}");
        }

        return await WaitForConnectionReadyAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> WaitForConnectionReadyAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_config.ConnectionTimeoutSeconds);
        while (true)
        {
            var response = await _client.GetAsync($"external/connections/{_config.ConnectorId}", ct)
                .ConfigureAwait(false);
            var state = response.Body?["state"]?.GetValue<string>() ?? "unknown";
            if (string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"Connection '{_config.ConnectorId}' is ready.");
                return state;
            }
            if (string.Equals(state, "draft", StringComparison.OrdinalIgnoreCase))
            {
                // draft = created, schema not yet registered — usable for schema PATCH.
                Logger.Info($"Connection '{_config.ConnectorId}' is in draft state (schema pending).");
                return state;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Connection '{_config.ConnectorId}' did not become ready within "
                    + $"{_config.ConnectionTimeoutSeconds}s (last state: {state}).");
            }
            Logger.Info($"Connection state: {state}; waiting...");
            await DelayAsync(TimeSpan.FromSeconds(_config.ConnectionRetryIntervalSeconds), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Load config/graph-schema.json into the schema PATCH payload.</summary>
    public static JsonObject BuildSchemaPayload(string graphSchemaPath)
    {
        var raw = File.ReadAllText(graphSchemaPath);
        var properties = JsonNode.Parse(raw) as JsonArray
            ?? throw new InvalidDataException(
                $"'{graphSchemaPath}' must be a JSON array of schema property definitions.");
        return new JsonObject
        {
            ["baseType"] = "microsoft.graph.externalItem",
            ["properties"] = properties.DeepClone(),
        };
    }

    public static string DefaultGraphSchemaPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "graph-schema.json");

    /// <summary>Register the schema and wait for the async provisioning operation.</summary>
    public async Task RegisterSchemaAsync(string? graphSchemaPath = null, CancellationToken ct = default)
    {
        var payload = BuildSchemaPayload(graphSchemaPath ?? DefaultGraphSchemaPath);
        Logger.Info($"Registering schema for connection '{_config.ConnectorId}'...");
        var response = await _client.PatchAsync(
            $"external/connections/{_config.ConnectorId}/schema", payload, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var location = response.Headers?.Location?.ToString();
            if (string.IsNullOrEmpty(location))
            {
                Logger.Warning("Schema PATCH accepted but no operation Location header; assuming success.");
                return;
            }
            await WaitForOperationAsync(location, ct).ConfigureAwait(false);
            return;
        }
        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Schema registration failed (HTTP {(int)response.StatusCode}): {response.RawBody}");
        }
        Logger.Info("Schema registered.");
    }

    private async Task WaitForOperationAsync(string operationUrl, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_config.ConnectionTimeoutSeconds);
        while (true)
        {
            var response = await _client.GetAsync(operationUrl, ct).ConfigureAwait(false);
            var status = response.Body?["status"]?.GetValue<string>() ?? "inprogress";
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("Schema provisioning completed.");
                return;
            }
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Schema provisioning failed: {response.Body?["error"]?.ToJsonString() ?? response.RawBody}");
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Schema provisioning did not complete within {_config.ConnectionTimeoutSeconds}s.");
            }
            Logger.Info($"Schema provisioning status: {status}; waiting...");
            await DelayAsync(TimeSpan.FromSeconds(_config.ConnectionRetryIntervalSeconds), ct)
                .ConfigureAwait(false);
        }
    }
}
