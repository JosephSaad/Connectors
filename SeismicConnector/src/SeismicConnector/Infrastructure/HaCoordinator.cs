// Infrastructure/HaCoordinator.cs
// -------------------------------
// Active-active multi-node crawl coordination (HA_MODE=true, requires the SQL
// backend — docs/HA.md).
//
// Crawl sessions: when a cycle is due each node calls OpenOrJoinCrawl —
// exactly one INSERTs the dbo.CrawlSessions row (guarded by a filtered unique
// index on open sessions), the rest join it. Resources (teamsites, the
// Library object) are rows in dbo.CrawlClaims keyed (CrawlId, ResourceKey): a
// node claims one, heartbeats while working (HA_HEARTBEAT_SECONDS), and marks
// it done|failed on completion. A claim whose heartbeat is older than
// HA_CLAIM_TIMEOUT_SECONDS is stolen with a guarded UPDATE.
//
// Close-with-failed-claims semantics (pinned; see CloseDecision + tests):
//   * the crawl CLOSES even when some claims failed — the session status is
//     then 'failed' instead of 'closed' (failures are dead-lettered and
//     re-driven by retry-failed, not by blocking the close);
//   * claims still in 'claimed' state block the close (work in flight);
//   * exactly ONE node ever wins the open→closed/failed UPDATE (ClosedBy is
//     recorded), and only that node writes the sync timestamp;
//   * the result is derived from ClosedBy = @NodeId, so a transient retry of
//     a close whose COMMIT succeeded but whose ack was lost still reports
//     true — it stays exactly-one under concurrency.
//
// The claim/steal and close decisions are pure functions (TryDecide /
// CloseDecision) so the contention logic is unit-testable without SQL Server.

using Microsoft.Data.SqlClient;

namespace SeismicConnector.Infrastructure;

/// <summary>Result of <see cref="HaCoordinator.OpenOrJoinCrawl"/>.</summary>
public sealed record HaCrawlHandle(Guid CrawlId, bool Created);

/// <summary>Final state of a coordinated crawl close attempt.</summary>
public enum HaCloseResult
{
    /// <summary>Claims are still in flight — the crawl stays open.</summary>
    StillOpen,

    /// <summary>This node performed (or provably owns) the close.</summary>
    ClosedByThisNode,

    /// <summary>Another node closed (or will close) the crawl.</summary>
    ClosedElsewhere,
}

/// <summary>
/// Unsealed with virtual SQL-touching members so tests can substitute an
/// in-memory coordinator and exercise multi-node claim behaviour without a
/// SQL Server (the pure decision seams below stay the single source of truth
/// for the contention logic).
/// </summary>
public class HaCoordinator
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.ha");

    private readonly string _connectorId;

    public string NodeId { get; }

    public static bool Enabled => EnvFlags.IsTrue("HA_MODE");

    public static int ClaimTimeoutSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("HA_CLAIM_TIMEOUT_SECONDS"), out var v) && v > 0
            ? v
            : 300;

    public static int HeartbeatSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("HA_HEARTBEAT_SECONDS"), out var v) && v > 0
            ? v
            : 60;

    public HaCoordinator(string connectorId, string? nodeId = null)
    {
        _connectorId = connectorId;
        NodeId = nodeId
            ?? Environment.GetEnvironmentVariable("NODE_ID")
            ?? Environment.MachineName;
    }

    // ── pure decision seams (unit-tested without SQL) ────────────────────────

    /// <summary>
    /// Pure claim decision: given the current owner/heartbeat of a claim row
    /// (or null when the row does not exist), decide whether
    /// <paramref name="nodeId"/> may take the resource.
    /// </summary>
    internal static bool TryDecide(
        (string OwnerNodeId, DateTime HeartbeatUtc)? existing,
        string nodeId,
        DateTime nowUtc,
        int claimTimeoutSeconds)
    {
        if (existing is null)
            return true;                                        // unclaimed
        if (existing.Value.OwnerNodeId == nodeId)
            return true;                                        // already ours (re-entrant)
        return (nowUtc - existing.Value.HeartbeatUtc).TotalSeconds > claimTimeoutSeconds;  // stale → steal
    }

    /// <summary>
    /// Pure crawl-close decision (the pinned close-with-failed-claims
    /// semantics). Inputs are the crawl's current observable state; the
    /// output says whether the open→final UPDATE should run, which final
    /// status it writes, and what THIS node must report.
    /// </summary>
    /// <param name="anyClaimedRemaining">Any claim rows still in 'claimed' state.</param>
    /// <param name="anyFailedClaims">Any claim rows in 'failed' state.</param>
    /// <param name="sessionStatus">Current session status: open|closed|failed.</param>
    /// <param name="closedBy">Session's ClosedBy (null while open).</param>
    /// <param name="nodeId">The calling node.</param>
    internal static (bool PerformClose, string FinalStatus, HaCloseResult Result) CloseDecision(
        bool anyClaimedRemaining,
        bool anyFailedClaims,
        string sessionStatus,
        string? closedBy,
        string nodeId)
    {
        // Work still in flight → nobody closes.
        if (sessionStatus == "open" && anyClaimedRemaining)
            return (false, "open", HaCloseResult.StillOpen);

        // The crawl closes EVEN WITH failed claims — status records the failure.
        var finalStatus = anyFailedClaims ? "failed" : "closed";

        if (sessionStatus == "open")
            return (true, finalStatus, HaCloseResult.ClosedByThisNode);

        // Already closed: a commit-ack-loss retry by the closing node must
        // still report true; anyone else reports false. Exactly one node ever
        // has ClosedBy == its id, so this stays exactly-one under concurrency.
        return (false, sessionStatus,
            closedBy == nodeId ? HaCloseResult.ClosedByThisNode : HaCloseResult.ClosedElsewhere);
    }

    // ── crawl sessions ───────────────────────────────────────────────────────

    /// <summary>
    /// Open this cycle's crawl session, or join the one another node already
    /// opened. Single-node mode (HA off) returns a fresh local handle without
    /// touching SQL.
    /// </summary>
    public virtual HaCrawlHandle OpenOrJoinCrawl(string crawlKind, string? sinceIso)
    {
        if (!Enabled)
            return new HaCrawlHandle(Guid.NewGuid(), Created: true);

        return SqlExecutor.Execute(connection =>
        {
            // Join an open session when one exists.
            using (var find = connection.CreateCommand())
            {
                find.CommandText = """
                    SELECT TOP 1 CrawlId FROM dbo.CrawlSessions
                    WHERE ConnectorId = @ConnectorId AND Status = 'open'
                    ORDER BY OpenedUtc DESC
                    """;
                find.Parameters.AddWithValue("@ConnectorId", _connectorId);
                if (find.ExecuteScalar() is Guid existing)
                {
                    Logger.Info($"[HA] Node {NodeId} joined open crawl {existing}");
                    return new HaCrawlHandle(existing, Created: false);
                }
            }

            var crawlId = Guid.NewGuid();
            try
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO dbo.CrawlSessions
                        (CrawlId, ConnectorId, CrawlKind, SinceIso, Status, OpenedUtc, OpenedBy)
                    VALUES (@CrawlId, @ConnectorId, @CrawlKind, @SinceIso, 'open', SYSUTCDATETIME(), @NodeId);
                    """;
                insert.Parameters.AddWithValue("@CrawlId", crawlId);
                insert.Parameters.AddWithValue("@ConnectorId", _connectorId);
                insert.Parameters.AddWithValue("@CrawlKind", crawlKind);
                insert.Parameters.AddWithValue("@SinceIso", (object?)sinceIso ?? DBNull.Value);
                insert.Parameters.AddWithValue("@NodeId", NodeId);
                insert.ExecuteNonQuery();
                Logger.Info($"[HA] Node {NodeId} opened crawl {crawlId} ({crawlKind})");
                return new HaCrawlHandle(crawlId, Created: true);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // Another node won the race on the open-session unique index — join theirs.
                using var refind = connection.CreateCommand();
                refind.CommandText = """
                    SELECT TOP 1 CrawlId FROM dbo.CrawlSessions
                    WHERE ConnectorId = @ConnectorId AND Status = 'open'
                    """;
                refind.Parameters.AddWithValue("@ConnectorId", _connectorId);
                var joined = (Guid)refind.ExecuteScalar()!;
                Logger.Info($"[HA] Node {NodeId} joined open crawl {joined} after insert race");
                return new HaCrawlHandle(joined, Created: false);
            }
        });
    }

    /// <summary>
    /// Close the crawl when no 'claimed' rows remain. The crawl closes even
    /// when some claims FAILED (status 'failed'); exactly one node reports
    /// <see cref="HaCloseResult.ClosedByThisNode"/> and records sync state.
    /// </summary>
    public virtual HaCloseResult TryCloseCrawl(Guid crawlId)
    {
        if (!Enabled)
            return HaCloseResult.ClosedByThisNode;  // single-node: always the closer

        return SqlExecutor.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            bool anyClaimed, anyFailed;
            string sessionStatus;
            string? closedBy;

            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM dbo.CrawlClaims WITH (UPDLOCK, HOLDLOCK)
                          WHERE CrawlId = @CrawlId AND Status = 'claimed'),
                        (SELECT COUNT(*) FROM dbo.CrawlClaims
                          WHERE CrawlId = @CrawlId AND Status = 'failed'),
                        (SELECT Status   FROM dbo.CrawlSessions WHERE CrawlId = @CrawlId),
                        (SELECT ClosedBy FROM dbo.CrawlSessions WHERE CrawlId = @CrawlId);
                    """;
                read.Parameters.AddWithValue("@CrawlId", crawlId);
                using var reader = read.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException($"Crawl session {crawlId} not found.");
                anyClaimed = reader.GetInt32(0) > 0;
                anyFailed = reader.GetInt32(1) > 0;
                sessionStatus = reader.IsDBNull(2) ? "open" : reader.GetString(2);
                closedBy = reader.IsDBNull(3) ? null : reader.GetString(3);
            }

            var (performClose, finalStatus, result) =
                CloseDecision(anyClaimed, anyFailed, sessionStatus, closedBy, NodeId);

            if (performClose)
            {
                using var close = connection.CreateCommand();
                close.Transaction = transaction;
                close.CommandText = """
                    UPDATE dbo.CrawlSessions
                       SET Status = @Status, ClosedUtc = SYSUTCDATETIME(), ClosedBy = @NodeId
                     WHERE CrawlId = @CrawlId AND Status = 'open';
                    """;
                close.Parameters.AddWithValue("@Status", finalStatus);
                close.Parameters.AddWithValue("@NodeId", NodeId);
                close.Parameters.AddWithValue("@CrawlId", crawlId);
                var won = close.ExecuteNonQuery() > 0;
                if (!won)
                {
                    // Lost the open→closed race inside the window — re-derive from ClosedBy.
                    result = ReadClosedBy(connection, transaction, crawlId) == NodeId
                        ? HaCloseResult.ClosedByThisNode
                        : HaCloseResult.ClosedElsewhere;
                }
                else if (finalStatus == "failed")
                {
                    Logger.Warning($"[HA] Crawl {crawlId} closed as FAILED (some claims failed).");
                }
            }

            transaction.Commit();
            return result;
        });
    }

    private static string? ReadClosedBy(SqlConnection connection, SqlTransaction transaction, Guid crawlId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT ClosedBy FROM dbo.CrawlSessions WHERE CrawlId = @CrawlId";
        cmd.Parameters.AddWithValue("@CrawlId", crawlId);
        return cmd.ExecuteScalar() as string;
    }

    // ── resource claims ──────────────────────────────────────────────────────

    private string Key(string resource) => $"{_connectorId}:{resource}";

    /// <summary>Attempt to claim <paramref name="resource"/> within <paramref name="crawlId"/>.</summary>
    public virtual bool TryClaim(Guid crawlId, string resource)
    {
        if (!Enabled)
            return true;  // single-node mode: everything is ours

        var key = Key(resource);
        return SqlExecutor.Execute(connection =>
        {
            var existing = ReadClaim(connection, crawlId, key);
            if (existing is { Status: "done" or "failed" })
                return false;  // already finished by someone this crawl
            var lease = existing is null
                ? ((string, DateTime)?)null
                : (existing.Value.NodeId, existing.Value.HeartbeatUtc);
            if (!TryDecide(lease, NodeId, DateTime.UtcNow, ClaimTimeoutSeconds))
                return false;

            try
            {
                using var cmd = connection.CreateCommand();
                if (existing is null)
                {
                    cmd.CommandText = """
                        INSERT INTO dbo.CrawlClaims (CrawlId, ResourceKey, NodeId, Status, ClaimedUtc, HeartbeatUtc)
                        VALUES (@CrawlId, @Key, @NodeId, 'claimed', SYSUTCDATETIME(), SYSUTCDATETIME());
                        """;
                }
                else
                {
                    // Guarded steal/renew: only wins if the row still looks the way we read it.
                    cmd.CommandText = """
                        UPDATE dbo.CrawlClaims
                        SET NodeId = @NodeId, Status = 'claimed',
                            ClaimedUtc = SYSUTCDATETIME(), HeartbeatUtc = SYSUTCDATETIME()
                        WHERE CrawlId = @CrawlId AND ResourceKey = @Key
                          AND NodeId = @PrevNode AND HeartbeatUtc = @PrevHeartbeat;
                        """;
                    cmd.Parameters.AddWithValue("@PrevNode", existing.Value.NodeId);
                    cmd.Parameters.AddWithValue("@PrevHeartbeat", existing.Value.HeartbeatUtc);
                }
                cmd.Parameters.AddWithValue("@CrawlId", crawlId);
                cmd.Parameters.AddWithValue("@Key", key);
                cmd.Parameters.AddWithValue("@NodeId", NodeId);
                var affected = cmd.ExecuteNonQuery();
                if (affected > 0)
                {
                    Metrics.IncHaClaimsAcquired();
                    Metrics.AddHaClaimsHeld(1);
                    Logger.Info($"[HA] Node {NodeId} claimed '{resource}' (crawl {crawlId})");
                }
                return affected > 0;
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                return false;  // another node inserted first
            }
        });
    }

    /// <summary>Refresh the heartbeat on a held claim.</summary>
    public virtual void Heartbeat(Guid crawlId, string resource)
    {
        if (!Enabled)
            return;
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE dbo.CrawlClaims SET HeartbeatUtc = SYSUTCDATETIME()
                WHERE CrawlId = @CrawlId AND ResourceKey = @Key AND NodeId = @NodeId AND Status = 'claimed';
                """;
            cmd.Parameters.AddWithValue("@CrawlId", crawlId);
            cmd.Parameters.AddWithValue("@Key", Key(resource));
            cmd.Parameters.AddWithValue("@NodeId", NodeId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Mark a held claim done|failed (no-op if another node stole it).</summary>
    public virtual void CompleteClaim(Guid crawlId, string resource, bool succeeded)
    {
        if (!Enabled)
            return;
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE dbo.CrawlClaims SET Status = @Status, HeartbeatUtc = SYSUTCDATETIME()
                WHERE CrawlId = @CrawlId AND ResourceKey = @Key AND NodeId = @NodeId AND Status = 'claimed';
                """;
            cmd.Parameters.AddWithValue("@Status", succeeded ? "done" : "failed");
            cmd.Parameters.AddWithValue("@CrawlId", crawlId);
            cmd.Parameters.AddWithValue("@Key", Key(resource));
            cmd.Parameters.AddWithValue("@NodeId", NodeId);
            if (cmd.ExecuteNonQuery() > 0)
                Metrics.AddHaClaimsHeld(-1);
        });
    }

    private static (string NodeId, string Status, DateTime HeartbeatUtc)? ReadClaim(
        SqlConnection connection, Guid crawlId, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT NodeId, Status, HeartbeatUtc FROM dbo.CrawlClaims
            WHERE CrawlId = @CrawlId AND ResourceKey = @Key
            """;
        cmd.Parameters.AddWithValue("@CrawlId", crawlId);
        cmd.Parameters.AddWithValue("@Key", key);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return (reader.GetString(0), reader.GetString(1),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
    }
}
