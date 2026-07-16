// Graph/Connection.cs
// -------------------
// External connection + schema provisioning.
//
// setup-connection / full-deployment call EnsureConnectionAsync then
// EnsureSchemaAsync. Both are idempotent: an existing connection is reused, an
// already-matching schema registration is left alone. Schema registration is a
// long-running Graph operation — we poll the connection's operational status
// until it reports `ready` (bounded by CONNECTION_TIMEOUT_SECONDS).

using System.Text.Json;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Graph;

public sealed class ConnectionManager
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.graph");
    private static readonly IAppLogger Progress = Logging.GetLogger("progress");

    private readonly GraphClient _client;
    private readonly AppConfig _config;

    /// <summary>Test seam: replaces Task.Delay in the provisioning poll loops.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    public ConnectionManager(GraphClient client, AppConfig config)
    {
        _client = client;
        _config = config;
    }

    /// <summary>Get the connection's current state, or null when it does not exist.</summary>
    public async Task<string?> GetConnectionStateAsync(CancellationToken ct = default)
    {
        try
        {
            var connection = await _client.GetAsync(
                $"/external/connections/{_config.Connector.Id}", ct).ConfigureAwait(false);
            return connection?["state"]?.GetValue<string>() ?? "unknown";
        }
        catch (GraphApiError ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>Create the external connection when missing; wait until it leaves 'draft' provisioning.</summary>
    public async Task EnsureConnectionAsync(CancellationToken ct = default)
    {
        var state = await GetConnectionStateAsync(ct).ConfigureAwait(false);
        if (state is not null)
        {
            Progress.Info($"Connection '{_config.Connector.Id}' already exists (state: {state})");
            return;
        }

        Progress.Info($"Creating external connection '{_config.Connector.Id}'...");
        await _client.PostAsync("/external/connections", new JsonObject
        {
            ["id"] = _config.Connector.Id,
            ["name"] = _config.Connector.Name,
            ["description"] = _config.Connector.Description,
        }, ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(_config.Graph.ConnectionTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            state = await GetConnectionStateAsync(ct).ConfigureAwait(false);
            if (state is "ready" or "draft")
            {
                Progress.Info($"Connection '{_config.Connector.Id}' created (state: {state})");
                return;
            }
            await DelayAsync(
                TimeSpan.FromSeconds(_config.Graph.ConnectionRetryIntervalSeconds), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Connection '{_config.Connector.Id}' did not finish provisioning within "
            + $"{_config.Graph.ConnectionTimeoutSeconds}s.");
    }

    /// <summary>Load config/graph-schema.json (the external connection schema payload).</summary>
    public JsonObject LoadGraphSchema()
    {
        var path = Path.Combine(_config.ConfigDir, "graph-schema.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Graph schema file not found: {path}");
        return JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new JsonException($"{path} is not a JSON object");
    }

    /// <summary>Register the schema when absent and poll until the connection is ready.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        JsonNode? existing = null;
        try
        {
            existing = await _client.GetAsync(
                $"/external/connections/{_config.Connector.Id}/schema", ct).ConfigureAwait(false);
        }
        catch (GraphApiError ex) when (ex.StatusCode == 404)
        {
        }

        if (existing?["properties"] is JsonArray { Count: > 0 })
        {
            Progress.Info("Schema already registered — skipping registration.");
            return;
        }

        Progress.Info("Registering schema (this can take several minutes)...");
        var schema = LoadGraphSchema();
        await _client.PatchAsync(
            $"/external/connections/{_config.Connector.Id}/schema", schema, ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(_config.Graph.ConnectionTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var state = await GetConnectionStateAsync(ct).ConfigureAwait(false);
            if (state == "ready")
            {
                Progress.Info("Schema registered; connection is ready.");
                return;
            }
            Logger.Info($"Waiting for schema provisioning (connection state: {state})...");
            await DelayAsync(
                TimeSpan.FromSeconds(_config.Graph.SchemaRetryIntervalSeconds), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Schema for '{_config.Connector.Id}' did not reach 'ready' within "
            + $"{_config.Graph.ConnectionTimeoutSeconds}s.");
    }
}
