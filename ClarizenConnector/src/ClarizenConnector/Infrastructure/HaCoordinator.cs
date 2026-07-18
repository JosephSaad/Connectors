// Infrastructure/HaCoordinator.cs
// -------------------------------
// Active-active multi-node crawl coordination (HA_MODE=true, requires the SQL
// Server state backend). Nodes running the same --continuous command against
// the same database coordinate through two tables (see
// scripts/sql/create-database.sql and docs/HA.md):
//
//   CrawlRuns    — one row per scheduled crawl (kind + schedule slot). The
//                  first node to insert the row "opens" the crawl; the others
//                  join it. Exactly one node closes it.
//   ObjectClaims — lease/lock rows: each Clarizen object type is an atomic
//                  work item claimed by one node with a heartbeat. A claim
//                  whose heartbeat is older than HA_CLAIM_TIMEOUT_SECONDS is
//                  reclaimable by a survivor, which resumes from that object
//                  type's checkpoint.
//
// Close-with-failed-claims semantics (pinned, matching the SF connector):
// 'failed' is a TERMINAL claim status — a crashed object worker marks its
// claim failed (the crash is dead-lettered) and the crawl still closes once
// no claim is left pending/claimed. The crawl row records Status='failed'
// when any claim failed, 'closed' otherwise. Exactly one node ever wins the
// open→closed transition (ClosedBy stamps the winner, so a commit-ack-loss
// retry by that node still reports true); the winner records the sync
// timestamp either way — dead-letter retry is the recovery path.
//
// All statements are single-row, parameterized and idempotent so any node can
// crash at any point without wedging the crawl.

using System.Globalization;

namespace ClarizenConnector.Infrastructure;

public sealed class HaCoordinator
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.ha");

    private readonly ISqlGateway _sql;

    public HaCoordinator(ISqlGateway sql, string? nodeId = null)
    {
        _sql = sql;
        NodeId = string.IsNullOrWhiteSpace(nodeId)
            ? EnvFlags.GetString("NODE_ID", Environment.MachineName)
            : nodeId;
        ClaimTimeoutSeconds = EnvFlags.GetInt("HA_CLAIM_TIMEOUT_SECONDS", 300);
        HeartbeatSeconds = EnvFlags.GetInt("HA_HEARTBEAT_SECONDS", 60);
    }

    public string NodeId { get; }

    public int ClaimTimeoutSeconds { get; }

    public int HeartbeatSeconds { get; }

    /// <summary>Pure lease-expiry rule: a claim is reclaimable when its heartbeat is stale.</summary>
    public static bool IsClaimExpired(DateTime lastHeartbeatUtc, int timeoutSeconds, DateTime nowUtc) =>
        (nowUtc - lastHeartbeatUtc).TotalSeconds > timeoutSeconds;

    /// <summary>
    /// Open (or join) the crawl for <paramref name="crawlKind"/> at schedule
    /// slot <paramref name="slot"/>. Returns the crawl key. The insert is
    /// guarded so concurrent nodes race safely — the loser just joins.
    /// </summary>
    public string OpenOrJoinCrawl(string connectorId, string crawlKind, DateTime slot)
    {
        var crawlKey = $"{connectorId}|{crawlKind}|{slot.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";
        _sql.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.CrawlRuns WHERE CrawlKey = @key)
                INSERT INTO dbo.CrawlRuns (CrawlKey, ConnectorId, Kind, OpenedBy, OpenedAtUtc, Status)
                VALUES (@key, @connector, @kind, @node, SYSUTCDATETIME(), 'open');
            """,
            ("@key", crawlKey), ("@connector", connectorId), ("@kind", crawlKind), ("@node", NodeId));
        Logger.Info($"Crawl '{crawlKey}' open (node {NodeId})");
        return crawlKey;
    }

    /// <summary>
    /// Atomically claim <paramref name="objectType"/> for this node: insert a
    /// fresh claim, or take over one whose heartbeat expired. Terminal claims
    /// ('completed'/'failed') are never reclaimed. Returns true when this node
    /// now owns the claim.
    /// </summary>
    public bool TryClaimObject(string crawlKey, string objectType)
    {
        var taken = _sql.Execute(
            """
            UPDATE dbo.ObjectClaims
               SET NodeId = @node, HeartbeatUtc = SYSUTCDATETIME(), Status = 'claimed'
             WHERE CrawlKey = @key AND ObjectType = @type
               AND Status = 'claimed'
               AND (NodeId = @node OR DATEDIFF(second, HeartbeatUtc, SYSUTCDATETIME()) > @timeout);
            """,
            ("@key", crawlKey), ("@type", objectType), ("@node", NodeId), ("@timeout", ClaimTimeoutSeconds));
        if (taken > 0)
            return true;

        var exists = _sql.Scalar(
            "SELECT COUNT(*) FROM dbo.ObjectClaims WHERE CrawlKey = @key AND ObjectType = @type;",
            ("@key", crawlKey), ("@type", objectType));
        if (Convert.ToInt32(exists, CultureInfo.InvariantCulture) > 0)
            return false;  // live claim held elsewhere, or already terminal

        try
        {
            var inserted = _sql.Execute(
                """
                INSERT INTO dbo.ObjectClaims (CrawlKey, ObjectType, NodeId, HeartbeatUtc, Status)
                VALUES (@key, @type, @node, SYSUTCDATETIME(), 'claimed');
                """,
                ("@key", crawlKey), ("@type", objectType), ("@node", NodeId));
            return inserted > 0;
        }
        catch (Exception exc)
        {
            // Almost always the primary-key race (another node inserted first),
            // which is expected and must stay quiet at default levels — but a
            // real SQL fault here would otherwise be indistinguishable from
            // "claimed elsewhere", so record the detail at DEBUG (captured by
            // connector.log) for HA troubleshooting. Semantics unchanged: the
            // claim is simply not ours.
            Logger.Debug(
                $"Claim insert for '{objectType}' (crawl '{crawlKey}') lost/failed — treating "
                + $"as claimed by another node: {exc.GetType().Name}: {exc.Message}");
            return false;
        }
    }

    /// <summary>Refresh the heartbeat on a claim this node holds.</summary>
    public void Heartbeat(string crawlKey, string objectType)
    {
        _sql.Execute(
            """
            UPDATE dbo.ObjectClaims SET HeartbeatUtc = SYSUTCDATETIME()
             WHERE CrawlKey = @key AND ObjectType = @type AND NodeId = @node AND Status = 'claimed';
            """,
            ("@key", crawlKey), ("@type", objectType), ("@node", NodeId));
    }

    /// <summary>Mark an object type finished successfully (terminal — never reclaimed).</summary>
    public void CompleteObject(string crawlKey, string objectType) =>
        SetTerminalStatus(crawlKey, objectType, "completed");

    /// <summary>
    /// Mark an object type FAILED (terminal — never reclaimed). Called when
    /// the object worker crashed; the crash is dead-lettered by the pipeline
    /// and the crawl still closes (see class remarks).
    /// </summary>
    public void FailObject(string crawlKey, string objectType) =>
        SetTerminalStatus(crawlKey, objectType, "failed");

    private void SetTerminalStatus(string crawlKey, string objectType, string status)
    {
        _sql.Execute(
            """
            UPDATE dbo.ObjectClaims
               SET Status = @status, HeartbeatUtc = SYSUTCDATETIME()
             WHERE CrawlKey = @key AND ObjectType = @type AND NodeId = @node;
            """,
            ("@key", crawlKey), ("@type", objectType), ("@node", NodeId), ("@status", status));
    }

    /// <summary>
    /// Close the crawl when every object type has reached a TERMINAL state
    /// ('completed' or 'failed' — failed claims never block the close). The
    /// crawl row records 'failed' when any claim failed, 'closed' otherwise.
    /// Returns true only for the node that performed the close (that node
    /// records the sync timestamp); ClosedBy makes the answer stable when a
    /// commit-ack-loss retry re-runs the winner's call.
    /// </summary>
    public bool TryCloseCrawl(string crawlKey, IReadOnlyCollection<string> objectTypes)
    {
        var live = _sql.Scalar(
            "SELECT COUNT(*) FROM dbo.ObjectClaims WHERE CrawlKey = @key AND Status = 'claimed';",
            ("@key", crawlKey));
        if (Convert.ToInt32(live, CultureInfo.InvariantCulture) > 0)
            return false;

        var terminal = _sql.Scalar(
            """
            SELECT COUNT(*) FROM dbo.ObjectClaims
             WHERE CrawlKey = @key AND Status IN ('completed', 'failed');
            """,
            ("@key", crawlKey));
        if (Convert.ToInt32(terminal, CultureInfo.InvariantCulture) < objectTypes.Count)
            return false;  // some object types not picked up yet

        var failed = _sql.Scalar(
            "SELECT COUNT(*) FROM dbo.ObjectClaims WHERE CrawlKey = @key AND Status = 'failed';",
            ("@key", crawlKey));
        var finalStatus = Convert.ToInt32(failed, CultureInfo.InvariantCulture) > 0 ? "failed" : "closed";

        var won = _sql.Execute(
            """
            UPDATE dbo.CrawlRuns
               SET Status = @status, ClosedBy = @node, ClosedAtUtc = SYSUTCDATETIME()
             WHERE CrawlKey = @key AND Status = 'open';
            """,
            ("@key", crawlKey), ("@node", NodeId), ("@status", finalStatus));
        if (won > 0)
        {
            Logger.Info($"Crawl '{crawlKey}' {finalStatus} by node {NodeId}"
                        + (finalStatus == "failed" ? " (some object claims failed — see dead-letter)" : string.Empty));
            return true;
        }

        // Commit-ack-loss retry: if THIS node already performed the close, the
        // answer must stay true (exactly one node ever records the sync state).
        var closedByMe = _sql.Scalar(
            """
            SELECT COUNT(*) FROM dbo.CrawlRuns
             WHERE CrawlKey = @key AND Status IN ('closed', 'failed') AND ClosedBy = @node;
            """,
            ("@key", crawlKey), ("@node", NodeId));
        return Convert.ToInt32(closedByMe, CultureInfo.InvariantCulture) > 0;
    }
}
