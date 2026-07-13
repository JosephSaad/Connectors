// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the ingested-item inventory (Graph/ItemInventory.cs): SQLite
// round-trip, upsert semantics, object-type grouping, removal (incl. no-op),
// persistence across reopen, and the DataDir-based Open factory.

using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

// Joins "EnvVars" because Open_UsesDataDir mutates the process-global
// ItemInventory.DataDir; every test that touches it saves and restores it.
[Collection("EnvVars")]
public sealed class ItemInventoryTests : IDisposable
{
    private readonly string _tempDir;

    public ItemInventoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfc_inv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private ItemInventory Open(string name = "InvTests") =>
        new(name, Path.Combine(_tempDir, $"{name}.db"));

    [Fact]
    public void RecordSeen_Read_Remove_RoundTrip()
    {
        using var inventory = Open();

        inventory.RecordSeen(
            new[] { ("Acct_1", "Account"), ("Acct_2", "Account"), ("Case_1", "Case") },
            new DateTime(2026, 7, 12, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(3, inventory.Count());
        Assert.Equal(new[] { "Acct_1", "Acct_2" }, inventory.IdsForObject("Account"));
        Assert.Equal(new[] { "Case_1" }, inventory.IdsForObject("Case"));
        Assert.Empty(inventory.IdsForObject("Ghost"));

        inventory.Remove(new[] { "Acct_1" });
        Assert.Equal(new[] { "Acct_2" }, inventory.IdsForObject("Account"));
        Assert.Equal(2, inventory.Count());
    }

    [Fact]
    public void RecordSeen_IsUpsert_NoDuplicates()
    {
        using var inventory = Open();
        inventory.RecordSeen(new[] { ("Acct_1", "Account") }, DateTime.UtcNow);
        inventory.RecordSeen(new[] { ("Acct_1", "Account") }, DateTime.UtcNow);
        Assert.Equal(1, inventory.Count());
        Assert.Equal(new[] { "Acct_1" }, inventory.IdsForObject("Account"));
    }

    [Fact]
    public void AllByObject_GroupsIds()
    {
        using var inventory = Open();
        inventory.RecordSeen(
            new[] { ("Acct_1", "Account"), ("Case_1", "Case"), ("Acct_2", "Account") }, DateTime.UtcNow);

        var all = inventory.AllByObject();
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "Acct_1", "Acct_2" }, all["Account"]);
        Assert.Equal(new[] { "Case_1" }, all["Case"]);
    }

    [Fact]
    public void Remove_MissingIds_IsNoOp()
    {
        using var inventory = Open();
        inventory.RecordSeen(new[] { ("Acct_1", "Account") }, DateTime.UtcNow);
        inventory.Remove(new[] { "Ghost_1" });
        Assert.Equal(1, inventory.Count());
    }

    [Fact]
    public void Persistence_SurvivesReopen()
    {
        var path = Path.Combine(_tempDir, "persist.db");
        using (var inventory = new ItemInventory("Persist", path))
            inventory.RecordSeen(new[] { ("Acct_9", "Account") }, DateTime.UtcNow);
        using (var reopened = new ItemInventory("Persist", path))
            Assert.Equal(new[] { "Acct_9" }, reopened.IdsForObject("Account"));
    }

    [Fact]
    public void Open_UsesDataDir()
    {
        var saved = ItemInventory.DataDir;
        var connectorId = "InvOpen" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            ItemInventory.DataDir = _tempDir;
            using (var inventory = ItemInventory.Open(connectorId))
            {
                inventory.RecordSeen(new[] { ("Acct_1", "Account") }, DateTime.UtcNow);
                Assert.Equal(new[] { "Acct_1" }, inventory.IdsForObject("Account"));
            }
            Assert.True(File.Exists(Path.Combine(_tempDir, $"{connectorId}_inventory.db")));
        }
        finally
        {
            ItemInventory.DataDir = saved;
        }
    }
}
