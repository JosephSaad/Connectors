// Commands/Runtime.cs
// -------------------
// Composition root: loads config and wires the state store, identity store,
// Graph client, seat service, alerting and (optionally) the HA coordinator
// for a command run. Dispose releases the identity store handle.

using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.Ingestion;
using AltrataConnector.State;

namespace AltrataConnector.Commands;

public sealed class Runtime : IDisposable
{
    public required AppConfig Config { get; init; }
    public required IStateStore State { get; init; }
    public required IIdentityStore Identity { get; init; }
    public required IGraphClient Graph { get; init; }
    public required SeatService Seats { get; init; }
    public required Alerting Alerts { get; init; }
    public required AuditLog Audit { get; init; }
    public required ErasureLedger Erasure { get; init; }
    /// <summary>The Graph dependency breaker (critical — drives readiness).</summary>
    public required CircuitBreaker GraphBreaker { get; init; }
    /// <summary>The Altrata enrichment-API breaker (non-critical).</summary>
    public required CircuitBreaker ApiBreaker { get; init; }
    public HaCoordinator? Ha { get; init; }

    /// <summary>Breaker snapshots for /metrics and /health readiness.</summary>
    public IReadOnlyList<BreakerSnapshot> BreakerSnapshots() =>
        new[] { GraphBreaker.Snapshot(), ApiBreaker.Snapshot() };

    /// <summary>True when a CRITICAL breaker is Open — degraded, not ready.</summary>
    public bool IsDegraded() => GraphBreaker.IsOpen;

    public CrawlEngine CreateEngine() =>
        new(Config, Graph, State, Identity, Seats, Alerts, Ha);

    public static Runtime Create()
    {
        var config = AppConfig.Load();
        return CreateFor(config);
    }

    /// <summary>Build a fully wired runtime for a given config (base or shard).</summary>
    public static Runtime CreateFor(AppConfig config)
    {
        IStateStore state = config.UseSqlServer
            ? new SqlStateStore(config.SqlConnectionString!, config.ConnectorId,
                config.SqlUseManagedIdentity, config.SqlMaxRetries)
            : new FileStateStore(config.ConnectorId);

        IIdentityStore identity = config.UseSqlServer
            ? new SqlServerIdentityStore(config.SqlConnectionString!, config.ConnectorId,
                config.SqlUseManagedIdentity)
            : new SqliteIdentityStore(config.ConnectorId);

        var graphBreaker = new CircuitBreaker("graph", CircuitBreakerOptions.FromEnv(critical: true));
        var apiBreaker = new CircuitBreaker("altrata-api", CircuitBreakerOptions.FromEnv(critical: false));

        var graph = new GraphClient(config, breaker: graphBreaker);
        var seats = new SeatService(config, identity, state);
        var alerts = new Alerting(config.ConnectorId);
        var audit = new AuditLog(config.ConnectorId);
        var erasure = new ErasureLedger(config.ConnectorId);

        HaCoordinator? ha = null;
        if (config.HaMode)
            ha = new HaCoordinator(new SqlLeaseStore(config.SqlConnectionString!, config.SqlUseManagedIdentity));

        return new Runtime
        {
            Config = config,
            State = state,
            Identity = identity,
            Graph = graph,
            Seats = seats,
            Alerts = alerts,
            Audit = audit,
            Erasure = erasure,
            GraphBreaker = graphBreaker,
            ApiBreaker = apiBreaker,
            Ha = ha,
        };
    }

    /// <summary>Clone this runtime bound to a shard's connection id. The
    /// connection id is the state key, so each shard carries its own
    /// checkpoint, dead-letter queue, identity DB and seat hash.</summary>
    public Runtime CreateForShard(Shard shard) =>
        CreateFor(ShardingConfig.ForShard(Config, shard));

    /// <summary>
    /// Dead-letter depth for /metrics — REPLAYABLE records only (transform
    /// failures have their own gauge and never trip the replay alert). Under
    /// connection sharding each shard dead-letters against its own connection
    /// id, so the depth is the sum across shard stores; otherwise the base
    /// connector's queue is the whole story.
    /// </summary>
    public int DeadLetterDepth()
    {
        if (!ShardingConfig.TryLoad(out var shards, out _) || shards.Count == 0)
            return State.ReadDeadLetters().Count(r => r.IsReplayable);

        var depth = 0;
        foreach (var shard in shards)
        {
            IStateStore shardState = Config.UseSqlServer
                ? new SqlStateStore(Config.SqlConnectionString!, shard.ConnectionId,
                    Config.SqlUseManagedIdentity, Config.SqlMaxRetries)
                : new FileStateStore(shard.ConnectionId);
            depth += shardState.ReadDeadLetters().Count(r => r.IsReplayable);
        }
        return depth;
    }

    public void Dispose() => Identity.Dispose();
}
