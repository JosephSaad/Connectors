// Infrastructure/HaCoordinator.cs
// -------------------------------
// HA_MODE=true active-active crawling. Multiple nodes share the SQL backend;
// before a node starts a crawl unit (a feed delivery, the seat-sync pass, ...)
// it must win the lease for that unit. Leases have a TTL and are renewed
// while the unit is running; a crashed node's lease simply expires and another
// node picks the unit up. See docs/HA.md.

using Microsoft.Data.SqlClient;

namespace AltrataConnector.Infrastructure;

public interface ILeaseStore
{
    /// <summary>Try to acquire (or re-acquire) a named lease. True on success.</summary>
    bool TryAcquire(string leaseName, string owner, TimeSpan ttl, DateTime utcNow);

    /// <summary>Extend a lease we own. True while still the owner.</summary>
    bool Renew(string leaseName, string owner, TimeSpan ttl, DateTime utcNow);

    /// <summary>Release a lease we own (no-op when not the owner).</summary>
    void Release(string leaseName, string owner);
}

/// <summary>In-memory lease store (tests / single-node dry runs).</summary>
public sealed class InMemoryLeaseStore : ILeaseStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (string Owner, DateTime ExpiresUtc)> _leases = new();

    public bool TryAcquire(string leaseName, string owner, TimeSpan ttl, DateTime utcNow)
    {
        lock (_sync)
        {
            if (_leases.TryGetValue(leaseName, out var lease)
                && lease.ExpiresUtc > utcNow
                && lease.Owner != owner)
            {
                return false;  // held by someone else and not expired
            }
            _leases[leaseName] = (owner, utcNow + ttl);
            return true;
        }
    }

    public bool Renew(string leaseName, string owner, TimeSpan ttl, DateTime utcNow)
    {
        lock (_sync)
        {
            if (!_leases.TryGetValue(leaseName, out var lease) || lease.Owner != owner)
                return false;
            _leases[leaseName] = (owner, utcNow + ttl);
            return true;
        }
    }

    public void Release(string leaseName, string owner)
    {
        lock (_sync)
        {
            if (_leases.TryGetValue(leaseName, out var lease) && lease.Owner == owner)
                _leases.Remove(leaseName);
        }
    }
}

/// <summary>SQL Server lease store over dbo.altrata_leases (shared HA backend).</summary>
public sealed class SqlLeaseStore : ILeaseStore
{
    private readonly string _connectionString;

    public SqlLeaseStore(string connectionString, bool useManagedIdentity = false)
    {
        _connectionString = State.SqlStateStore.BuildConnectionString(connectionString, useManagedIdentity);
    }

    public bool TryAcquire(string leaseName, string owner, TimeSpan ttl, DateTime utcNow)
    {
        using var conn = Open();
        using var cmd = new SqlCommand("""
            MERGE dbo.altrata_leases WITH (HOLDLOCK) AS target
            USING (SELECT @name AS lease_name) AS source
            ON target.lease_name = source.lease_name
            WHEN MATCHED AND (target.expires_utc <= @now OR target.owner = @owner)
                THEN UPDATE SET owner = @owner, expires_utc = @expires
            WHEN NOT MATCHED
                THEN INSERT (lease_name, owner, expires_utc) VALUES (@name, @owner, @expires);
            SELECT owner FROM dbo.altrata_leases WHERE lease_name = @name;
            """, conn);
        cmd.Parameters.AddWithValue("@name", leaseName);
        cmd.Parameters.AddWithValue("@owner", owner);
        cmd.Parameters.AddWithValue("@now", utcNow);
        cmd.Parameters.AddWithValue("@expires", utcNow + ttl);
        return (string?)cmd.ExecuteScalar() == owner;
    }

    public bool Renew(string leaseName, string owner, TimeSpan ttl, DateTime utcNow)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "UPDATE dbo.altrata_leases SET expires_utc = @expires " +
            "WHERE lease_name = @name AND owner = @owner", conn);
        cmd.Parameters.AddWithValue("@expires", utcNow + ttl);
        cmd.Parameters.AddWithValue("@name", leaseName);
        cmd.Parameters.AddWithValue("@owner", owner);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void Release(string leaseName, string owner)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "DELETE FROM dbo.altrata_leases WHERE lease_name = @name AND owner = @owner", conn);
        cmd.Parameters.AddWithValue("@name", leaseName);
        cmd.Parameters.AddWithValue("@owner", owner);
        cmd.ExecuteNonQuery();
    }

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }
}

/// <summary>Node-level coordinator wrapping a lease store.</summary>
public sealed class HaCoordinator
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.ha");

    public static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(5);

    private readonly ILeaseStore _leases;

    public string NodeId { get; }

    public HaCoordinator(ILeaseStore leases, string? nodeId = null)
    {
        _leases = leases;
        NodeId = nodeId
            ?? Environment.GetEnvironmentVariable("HA_NODE_ID")
            ?? $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    // Lease ops fail fast when the shared SQL backend is down (HA cannot
    // coordinate without it — unchanged), but each op logs WHICH unit and node
    // failed before the raw SqlException flies: a bare "login failed" stack
    // does not say the crawl died acquiring "delivery:D42".

    public bool TryAcquire(string unit, TimeSpan? ttl = null, DateTime? utcNow = null)
    {
        try
        {
            var won = _leases.TryAcquire(unit, NodeId, ttl ?? DefaultLeaseTtl, utcNow ?? DateTime.UtcNow);
            if (won)
                Metrics.Increment("altrata_ha_leases_held", 1);   // best-effort per-process gauge
            return won;
        }
        catch (Exception exc)
        {
            Logger.Error($"[HA] Lease acquire FAILED for '{unit}' (node {NodeId}): {exc.GetType().Name}: {exc.Message}");
            throw;
        }
    }

    public bool Renew(string unit, TimeSpan? ttl = null, DateTime? utcNow = null)
    {
        try
        {
            return _leases.Renew(unit, NodeId, ttl ?? DefaultLeaseTtl, utcNow ?? DateTime.UtcNow);
        }
        catch (Exception exc)
        {
            Logger.Error($"[HA] Lease renew FAILED for '{unit}' (node {NodeId}): {exc.GetType().Name}: {exc.Message}");
            throw;
        }
    }

    public void Release(string unit)
    {
        try
        {
            _leases.Release(unit, NodeId);
            Metrics.Increment("altrata_ha_leases_held", -1);   // paired with TryAcquire above
        }
        catch (Exception exc)
        {
            // Still rethrows (release runs in finally blocks; masking rules are
            // the caller's) — but the log records that the lease was left to
            // EXPIRE on its TTL rather than being released.
            Logger.Error($"[HA] Lease release FAILED for '{unit}' (node {NodeId}): {exc.GetType().Name}: {exc.Message} " +
                         "— the lease will expire on its TTL instead.");
            throw;
        }
    }

    /// <summary>How long a crawl-close lease is pinned to the closing node.</summary>
    public static readonly TimeSpan CloseLeaseTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Close-with-failed-claims semantics (pinned to match the reference
    /// connector's usp_CloseCrawlIfComplete):
    ///
    ///   * Exactly ONE node wins the close for a given crawl id — that node
    ///     records the sync timestamp; every other node skips it.
    ///   * The win is idempotent for the winner: a retry by the same node
    ///     (e.g. after a lost ack) still reports true, because the close
    ///     lease is re-entrant for its owner.
    ///   * The crawl closes even when some claims FAILED (a failed delivery
    ///     never wedges the crawl open); the recorded status is then
    ///     "failed" instead of "closed" so operators can see it, and the
    ///     failure is visible in the state store under crawl_status_{crawlId}.
    /// </summary>
    public bool TryCloseCrawl(string crawlId, bool anyFailedClaims, State.IStateStore state,
        DateTime? utcNow = null)
    {
        try
        {
            var won = _leases.TryAcquire($"crawl-close:{crawlId}", NodeId, CloseLeaseTtl,
                utcNow ?? DateTime.UtcNow);
            if (won)
            {
                state.SetValue($"crawl_status_{crawlId}", anyFailedClaims ? "failed" : "closed");
                state.SetValue($"crawl_closed_by_{crawlId}", NodeId);
            }
            return won;
        }
        catch (Exception exc)
        {
            Logger.Error($"[HA] Crawl close FAILED for '{crawlId}' (node {NodeId}): {exc.GetType().Name}: {exc.Message} " +
                         "— no sync timestamp recorded; the crawl close retries on the next run.");
            throw;
        }
    }
}
