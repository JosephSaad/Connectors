// Commands/Runtime.cs
// -------------------
// Shared command bootstrap: layered env loading, logging init, log pruning,
// config + schema loading, client construction, and the assembled ingestion
// pipeline (directory snapshot → mapper → resolver → converter).

using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Commands;

public sealed class RuntimeContext : IDisposable
{
    public required AppConfig Config { get; init; }
    public required SchemaConfig Schema { get; init; }
    public required ClarizenClient Clarizen { get; init; }
    public required GraphClient Graph { get; init; }
    public required ConnectionManager Connection { get; init; }
    public required IIdentityStore IdentityStore { get; init; }
    public required IdentitySync IdentitySync { get; init; }

    public IDisposable? Health { get; set; }

    /// <summary>OpenTelemetry provider handle (null when tracing is off).</summary>
    public IDisposable? Tracing { get; set; }

    private DirectorySnapshot? _snapshot;

    /// <summary>Load (and cache) the directory snapshot for ACL resolution.</summary>
    public async Task<DirectorySnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        _snapshot ??= await IdentitySync.LoadDirectoryAsync(ct).ConfigureAwait(false);

    /// <summary>Invalidate the cached snapshot (fresh crawl cycles reload it).</summary>
    public void InvalidateSnapshot() => _snapshot = null;

    /// <summary>
    /// Provision the Graph external connection(s) + schema: one connection on
    /// the default path, or one per shard when GRAPH_CONNECTION_SHARDS is set
    /// (each shard is its own connection with its own schema — docs/SHARDING.md).
    /// A misconfigured shard map aborts before touching Graph.
    /// </summary>
    public async Task ProvisionConnectionsAsync(CancellationToken ct = default)
    {
        if (ShardingConfig.TryLoad(Schema, out var shards, out var shardError))
        {
            foreach (var shard in shards)
            {
                Dashboard.Line($"Provisioning shard connection '{shard.ConnectionId}' "
                               + $"({string.Join(", ", shard.ObjectTypes)})...");
                var manager = new ConnectionManager(Config.CloneForConnection(shard.ConnectionId), Graph);
                await manager.EnsureConnectionAsync(ct).ConfigureAwait(false);
                await manager.RegisterSchemaAsync(ct: ct).ConfigureAwait(false);
            }
            return;
        }
        if (shardError is not null)
            throw new ArgumentException(shardError);

        await Connection.EnsureConnectionAsync(ct).ConfigureAwait(false);
        await Connection.RegisterSchemaAsync(ct: ct).ConfigureAwait(false);
    }

    public async Task<IngestPipeline> BuildPipelineAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var mapper = new PrincipalMapper(IdentityStore);
        var resolver = new AclResolver(mapper, snapshot);
        var converter = new Item.ItemConverter(Config);

        HaCoordinator? ha = null;
        if (EnvFlags.HaMode)
        {
            if (!EnvFlags.UseSqlServer)
            {
                throw new ArgumentException(
                    "Invalid configuration: HA_MODE=true requires USE_SQL_SERVER=true "
                    + "and SQL_CONNECTION_STRING (shared state backend).");
            }
            ha = new HaCoordinator(new SqlExecutor());
        }

        var pipeline = new IngestPipeline(Config, Schema, Clarizen, Graph, resolver, converter, ha)
        {
            OnProgress = Dashboard.ReportProgress,
        };
        return pipeline;
    }

    public void Dispose()
    {
        Health?.Dispose();
        // Flush + dispose the tracer provider (bounded) so a graceful stop ships
        // buffered spans without a dead collector hanging shutdown.
        Tracing?.Dispose();
        IdentityStore.Dispose();
    }
}

internal static class Runtime
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector");

    /// <summary>Standard command bootstrap. Throws ArgumentException on bad config.</summary>
    public static RuntimeContext Create(ParsedArgs args, string runPrefix)
    {
        EnvLoader.LoadLayered();
        Logging.Initialize(runPrefix, args.Verbose);
        LogPruner.PruneIfConfigured(Logging.LogsRoot);

        var config = AppConfig.Load();
        Alerting.ConnectorId = config.ConnectorId;
        var schema = SchemaConfig.Load(SchemaConfig.DefaultPath);

        // Distributed tracing: registers an OTLP exporter only when
        // OTEL_EXPORTER_OTLP_ENDPOINT is set; otherwise a cheap no-op.
        var tracing = Infrastructure.Tracing.Initialize(config.ConnectorName);

        // Circuit breakers per external dependency (CIRCUIT_BREAKER, default on
        // but inert on the happy path; CIRCUIT_BREAKER=false = pure passthrough).
        Breakers.Initialize(CircuitBreakerOptions.FromEnv());

        var clarizen = new ClarizenClient(config, breaker: Breakers.Clarizen);
        var graph = new GraphClient(config, breaker: Breakers.Graph);
        var connection = new ConnectionManager(config, graph);
        var identityStore = IdentityStore.Open(config.ConnectorId);
        var identitySync = new IdentitySync(clarizen, graph);

        var context = new RuntimeContext
        {
            Config = config,
            Schema = schema,
            Clarizen = clarizen,
            Graph = graph,
            Connection = connection,
            IdentityStore = identityStore,
            IdentitySync = identitySync,
            Tracing = tracing,
        };
        // Under sharding each shard dead-letters against its own connection id,
        // so the live depth is the sum across shards.
        context.Health = HealthEndpoint.StartIfConfigured(
            () => ShardingConfig.EffectiveConnectionIds(config, schema)
                .Sum(id => SyncState.ReadFailedRecords(id).Count));

        // Ctrl+C = the same graceful stop as an SCM stop.
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Logger.Warning("Ctrl+C — finishing the current chunk and saving the checkpoint...");
            ServiceStop.Request();
        };

        Logger.Info($"Connector '{config.ConnectorId}' initialized (run dir: {Logging.RunDirectory}).");
        return context;
    }
}
