// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// TestAclEngine/ShareFetcherPrewarmFallbackTests.cs
// -------------------------------------------------
// Regression tests for the 2026-07 code-review MEDIUM finding:
//
//   ShareFetcher.PrewarmChunkAsync pre-seeds _ownerCache[rid]=null and
//   _shareCache[rid]=[] for EVERY record before the bulk owner/share queries.
//   If a bulk query throws a *transient* (non-INVALID_FIELD) error, those seeded
//   blanks used to stay authoritative — the GetOwnerIdAsync / GetShareEntriesAsync
//   fast-path guards would return owner-less / deny-all WITHOUT taking the
//   per-record slow path, silently indexing a whole batch with wrong ACLs.
//
//   Fix: on a transient batch failure the seeded keys for that batch are dropped
//   from the caches so the slow path re-queries each record.  Records that WERE
//   successfully queried keep their cached blanks (zero-share fast-path preserved).

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

/// <summary>SalesforceClient fake that routes each SOQL string to a handler and records calls.</summary>
file sealed class RoutingFakeSfClient : SalesforceClient
{
    public Func<string, List<JsonObject>> QueryAllHandler = _ => new List<JsonObject>();
    public Func<string, JsonObject> DescribeHandler = _ => new JsonObject { ["fields"] = new JsonArray() };
    public readonly List<string> Queries = new();

    public RoutingFakeSfClient()
        : base("https://test.my.salesforce.com", "60.0", "mock-token")
    {
    }

    public override Task<List<JsonObject>> QueryAllAsync(string soql, bool tooling = false)
    {
        lock (Queries)
            Queries.Add(soql);
        return Task.FromResult(QueryAllHandler(soql));
    }

    public override Task<JsonObject> DescribeSObjectAsync(string sobjectName)
        => Task.FromResult(DescribeHandler(sobjectName));
}

public class ShareFetcherPrewarmFallbackTests
{
    // AccountShare describe: parent reference field + access-level picklist.
    private static JsonObject AccountShareDescribe() => new()
    {
        ["fields"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "AccountId",
                ["type"] = "reference",
                ["referenceTo"] = new JsonArray("Account"),
            },
            new JsonObject { ["name"] = "AccountAccessLevel", ["type"] = "picklist" }),
    };

    [Fact]
    public async Task TransientBulkOwnerFailureFallsBackToPerRecordSlowPath()
    {
        const string recordId = "001AAA";
        var sf = new RoutingFakeSfClient
        {
            DescribeHandler = _ => AccountShareDescribe(),
            QueryAllHandler = soql =>
            {
                // Bulk owner fetch — transient failure (row lock, NOT INVALID_FIELD).
                if (soql.StartsWith("SELECT Id, OwnerId FROM Account WHERE Id IN"))
                    throw new InvalidOperationException("UNABLE_TO_LOCK_ROW: transient contention");
                // Per-record slow-path owner fetch — succeeds.
                if (soql.StartsWith("SELECT OwnerId FROM Account WHERE Id ="))
                    return new List<JsonObject> { new() { ["OwnerId"] = "005OWNER" } };
                // Bulk share fetch — succeeds (empty).
                return new List<JsonObject>();
            },
        };
        var fetcher = new ShareFetcher(sf);

        await fetcher.PrewarmChunkAsync("Account", new List<string> { recordId });

        // Fix: the seeded owner blank was dropped, so the fast path misses and the
        // per-record slow path re-queries and finds the real owner.
        var owner = await fetcher.GetOwnerIdAsync("Account", recordId);

        Assert.Equal("005OWNER", owner);
        Assert.Contains(sf.Queries, q => q.StartsWith("SELECT OwnerId FROM Account WHERE Id ="));
    }

    [Fact]
    public async Task TransientBulkShareFailureFallsBackToPerRecordSlowPath()
    {
        const string recordId = "001BBB";
        var sf = new RoutingFakeSfClient
        {
            DescribeHandler = _ => AccountShareDescribe(),
            QueryAllHandler = soql =>
            {
                // Bulk owner fetch — succeeds.
                if (soql.StartsWith("SELECT Id, OwnerId FROM Account WHERE Id IN"))
                    return new List<JsonObject> { new() { ["Id"] = recordId, ["OwnerId"] = "005OWNER" } };
                // Bulk share fetch — transient failure.
                if (soql.Contains("FROM AccountShare WHERE AccountId IN"))
                    throw new InvalidOperationException("REQUEST_LIMIT_EXCEEDED: transient");
                // Per-record slow-path share fetch — succeeds with one grant.
                if (soql.Contains("FROM AccountShare WHERE AccountId ="))
                {
                    return new List<JsonObject>
                    {
                        new()
                        {
                            ["UserOrGroupId"] = "005SHARED",
                            ["RowCause"] = "Manual",
                            ["AccountAccessLevel"] = "Edit",
                        },
                    };
                }
                return new List<JsonObject>();
            },
        };
        var fetcher = new ShareFetcher(sf);

        await fetcher.PrewarmChunkAsync("Account", new List<string> { recordId });

        // Fix: the seeded empty share list was dropped, so the fast path misses and
        // the per-record slow path re-queries and finds the real grant (not deny-all).
        var entries = await fetcher.GetShareEntriesAsync("Account", recordId);

        Assert.Single(entries);
        Assert.Equal("005SHARED", entries[0].UserOrGroupId);
        Assert.Equal("Edit", entries[0].AccessLevel);
        Assert.Contains(sf.Queries, q => q.Contains("FROM AccountShare WHERE AccountId ="));

        // Owner batch succeeded → still served from the prewarm cache (no slow re-query).
        var owner = await fetcher.GetOwnerIdAsync("Account", recordId);
        Assert.Equal("005OWNER", owner);
        Assert.DoesNotContain(sf.Queries, q => q.StartsWith("SELECT OwnerId FROM Account WHERE Id ="));
    }

    [Fact]
    public async Task SuccessfulPrewarmPreservesZeroShareFastPath()
    {
        // Guards against over-eager cache eviction: when the bulk queries succeed,
        // a record with zero shares must still be served from the cache with NO
        // per-record slow-path SOQL.
        const string recordId = "001CCC";
        var sf = new RoutingFakeSfClient
        {
            DescribeHandler = _ => AccountShareDescribe(),
            QueryAllHandler = soql =>
            {
                if (soql.StartsWith("SELECT Id, OwnerId FROM Account WHERE Id IN"))
                    return new List<JsonObject> { new() { ["Id"] = recordId, ["OwnerId"] = "005OWNER" } };
                // Bulk share fetch succeeds but returns no rows for this record.
                return new List<JsonObject>();
            },
        };
        var fetcher = new ShareFetcher(sf);

        await fetcher.PrewarmChunkAsync("Account", new List<string> { recordId });
        var queriesAfterPrewarm = sf.Queries.Count;

        var owner = await fetcher.GetOwnerIdAsync("Account", recordId);
        var entries = await fetcher.GetShareEntriesAsync("Account", recordId);

        Assert.Equal("005OWNER", owner);
        Assert.Empty(entries);
        // Both served from the prewarm cache: zero additional SOQL.
        Assert.Equal(queriesAfterPrewarm, sf.Queries.Count);
    }
}
