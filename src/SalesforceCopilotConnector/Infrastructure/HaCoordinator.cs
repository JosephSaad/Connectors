// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/HaCoordinator.cs
// -------------------------------
// Active-active crawl coordination for multi-node deployments, per
// docs/SQL_CONTRACT.md (HA coordination stored procedures).
//
// All nodes run the same --continuous command. When a cycle is due each node
// calls usp_OpenOrJoinCrawl — exactly one creates the CrawlSessions row (under
// sp_getapplock inside the proc), the rest join. Nodes then loop
// usp_ClaimNextObject → ingest that object type → usp_CompleteClaim until no
// claims remain. A background heartbeat task per active claim keeps the claim
// alive; if a node dies its heartbeat goes stale and another node reclaims the
// object via usp_ClaimNextObject, resuming from the object's SQL checkpoint.
// Whoever gets usp_CloseCrawlIfComplete() == 1 records the sync state.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SalesforceCopilotConnector.Config;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>Result of <see cref="HaCoordinator.OpenOrJoinCrawlAsync"/>.</summary>
public sealed class HaCrawlHandle
{
    /// <summary>The coordinated crawl's id (CrawlSessions.CrawlId).</summary>
    public Guid CrawlId { get; init; }

    /// <summary>True when this node created the crawl; false when it joined an open one.</summary>
    public bool Created { get; init; }

    /// <summary>The crawl's incremental boundary, when the proc returned it.</summary>
    public string? SinceIso { get; init; }

    /// <summary>True when the result set contained a SinceIso column (even if NULL).</summary>
    public bool HasSinceIso { get; init; }

    /// <summary>The crawl kind (full|incremental), when the proc returned it.</summary>
    public string? CrawlKind { get; init; }
}

/// <summary>
/// Seam used by Graph.Ingest: when installed (HA mode), the per-object worker
/// loop pulls object types from coordinator claims instead of the static
/// schema list, heartbeats while working each object, and completes the claim
/// when the object finishes.
/// </summary>
public interface IObjectWorkSource
{
    /// <summary>Claim the next object type to ingest, or <c>null</c> when none remain.</summary>
    Task<string?> ClaimNextAsync();

    /// <summary>Start the heartbeat for a claimed object; dispose when the object finishes.</summary>
    IDisposable BeginHeartbeat(string objectType);

    /// <summary>Complete the claim — status ``done`` on success, ``failed`` on exception.</summary>
    Task CompleteAsync(string objectType, bool succeeded);
}

public static class HaCoordinator
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    private static bool? _haMode;

    /// <summary>True when HA_MODE=true (cached; resettable for tests).</summary>
    public static bool IsHaMode
    {
        get
        {
            _haMode ??= string.Equals(
                Environment.GetEnvironmentVariable("HA_MODE"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            return _haMode.Value;
        }
    }

    /// <summary>Test seam: re-read HA_MODE on next use.</summary>
    internal static void ResetCache() => _haMode = null;

    /// <summary>Stable identifier of this node in claims/heartbeats (NODE_ID, default machine name).</summary>
    public static string NodeId =>
        Environment.GetEnvironmentVariable("NODE_ID") is { Length: > 0 } nodeId
            ? nodeId
            : Environment.MachineName;

    /// <summary>A claim whose heartbeat is older than this is abandoned and reclaimable.</summary>
    public static int ClaimTimeoutSeconds => ParseEnvInt("HA_CLAIM_TIMEOUT_SECONDS", 300);

    /// <summary>Heartbeat interval while a node works an object claim.</summary>
    public static int HeartbeatSeconds => ParseEnvInt("HA_HEARTBEAT_SECONDS", 60);

    private static int ParseEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }

    private static async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(SqlStateStore.ConnectionString);
        await connection.OpenAsync(ServiceStop.Token);
        return connection;
    }

    private static SqlCommand Proc(SqlConnection connection, string name)
    {
        var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = name;
        return command;
    }

    // ── Crawl session ────────────────────────────────────────────────────────

    /// <summary>
    /// Open the coordinated crawl for this cycle, or join the one another node
    /// already opened (usp_OpenOrJoinCrawl decides atomically under an applock).
    ///
    /// Cycle dedup: when <paramref name="cycleDueUtc"/> is provided and the
    /// connector's last-sync in SQL is already fresher than the due time, the
    /// cycle was completed (and its crawl closed) by another node — return
    /// <c>null</c> so the caller skips this cycle. When a crawl is still open,
    /// the last-sync is necessarily older than the due time, so joiners fall
    /// through to usp_OpenOrJoinCrawl and help finish it.
    /// </summary>
    public static async Task<HaCrawlHandle?> OpenOrJoinCrawlAsync(
        string connectorId,
        string crawlKind,
        string? sinceIso,
        IReadOnlyList<string> objectTypes,
        DateTime? cycleDueUtc = null,
        string? nodeId = null)
    {
        if (cycleDueUtc != null)
        {
            var lastSync = SyncState.ReadLastSync(connectorId);
            if (lastSync != null && lastSync.Value >= cycleDueUtc.Value.ToUniversalTime())
            {
                Logger.Info(
                    $"[HA] Last sync {lastSync.Value:o} is fresher than this node's due time " +
                    $"{cycleDueUtc.Value.ToUniversalTime():o} — skipping the cycle");
                return null;
            }
        }

        var typesJson = new JsonArray(objectTypes.Select(t => (JsonNode)t).ToArray()).ToJsonString();
        await using var connection = await OpenConnectionAsync();
        await using var command = Proc(connection, "dbo.usp_OpenOrJoinCrawl");
        command.Parameters.AddWithValue("@ConnectorId", connectorId);
        command.Parameters.AddWithValue("@CrawlKind", crawlKind);
        command.Parameters.AddWithValue("@SinceIso", (object?)sinceIso ?? DBNull.Value);
        command.Parameters.AddWithValue("@NodeId", nodeId ?? NodeId);
        command.Parameters.AddWithValue("@ObjectTypesJson", typesJson);
        await using var reader = await command.ExecuteReaderAsync(ServiceStop.Token);
        if (!await reader.ReadAsync(ServiceStop.Token))
        {
            throw new InvalidOperationException("usp_OpenOrJoinCrawl returned no rows");
        }
        var crawlId = reader.GetGuid(reader.GetOrdinal("CrawlId"));
        var created = Convert.ToBoolean(reader[reader.GetOrdinal("Created")]);
        string? crawlSinceIso = null;
        var hasSinceIso = TryGetString(reader, "SinceIso", out crawlSinceIso);
        TryGetString(reader, "CrawlKind", out var returnedKind);
        return new HaCrawlHandle
        {
            CrawlId = crawlId,
            Created = created,
            SinceIso = crawlSinceIso,
            HasSinceIso = hasSinceIso,
            CrawlKind = returnedKind,
        };
    }

    private static bool TryGetString(SqlDataReader reader, string column, out string? value)
    {
        value = null;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), column, StringComparison.OrdinalIgnoreCase))
            {
                value = reader.IsDBNull(i) ? null : reader.GetString(i);
                return true;
            }
        }
        return false;
    }

    // ── Object claims ────────────────────────────────────────────────────────

    /// <summary>
    /// Atomically claim one pending object (or reclaim one whose heartbeat is
    /// stale). Returns the object type, or <c>null</c> when no work remains.
    /// </summary>
    public static async Task<string?> ClaimNextObjectAsync(Guid crawlId, string? nodeId = null)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = Proc(connection, "dbo.usp_ClaimNextObject");
        command.Parameters.AddWithValue("@CrawlId", crawlId);
        command.Parameters.AddWithValue("@NodeId", nodeId ?? NodeId);
        command.Parameters.AddWithValue("@ClaimTimeoutSeconds", ClaimTimeoutSeconds);
        var result = await command.ExecuteScalarAsync(ServiceStop.Token);
        return result is string objectType && objectType.Length > 0 ? objectType : null;
    }

    /// <summary>Refresh the claim's HeartbeatUtc.</summary>
    public static async Task HeartbeatClaimAsync(Guid crawlId, string objectType, string? nodeId = null)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = Proc(connection, "dbo.usp_HeartbeatClaim");
        command.Parameters.AddWithValue("@CrawlId", crawlId);
        command.Parameters.AddWithValue("@ObjectType", objectType);
        command.Parameters.AddWithValue("@NodeId", nodeId ?? NodeId);
        await command.ExecuteNonQueryAsync(ServiceStop.Token);
    }

    /// <summary>Complete the claim with status ``done`` or ``failed``.</summary>
    public static async Task CompleteClaimAsync(
        Guid crawlId, string objectType, string status, string? nodeId = null)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = Proc(connection, "dbo.usp_CompleteClaim");
        command.Parameters.AddWithValue("@CrawlId", crawlId);
        command.Parameters.AddWithValue("@ObjectType", objectType);
        command.Parameters.AddWithValue("@NodeId", nodeId ?? NodeId);
        command.Parameters.AddWithValue("@Status", status);
        await command.ExecuteNonQueryAsync(ServiceStop.Token);
    }

    /// <summary>
    /// Close the crawl session when no pending/claimed rows remain. Returns
    /// true only for the caller that performed the close — that node records
    /// the sync timestamp; every other node skips it.
    /// </summary>
    public static async Task<bool> CloseCrawlIfCompleteAsync(Guid crawlId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = Proc(connection, "dbo.usp_CloseCrawlIfComplete");
        command.Parameters.AddWithValue("@CrawlId", crawlId);
        var result = await command.ExecuteScalarAsync(ServiceStop.Token);
        return result is not (null or DBNull) && Convert.ToInt32(result) == 1;
    }

    // ── Heartbeat background task ────────────────────────────────────────────

    /// <summary>
    /// Start a background heartbeat for an active claim. Fires every
    /// HA_HEARTBEAT_SECONDS until disposed; honors <see cref="ServiceStop.Token"/>
    /// so a graceful stop lets the claim expire and be reclaimed elsewhere.
    /// </summary>
    public static IDisposable StartHeartbeat(Guid crawlId, string objectType, string? nodeId = null)
    {
        var node = nodeId ?? NodeId;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ServiceStop.Token);
        var token = cts.Token;
        var loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                try
                {
                    await HeartbeatClaimAsync(crawlId, objectType, node);
                    Logger.Debug($"[HA] Heartbeat sent for {objectType} (crawl {crawlId}, node {node})");
                }
                catch (Exception exc)
                {
                    // A missed heartbeat is survivable until the claim timeout —
                    // log and keep trying.
                    Logger.Warning(
                        $"[HA] Heartbeat failed for {objectType} (crawl {crawlId}): {exc.Message}");
                }
            }
        }, CancellationToken.None);
        return new HeartbeatScope(cts, loop);
    }

    private sealed class HeartbeatScope : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;

        public HeartbeatScope(CancellationTokenSource cts, Task loop)
        {
            _cts = cts;
            _loop = loop;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Heartbeat loop already surfaced its own logging.
            }
            _cts.Dispose();
        }
    }

    // ── Work source (Graph.Ingest seam) ──────────────────────────────────────

    /// <summary>Create the claim-backed work source for an open crawl.</summary>
    public static IObjectWorkSource CreateWorkSource(Guid crawlId) => new HaObjectWorkSource(crawlId);

    private sealed class HaObjectWorkSource : IObjectWorkSource
    {
        private readonly Guid _crawlId;

        public HaObjectWorkSource(Guid crawlId) => _crawlId = crawlId;

        public Task<string?> ClaimNextAsync() => ClaimNextObjectAsync(_crawlId);

        public IDisposable BeginHeartbeat(string objectType) => StartHeartbeat(_crawlId, objectType);

        public Task CompleteAsync(string objectType, bool succeeded) =>
            CompleteClaimAsync(_crawlId, objectType, succeeded ? "done" : "failed");
    }
}
