// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the automatic post-full-crawl deletion sweep
// (Graph/Reconciler.SweepDeletionsAsync) and its mass-deletion safety guard, plus the
// DELETION_SYNC / DELETION_SYNC_MAX_PERCENT EnvFlags defaults.
//
// The load-bearing test is Sweep_DoesNotDeleteItemStillInSource_EvenIfNotIngestedThisRun:
// an item that still exists in Salesforce but was NOT (re-)ingested this run must NEVER be
// swept, because the sweep's source of truth is a FRESH id-only query — never the set of
// ids ingested during the crawl.
//
// Salesforce and Graph are both faked (injected source fetcher + FakeGraphClient +
// temp-dir inventory), so no network and no data/ SQLite are touched.

using System.Net;
using System.Text.Json;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestGraph;

// Joins "EnvVars": the guard-trip test drives ALERT_WEBHOOK_URL + Alerting.HttpClient, the
// sharding test mutates GRAPH_CONNECTION_SHARDS, and the EnvFlags tests mutate DELETION_SYNC*.
// Every test saves/restores what it touches and resets the process-global Metrics counters;
// the collection is serialized so these globals never race.
[Collection("EnvVars")]
public sealed class DeletionSweepTests : IDisposable
{
    /// <summary>Captures the outgoing webhook request and returns a configurable status.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private readonly string _tempDir;
    private readonly string? _savedShards;
    private readonly string? _savedWebhook;
    private readonly string? _savedDeletionSync;
    private readonly string? _savedMaxPercent;
    private readonly HttpClient _savedAlertClient;
    private readonly string? _savedAlertConnector;
    private readonly AppConfig _config = TestFixtures.TestConfig();
    private readonly FakeGraphClient _graph = new();

    // A real object type from the configured object list (always present in ObjectConfigs).
    private static readonly string TypeA = ApiClient.ObjectConfigs[0].ObjectType;

    public DeletionSweepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfc_sweep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _savedShards = Environment.GetEnvironmentVariable(ShardingConfig.EnvVar);
        _savedWebhook = Environment.GetEnvironmentVariable("ALERT_WEBHOOK_URL");
        _savedDeletionSync = Environment.GetEnvironmentVariable("DELETION_SYNC");
        _savedMaxPercent = Environment.GetEnvironmentVariable("DELETION_SYNC_MAX_PERCENT");
        _savedAlertClient = Alerting.HttpClient;
        _savedAlertConnector = Alerting.ConnectorId;
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", null);
        Metrics.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, _savedShards);
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", _savedWebhook);
        Environment.SetEnvironmentVariable("DELETION_SYNC", _savedDeletionSync);
        Environment.SetEnvironmentVariable("DELETION_SYNC_MAX_PERCENT", _savedMaxPercent);
        Alerting.HttpClient = _savedAlertClient;
        Alerting.ConnectorId = _savedAlertConnector;
        Metrics.ResetForTests();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private IItemInventory InventoryFactory(string connectionId) =>
        new ItemInventory(connectionId, Path.Combine(_tempDir, $"inventory_{connectionId}.db"));

    private void Seed(string connectionId, params (string ItemId, string ObjectType)[] items)
    {
        using var inventory = InventoryFactory(connectionId);
        inventory.RecordSeen(items, DateTime.UtcNow);
    }

    // Fresh-source fetcher backed by a per-object-type dictionary (missing key ⇒ no source ids).
    // Records which object types it was asked for when <paramref name="asked"/> is supplied.
    private static Func<SalesforceObjectConfig, CancellationToken, Task<List<string>>> Source(
        Dictionary<string, List<string>> byType, List<string>? asked = null) =>
        (cfg, _) =>
        {
            asked?.Add(cfg.ObjectType);
            return Task.FromResult(
                byType.TryGetValue(cfg.ObjectType, out var ids) ? new List<string>(ids) : new List<string>());
        };

    private Reconciler Make(Dictionary<string, List<string>>? source = null, List<string>? asked = null) =>
        new(_config, _graph, InventoryFactory, Source(source ?? new Dictionary<string, List<string>>(), asked));

    private (string ItemId, string ObjectType)[] SeedRange(int count) =>
        Enumerable.Range(0, count).Select(i => ($"{TypeA}_{i}", TypeA)).ToArray();

    // ── Happy path: stale withdrawn ──────────────────────────────────────────

    [Fact]
    public async Task Sweep_WithdrawsStale_DeletesViaGraph_PrunesInventory_Counts()
    {
        // indexed {keep, gone}; fresh source {keep} ⇒ gone is stale. inventory 2 < 20 floor,
        // so the guard never engages even at 50% stale.
        Seed(_config.Connector.Id, ($"{TypeA}_keep", TypeA), ($"{TypeA}_gone", TypeA));
        var source = new Dictionary<string, List<string>> { [TypeA] = new() { $"{TypeA}_keep" } };

        var result = await Make(source).SweepDeletionsAsync(guardPercent: 25);

        Assert.Equal(1, result.Deleted);
        Assert.Empty(result.Skipped);
        Assert.Equal(1, result.PerObject[TypeA]);
        Assert.Contains(_graph.DeleteCalls, c =>
            c.Url == $"{GraphClient.ExternalConnectionsPath}/{_config.Connector.Id}/items/{TypeA}_gone");
        Assert.DoesNotContain(_graph.DeleteCalls, c => c.Url.EndsWith($"/{TypeA}_keep", StringComparison.Ordinal));

        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Equal(new[] { $"{TypeA}_keep" }, inventory.IdsForObject(TypeA));
        Assert.Equal(1, Metrics.ItemsDeleted);
    }

    // ── DATA-LOSS GUARANTEE (load-bearing) ───────────────────────────────────

    [Fact]
    public async Task Sweep_DoesNotDeleteItemStillInSource_EvenIfNotIngestedThisRun()
    {
        // `survivor` is in the inventory AND in the FRESH source query, but was NOT ingested this
        // run (a transient ingest failure would look exactly like this). Because the sweep's source
        // set is the fresh query — never "the ids ingested this run" — the survivor must NOT be
        // swept. `reallyGone` is in the inventory but absent from the source ⇒ the only deletion.
        Seed(_config.Connector.Id, ($"{TypeA}_survivor", TypeA), ($"{TypeA}_reallyGone", TypeA));
        var source = new Dictionary<string, List<string>>
        {
            [TypeA] = new() { $"{TypeA}_survivor" },  // still present in the live source
        };

        // Guard disabled (0) so this asserts the invariant itself, not the guard.
        var result = await Make(source).SweepDeletionsAsync(guardPercent: 0);

        // The survivor is present in the live source ⇒ NEVER deleted, despite not being re-ingested.
        Assert.DoesNotContain(_graph.DeleteCalls, c => c.Url.EndsWith($"/{TypeA}_survivor", StringComparison.Ordinal));
        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Contains($"{TypeA}_survivor", inventory.IdsForObject(TypeA));

        // Only the genuinely-absent id was withdrawn.
        Assert.Equal(1, result.Deleted);
        Assert.Contains(_graph.DeleteCalls, c => c.Url.EndsWith($"/{TypeA}_reallyGone", StringComparison.Ordinal));
        Assert.DoesNotContain($"{TypeA}_reallyGone", inventory.IdsForObject(TypeA));
    }

    // ── Mass-deletion guard ──────────────────────────────────────────────────

    [Fact]
    public async Task Guard_Trips_WhenStalePercentExceedsThreshold_SkipsAndAlerts()
    {
        // 24 indexed (>= 20 floor); source keeps 4 ⇒ 20 stale ≈ 83% > 25% ⇒ guard trips.
        Seed(_config.Connector.Id, SeedRange(24));
        var source = new Dictionary<string, List<string>>
        {
            [TypeA] = Enumerable.Range(0, 4).Select(i => $"{TypeA}_{i}").ToList(),
        };

        // Capture the deletion_sweep_skipped alert through the Alerting seam.
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "https://example.test/hook");
        var handler = new CapturingHandler();
        Alerting.HttpClient = new HttpClient(handler);

        var result = await Make(source).SweepDeletionsAsync(guardPercent: 25);

        Assert.Equal(0, result.Deleted);
        Assert.Contains(TypeA, result.Skipped);
        Assert.Empty(_graph.DeleteCalls);               // nothing deleted
        Assert.Equal(0, Metrics.ItemsDeleted);

        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Equal(24, inventory.IdsForObject(TypeA).Count);  // inventory untouched

        Assert.Equal(1, handler.Calls);                 // alert fired
        Assert.Contains("deletion_sweep_skipped", handler.LastBody);
        Assert.Contains(TypeA, handler.LastBody);
    }

    [Fact]
    public async Task Guard_BelowInventoryFloor_ProceedsEvenAtHighStalePercent()
    {
        // 19 indexed (< 20 floor); source keeps 1 ⇒ 18 stale ≈ 95%, but the floor is not met ⇒ deletes.
        Seed(_config.Connector.Id, SeedRange(19));
        var source = new Dictionary<string, List<string>> { [TypeA] = new() { $"{TypeA}_0" } };

        var result = await Make(source).SweepDeletionsAsync(guardPercent: 25);

        Assert.Empty(result.Skipped);
        Assert.Equal(18, result.Deleted);
        Assert.Equal(18, _graph.DeleteCalls.Count);
        Assert.Equal(18, Metrics.ItemsDeleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Guard_Disabled_ProceedsAtHighStalePercent(int guardPercent)
    {
        // 30 indexed (>= 20 floor); source keeps 1 ⇒ 29 stale ≈ 96% — but the guard is disabled.
        Seed(_config.Connector.Id, SeedRange(30));
        var source = new Dictionary<string, List<string>> { [TypeA] = new() { $"{TypeA}_0" } };

        var result = await Make(source).SweepDeletionsAsync(guardPercent);

        Assert.Empty(result.Skipped);
        Assert.Equal(29, result.Deleted);
        Assert.Equal(29, Metrics.ItemsDeleted);
    }

    // ── Delete outcomes ──────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_404OnDelete_CountsAsRemovedFromInventory()
    {
        Seed(_config.Connector.Id, ($"{TypeA}_ghost", TypeA));
        _graph.OnDelete = _ => throw new GraphApiError(404, "Not Found");
        var source = new Dictionary<string, List<string>> { [TypeA] = new() };  // ghost is stale

        var result = await Make(source).SweepDeletionsAsync(guardPercent: 25);

        Assert.Equal(1, result.Deleted);
        Assert.Empty(result.Failures);
        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Empty(inventory.IdsForObject(TypeA));
        Assert.Equal(1, Metrics.ItemsDeleted);
    }

    [Fact]
    public async Task Sweep_HardDeleteFailure_KeptInInventory_AndReported()
    {
        Seed(_config.Connector.Id, ($"{TypeA}_boom", TypeA));
        _graph.OnDelete = _ => throw new GraphApiError(500, "Boom");
        var source = new Dictionary<string, List<string>> { [TypeA] = new() };

        var result = await Make(source).SweepDeletionsAsync(guardPercent: 25);

        Assert.Equal(0, result.Deleted);
        Assert.Single(result.Failures);
        Assert.Contains("500", result.Failures[0]);
        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Contains($"{TypeA}_boom", inventory.IdsForObject(TypeA));
        Assert.Equal(0, Metrics.ItemsDeleted);
    }

    [Fact]
    public async Task Sweep_NoStale_DeletesNothing()
    {
        Seed(_config.Connector.Id, ($"{TypeA}_1", TypeA));
        var source = new Dictionary<string, List<string>> { [TypeA] = new() { $"{TypeA}_1" } };

        var result = await Make(source).SweepDeletionsAsync(guardPercent: 25);

        Assert.Equal(0, result.Deleted);
        Assert.Empty(result.Skipped);
        Assert.Empty(_graph.DeleteCalls);
    }

    // ── Sharding: scoped to the shard, routed to the owning connection ───────

    [Fact]
    public async Task Sweep_ShardScoped_OnlySweepsOwnObjects_AgainstOwningConnection()
    {
        // TypeA → shardA; every other configured object → shardB (a valid full map).
        var rest = _config.ObjectNames
            .Where(n => !string.Equals(n, TypeA, StringComparison.Ordinal))
            .ToArray();
        var map = new Dictionary<string, string[]> { ["shardA"] = new[] { TypeA }, ["shardB"] = rest };
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, JsonSerializer.Serialize(map));

        Seed("shardA", ($"{TypeA}_stale", TypeA));   // stale in shard A's inventory
        var typeB = rest[0];
        Seed("shardB", ($"{typeB}_stale", typeB));   // shard B's stale must be left untouched

        // Per-shard config: Connector.Id = shardA, ShardObjectTypes = [TypeA] — exactly what the
        // per-shard crawl passes to the sweep.
        var shardConfig = ShardingConfig.ForShard(_config, new Shard("shardA", new[] { TypeA }));
        var asked = new List<string>();
        var reconciler = new Reconciler(
            shardConfig, _graph, InventoryFactory,
            Source(new Dictionary<string, List<string>>(), asked));  // empty source ⇒ seeded items are stale

        var result = await reconciler.SweepDeletionsAsync(guardPercent: 25);

        Assert.Equal(new[] { TypeA }, asked);        // only this shard's object was queried
        Assert.Equal(1, result.Deleted);
        Assert.Contains(_graph.DeleteCalls, c =>
            c.Url == $"{GraphClient.ExternalConnectionsPath}/shardA/items/{TypeA}_stale");

        using var invB = InventoryFactory("shardB");
        Assert.Contains($"{typeB}_stale", invB.IdsForObject(typeB));  // shard B untouched
    }

    // ── EnvFlags defaults (opt-OUT sweep, guard percentage) ──────────────────

    [Fact]
    public void DeletionSync_DefaultsOn_WhenUnset()
    {
        Environment.SetEnvironmentVariable("DELETION_SYNC", null);
        Assert.True(SalesforceFlags.DeletionSync);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("FALSE")]
    [InlineData(" No ")]
    public void DeletionSync_Disabled_ByFalseyValue(string value)
    {
        Environment.SetEnvironmentVariable("DELETION_SYNC", value);
        Assert.False(SalesforceFlags.DeletionSync);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("something")]
    public void DeletionSync_On_ForNonFalseyValue(string value)
    {
        Environment.SetEnvironmentVariable("DELETION_SYNC", value);
        Assert.True(SalesforceFlags.DeletionSync);
    }

    [Fact]
    public void DeletionSyncMaxPercent_Defaults25_WhenUnsetOrUnparseable()
    {
        Environment.SetEnvironmentVariable("DELETION_SYNC_MAX_PERCENT", null);
        Assert.Equal(25, SalesforceFlags.DeletionSyncMaxPercent);
        Environment.SetEnvironmentVariable("DELETION_SYNC_MAX_PERCENT", "not-a-number");
        Assert.Equal(25, SalesforceFlags.DeletionSyncMaxPercent);
    }

    [Fact]
    public void DeletionSyncMaxPercent_ParsesInteger()
    {
        Environment.SetEnvironmentVariable("DELETION_SYNC_MAX_PERCENT", "40");
        Assert.Equal(40, SalesforceFlags.DeletionSyncMaxPercent);
    }
}
