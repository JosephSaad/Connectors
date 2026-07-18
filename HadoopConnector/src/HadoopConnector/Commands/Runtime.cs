// Commands/Runtime.cs
// -------------------
// Shared command bootstrap: layered env loading, logging init, log pruning,
// config + schema + filter loading, client construction, and the assembled
// ingestion pipeline (user directory → mapper → resolver → converter).

using HadoopConnector.AclEngine;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Commands;

public sealed class RuntimeContext : IDisposable
{
    public required AppConfig Config { get; init; }
    public required SchemaConfig Schema { get; init; }
    public required FilterSet Filters { get; init; }
    public required IBdhSource Source { get; init; }
    public required BdhFetcher Fetcher { get; init; }
    public required GraphClient Graph { get; init; }
    public required ConnectionManager Connection { get; init; }
    public required IIdentityStore IdentityStore { get; init; }
    public required IdentitySync IdentitySync { get; init; }

    public IDisposable? Health { get; set; }

    /// <summary>OpenTelemetry provider handle (null when tracing is off).</summary>
    public IDisposable? Tracing { get; set; }

    private UserDirectory? _directory;

    /// <summary>Load (and cache) the BDH user directory for ACL resolution.</summary>
    public async Task<UserDirectory> GetUserDirectoryAsync(CancellationToken ct = default) =>
        _directory ??= await IdentitySync.LoadDirectoryAsync(ct).ConfigureAwait(false);

    /// <summary>Invalidate the cached directory (fresh crawl cycles reload it).</summary>
    public void InvalidateUserDirectory() => _directory = null;

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

    public Task<IngestPipeline> BuildPipelineAsync(CancellationToken ct = default)
    {
        var mapper = new PrincipalMapper(IdentityStore);
        var resolver = new AclResolver(mapper);
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

        var pipeline = new IngestPipeline(Config, Schema, Fetcher, Graph, resolver, converter, ha)
        {
            OnProgress = Dashboard.ReportProgress,
        };
        return Task.FromResult(pipeline);
    }

    public void Dispose()
    {
        Health?.Dispose();
        // Flush + dispose the tracer provider (bounded) so a graceful stop ships
        // buffered spans without a dead collector hanging shutdown.
        Tracing?.Dispose();
        IdentityStore.Dispose();
        Source.Dispose();
        // Lifecycle-stop event (id 1001) for SIEM collectors; no-op when the
        // sink is off. Last, so any disposal warnings above are still mirrored.
        EventLogSink.Shutdown();
    }
}

internal static class Runtime
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    /// <summary>Build the configured BDH source (webhdfs | localpath).</summary>
    internal static IBdhSource CreateSource(AppConfig config, CircuitBreaker? breaker = null)
    {
        if (string.Equals(config.HdfsMode, "localpath", StringComparison.OrdinalIgnoreCase))
            return new LocalPathSource(config.BdhExportPath!);
        return new WebHdfsClient(config, breaker: breaker ?? Breakers.Hdfs);
    }

    /// <summary>Standard command bootstrap. Throws ArgumentException on bad config.</summary>
    public static RuntimeContext Create(ParsedArgs args, string runPrefix)
    {
        EnvLoader.LoadLayered();
        // Restrictive filesystem permissions FIRST: the local state directories
        // hold a second copy of business data (dead-letter payloads, identity /
        // inventory DBs, run logs), so create them owner-only (0700 POSIX; NTFS
        // owner+admins best-effort on Windows) before anything writes into them.
        // Best-effort — never fatal.
        SecureDirectories.HardenStartupDirectories(
            Logging.LogsRoot,
            Config.SyncState.LogsDir,
            Graph.IdentityStore.DataDir,
            Graph.ItemInventory.DataDir);
        Logging.Initialize(runPrefix, args.Verbose);
        // Windows Event Log mirroring (EVENTLOG_ENABLED; no-op off-Windows).
        // After Logging.Initialize so the lifecycle-start event carries the pid
        // of a fully bootstrapped logger; before AppConfig.Load so config
        // failures already reach the event log.
        EventLogSink.Initialize();
        LogPruner.PruneIfConfigured(Logging.LogsRoot);

        var config = AppConfig.Load();
        Alerting.ConnectorId = config.ConnectorId;
        var schema = SchemaConfig.Load(SchemaConfig.DefaultPath);
        var filters = FilterSet.Load(
            Environment.GetEnvironmentVariable("BDH_FILTERS_PATH") ?? FilterSet.DefaultPath);

        // Distributed tracing: registers an OTLP exporter only when
        // OTEL_EXPORTER_OTLP_ENDPOINT is set; otherwise a cheap no-op.
        var tracing = Infrastructure.Tracing.Initialize(config.ConnectorName);

        // Circuit breakers per external dependency (CIRCUIT_BREAKER, default on
        // but inert on the happy path; CIRCUIT_BREAKER=false = pure passthrough).
        Breakers.Initialize(CircuitBreakerOptions.FromEnv());

        var source = CreateSource(config);
        var fetcher = new BdhFetcher(config, source, filters);
        var graph = new GraphClient(config, breaker: Breakers.Graph);
        var connection = new ConnectionManager(config, graph);
        var identityStore = IdentityStore.Open(config.ConnectorId);
        var identitySync = new IdentitySync(fetcher, graph, config);

        var context = new RuntimeContext
        {
            Config = config,
            Schema = schema,
            Filters = filters,
            Source = source,
            Fetcher = fetcher,
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

        Logger.Info(
            $"Connector '{config.ConnectorId}' initialized "
            + $"(source: {source.Description}; run dir: {Logging.RunDirectory}).");
        return context;
    }
}
