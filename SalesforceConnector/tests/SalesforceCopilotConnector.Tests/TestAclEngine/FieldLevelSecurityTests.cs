// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// FieldLevelSecurityTests.cs  (WP-SF-2)
// -------------------------------------
// Salesforce FIELD-LEVEL SECURITY enforcement.
//
// The connector reproduces Salesforce's RECORD-level sharing faithfully, but
// until WP-SF-2 it evaluated no field-level security at all: a user permitted
// to see a record saw EVERY indexed field on it, including fields Salesforce
// itself would hide.
//
// The load-bearing correctness property these tests pin down is the DUAL LOOP.
// SalesforceObjectHandler.BuildItemPropertiesAndContent walks the record twice:
//
//   * loop 1 writes Graph PROPERTIES (via AddSchemaPropertyForField), and
//   * loop 2 appends every field that did NOT become a property into the
//     searchable CONTENT body as "key: value".
//
// A restriction applied to only one of those loops LEAKS — the value vanishes
// from the property but survives verbatim in the body text Copilot grounds on
// (which is exactly what the pre-WP-SF-2 `flsFields` precedent did: it nulled
// the property and left the content body untouched).
//
// Every drop assertion below therefore checks the WHOLE item — properties AND
// content — for the secret value.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Item;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

/// <summary>Shared fixtures for the FLS tests.</summary>
public static class FlsFixtures
{
    public const string InstanceUrl = "https://test.my.salesforce.com";

    /// <summary>The two secrets that must never reach the index.</summary>
    public const string CompensationSecret = "250000-COMP-SECRET";
    public const string MarginSecret = "42.5-MARGIN-SECRET";

    /// <summary>
    /// An object config with two sensitive fields deliberately routed down the
    /// two DIFFERENT assembly paths:
    ///
    ///   * Compensation__c → mapped to the graph property "Compensation", which
    ///     IS in the Graph schema  ⇒ travels the PROPERTY loop;
    ///   * Margin__c       → mapped to the graph property "Margin", which is
    ///     NOT in the Graph schema ⇒ falls through to the CONTENT loop.
    ///
    /// One record therefore exercises both loops at once.
    /// </summary>
    public static JsonObject ObjectConfig(string[]? manualFlsFields = null)
    {
        var config = new JsonObject
        {
            ["objectName"] = "Account",
            ["selectedFields"] = new JsonObject
            {
                ["Name"] = "Title",
                ["Compensation__c"] = "Compensation",
                ["Margin__c"] = "Margin",
            },
        };
        if (manualFlsFields is not null)
        {
            var arr = new JsonArray();
            foreach (var f in manualFlsFields)
                arr.Add(f);
            config["flsFields"] = arr;
        }
        return config;
    }

    /// <summary>Graph schema properties: "Compensation" is present, "Margin" deliberately is not.</summary>
    public static HashSet<string> GraphSchemaProperties() =>
        new() { "ObjectName", "url", "Title", "Compensation" };

    public static JsonObject Record() => new()
    {
        ["attributes"] = new JsonObject { ["type"] = "Account" },
        ["Id"] = "001FLS",
        ["Name"] = "Acme Corp",
        ["Compensation__c"] = CompensationSecret,
        ["Margin__c"] = MarginSecret,
    };

    public static JsonObject QueryResult() => new() { ["records"] = new JsonArray { Record() } };

    /// <summary>Convert one record through a handler and return the single item.</summary>
    public static JsonObject ConvertOne(SalesforceObjectHandler handler)
    {
        var items = handler.ConstructIngestionItems(
            QueryResult(), InstanceUrl, GraphSchemaProperties());
        return Assert.Single(items);
    }

    public static SalesforceObjectHandler Handler(string[]? manualFlsFields = null)
    {
        var handler = new SalesforceObjectHandler(ObjectConfig(manualFlsFields))
        {
            GraphSchemaProperties = GraphSchemaProperties(),
        };
        return handler;
    }

    public static string PropertiesJson(JsonObject item) =>
        (item["properties"] as JsonObject ?? new JsonObject()).ToJsonString();

    public static string ContentBody(JsonObject item) =>
        (item["content"] as JsonObject)?["parsedData"]?.GetValue<string>() ?? "";

    /// <summary>
    /// Assert a secret value appears in NEITHER the Graph properties NOR the
    /// searchable content body. This is the dual-loop guarantee.
    /// </summary>
    public static void AssertDroppedFromBothLoops(JsonObject item, string secret)
    {
        Assert.DoesNotContain(secret, PropertiesJson(item));
        Assert.DoesNotContain(secret, ContentBody(item));
        // Belt and braces: nothing anywhere in the serialised item.
        Assert.DoesNotContain(secret, item.ToJsonString());
    }
}

/// <summary>
/// The dual-loop drop: a restricted field must appear in NEITHER the Graph
/// properties NOR the searchable content body.
/// </summary>
public class FlsDualLoopTests
{
    [Fact]
    public void RestrictedFieldAppearsInNeitherPropertiesNorContent()
    {
        var handler = FlsFixtures.Handler();
        handler.ApplyFlsDrops(new[] { "Compensation__c", "Margin__c" });

        var item = FlsFixtures.ConvertOne(handler);

        // Compensation__c travels the PROPERTY loop; Margin__c the CONTENT loop.
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.CompensationSecret);
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.MarginSecret);
    }

    [Fact]
    public void ContentLoopLeakIsClosedForTheContentRoutedField()
    {
        // Margin__c maps to a graph property that is NOT in the schema, so before
        // WP-SF-2 it was appended verbatim to the body as "Margin__c: <secret>".
        var handler = FlsFixtures.Handler();
        handler.ApplyFlsDrops(new[] { "Margin__c" });

        var item = FlsFixtures.ConvertOne(handler);

        Assert.DoesNotContain("Margin__c", FlsFixtures.ContentBody(item));
        Assert.DoesNotContain(FlsFixtures.MarginSecret, FlsFixtures.ContentBody(item));
    }

    [Fact]
    public void PropertyLoopDropRemovesTheValueFromProperties()
    {
        var handler = FlsFixtures.Handler();
        handler.ApplyFlsDrops(new[] { "Compensation__c" });

        var item = FlsFixtures.ConvertOne(handler);
        var props = (JsonObject)item["properties"]!;

        Assert.True(props["Compensation"] is null, "restricted field must not carry a value");
        Assert.DoesNotContain(FlsFixtures.CompensationSecret, FlsFixtures.PropertiesJson(item));
    }

    [Fact]
    public void UnrestrictedFieldsAreUntouched()
    {
        var handler = FlsFixtures.Handler();
        handler.ApplyFlsDrops(new[] { "Compensation__c", "Margin__c" });

        var item = FlsFixtures.ConvertOne(handler);

        Assert.Equal("Acme Corp", ((JsonObject)item["properties"]!)["Title"]!.GetValue<string>());
    }

    [Fact]
    public void DropCanBeExpressedAsTheGraphPropertyNameToo()
    {
        // The pre-existing `flsFields` config precedent wrote `props[flsField] = null`,
        // i.e. it keyed on the GRAPH PROPERTY name. Both spellings must gate both loops.
        var handler = FlsFixtures.Handler();
        handler.ApplyFlsDrops(new[] { "Compensation", "Margin" });

        var item = FlsFixtures.ConvertOne(handler);

        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.CompensationSecret);
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.MarginSecret);
    }

    [Fact]
    public void NestedRelationshipSubFieldsAreGatedInTheContentLoop()
    {
        // Nested objects are flattened into the body as "Parent.Child: value" by
        // the content loop; a restricted sub-field must be gated there too.
        var config = new JsonObject
        {
            ["objectName"] = "Account",
            ["selectedFields"] = new JsonObject { ["Name"] = "Title" },
        };
        var handler = new SalesforceObjectHandler(config)
        {
            GraphSchemaProperties = new HashSet<string> { "ObjectName", "url", "Title" },
        };
        handler.ApplyFlsDrops(new[] { "Owner.Compensation__c" });

        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["Id"] = "001NEST",
            ["Name"] = "Acme Corp",
            ["Owner"] = new JsonObject
            {
                ["Name"] = "Owner Name",
                ["Compensation__c"] = FlsFixtures.CompensationSecret,
            },
        };
        var items = handler.ConstructIngestionItems(
            new JsonObject { ["records"] = new JsonArray { record } },
            FlsFixtures.InstanceUrl,
            new HashSet<string> { "ObjectName", "url", "Title" });
        var item = Assert.Single(items);

        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.CompensationSecret);
    }
}

/// <summary>
/// The manual per-object `flsFields` list in config/schema.json remains an
/// operator override, and UNIONS with the fetched permissions — a fetched set
/// must never silently shrink what an operator explicitly listed.
/// </summary>
public class FlsManualOverrideTests
{
    [Fact]
    public void ManualFlsFieldsListStillApplies()
    {
        var handler = FlsFixtures.Handler(new[] { "Compensation__c", "Margin__c" });
        // No fetched drops at all.
        var item = FlsFixtures.ConvertOne(handler);

        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.CompensationSecret);
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.MarginSecret);
    }

    [Fact]
    public void ManualFlsFieldsNowGateTheContentLoopToo()
    {
        // Pre-WP-SF-2 the manual list only nulled the PROPERTY. Margin__c is
        // content-routed, so the old code leaked it into the body.
        var handler = FlsFixtures.Handler(new[] { "Margin__c" });
        var item = FlsFixtures.ConvertOne(handler);

        Assert.DoesNotContain(FlsFixtures.MarginSecret, FlsFixtures.ContentBody(item));
    }

    [Fact]
    public void FetchedDropsUnionWithTheManualList()
    {
        var handler = FlsFixtures.Handler(new[] { "Compensation__c" });
        handler.ApplyFlsDrops(new[] { "Margin__c" });

        var item = FlsFixtures.ConvertOne(handler);

        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.CompensationSecret);
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.MarginSecret);
        Assert.Equal(
            new[] { "Compensation__c", "Margin__c" },
            handler.EffectiveFlsFields.OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void FetchedDropsNeverShrinkTheManualList()
    {
        var handler = FlsFixtures.Handler(new[] { "Compensation__c" });
        // A fetched set that says "everything is readable" must not clear the
        // operator's explicit entry.
        handler.ApplyFlsDrops(Array.Empty<string>());

        Assert.Contains("Compensation__c", handler.EffectiveFlsFields);
        FlsFixtures.AssertDroppedFromBothLoops(FlsFixtures.ConvertOne(handler), FlsFixtures.CompensationSecret);
    }

    [Fact]
    public void ApplyFlsDropsIsIdempotentAcrossRepeatedCrawls()
    {
        var handler = FlsFixtures.Handler(new[] { "Compensation__c" });
        handler.ApplyFlsDrops(new[] { "Margin__c" });
        handler.ApplyFlsDrops(new[] { "Margin__c" });

        Assert.Equal(2, handler.EffectiveFlsFields.Count);
    }
}

/// <summary>
/// THE DECISION RULE.
///
/// An indexed item carries ONE property set shared by every principal on its
/// ACL, so field visibility is per-ITEM, not per-user. A field readable by SOME
/// but not ALL principals on the item's ACL must therefore be DROPPED
/// (least-privilege union) — that is FLS_MODE=strict, the default.
///
/// FLS_MODE=permissive is the documented weaker escape hatch: it drops only
/// fields NO principal on the ACL can read, and can therefore expose a field to
/// a principal Salesforce would deny.
/// </summary>
public class FlsStrictVsPermissiveTests
{
    private static FlsObjectPermissions Perms()
    {
        // Two principals on the ACL: profile "Sales Rep" (psSalesRep) and
        // profile "Sales Manager" (psManager).
        //  * Compensation__c — readable by the manager ONLY (the split field).
        //  * Margin__c       — readable by NOBODY.
        //  * Title           — readable by BOTH.
        //  * Name            — never appears in FieldPermissions ⇒ ungoverned.
        return new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string> { "psSalesRep", "psManager" },
            governedFields: new HashSet<string> { "Compensation__c", "Margin__c", "Title" },
            readersByField: new Dictionary<string, HashSet<string>>
            {
                ["Compensation__c"] = new() { "psManager" },
                ["Margin__c"] = new(),
                ["Title"] = new() { "psSalesRep", "psManager" },
            });
    }

    private static string[] Candidates() => new[] { "Name", "Title", "Compensation__c", "Margin__c" };

    [Fact]
    public void StrictDropsAFieldReadableByOnePrincipalButNotTheOther()
    {
        var drops = FlsPolicy.ComputeDrops(Perms(), Candidates(), FlsMode.Strict);
        var dropped = drops.Select(d => d.Field).ToHashSet();

        Assert.Contains("Compensation__c", dropped);   // readable by 1 of 2 → dropped
        Assert.Contains("Margin__c", dropped);         // readable by 0 of 2 → dropped
        Assert.DoesNotContain("Title", dropped);       // readable by 2 of 2 → kept
        Assert.DoesNotContain("Name", dropped);        // ungoverned by Salesforce → kept
    }

    [Fact]
    public void PermissiveKeepsAFieldReadableByAtLeastOnePrincipal()
    {
        var drops = FlsPolicy.ComputeDrops(Perms(), Candidates(), FlsMode.Permissive);
        var dropped = drops.Select(d => d.Field).ToHashSet();

        Assert.DoesNotContain("Compensation__c", dropped);  // THE weakening: 1 of 2 → kept
        Assert.Contains("Margin__c", dropped);              // 0 of 2 → still dropped
        Assert.DoesNotContain("Title", dropped);
        Assert.DoesNotContain("Name", dropped);
    }

    [Fact]
    public void StrictIsAlwaysAtLeastAsRestrictiveAsPermissive()
    {
        var strict = FlsPolicy.ComputeDrops(Perms(), Candidates(), FlsMode.Strict)
            .Select(d => d.Field).ToHashSet();
        var permissive = FlsPolicy.ComputeDrops(Perms(), Candidates(), FlsMode.Permissive)
            .Select(d => d.Field).ToHashSet();

        Assert.ProperSubset(strict, permissive);
    }

    [Fact]
    public void DropReasonsAreRecordedForTheAuditManifest()
    {
        var drops = FlsPolicy.ComputeDrops(Perms(), Candidates(), FlsMode.Strict);
        var comp = Assert.Single(drops, d => d.Field == "Compensation__c");

        Assert.Contains("strict", comp.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(comp.Reason));
    }

    [Fact]
    public void AnExplicitPrincipalScopeNarrowsTheStrictUnion()
    {
        // Scoped to the manager alone, Compensation__c is readable by ALL (=1)
        // principals in scope, so strict keeps it.
        var drops = FlsPolicy.ComputeDrops(
            Perms(), Candidates(), FlsMode.Strict,
            principalScope: new HashSet<string> { "psManager" });

        Assert.DoesNotContain("Compensation__c", drops.Select(d => d.Field));
        Assert.Contains("Margin__c", drops.Select(d => d.Field));
    }

    [Fact]
    public void EmptyPrincipalScopeDropsEveryGovernedFieldUnderStrict()
    {
        // No principal known to read the object ⇒ we cannot prove anyone may read
        // any governed field. Fail closed.
        var perms = new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string>(),
            governedFields: new HashSet<string> { "Compensation__c" },
            readersByField: new Dictionary<string, HashSet<string>>
            {
                ["Compensation__c"] = new(),
            });

        var drops = FlsPolicy.ComputeDrops(perms, Candidates(), FlsMode.Strict);
        Assert.Contains("Compensation__c", drops.Select(d => d.Field));
    }

    [Theory]
    [InlineData("strict", FlsMode.Strict)]
    [InlineData("STRICT", FlsMode.Strict)]
    [InlineData("permissive", FlsMode.Permissive)]
    [InlineData("", FlsMode.Strict)]
    [InlineData("nonsense", FlsMode.Strict)]
    public void ModeParsingDefaultsToStrict(string raw, FlsMode expected)
    {
        Assert.Equal(expected, FlsSettings.ParseMode(raw));
    }
}

/// <summary>
/// The record-level ACL proof (two differently permissioned principals on the
/// same record) EXTENDED to field visibility: the record is shared with both,
/// and the field only one of them may read is dropped from the item entirely.
/// </summary>
public class FlsAclProofTests
{
    [Fact]
    public async Task TwoDifferentlyPermissionedUsersShareTheRecordButNotTheField()
    {
        // ── record level (the pre-existing proof) ────────────────────────────
        var salesRep = GroupAclBuilderTests.MakeSfUser(
            userId: "005REP",
            name: "Sales Rep",
            federationId: "11111111-1111-1111-1111-111111111111",
            permissionSets: new List<Dictionary<string, object?>> { new() { ["Id"] = "psSalesRep" } });
        var manager = GroupAclBuilderTests.MakeSfUser(
            userId: "005MGR",
            name: "Sales Manager",
            federationId: "22222222-2222-2222-2222-222222222222",
            permissionSets: new List<Dictionary<string, object?>> { new() { ["Id"] = "psManager" } });

        var builder = GroupAclBuilderTests.MakeBuilder(
            owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
            users: new List<SfUser> { salesRep, manager });
        var records = new List<JsonObject>
        {
            new()
            {
                ["Id"] = "001FLS",
                ["Shares"] = GroupAclBuilderTests.Shares(
                    GroupAclBuilderTests.Share("005REP", "User"),
                    GroupAclBuilderTests.Share("005MGR", "User")),
            },
        };
        var aclMap = await builder.BuildAclMapAsync("Account", records, GroupAclBuilderTests.EmptyAclMaps());
        var userAceValues = aclMap["001FLS"].Where(a => a["type"] == "user").Select(a => a["value"]).ToList();

        Assert.Contains("11111111-1111-1111-1111-111111111111", userAceValues);
        Assert.Contains("22222222-2222-2222-2222-222222222222", userAceValues);

        // ── field level (the WP-SF-2 extension) ──────────────────────────────
        // Both principals are on this item's ACL. Compensation__c is readable by
        // the manager only. The item carries ONE property set shared by both, so
        // strict mode must drop the field rather than expose it to the rep.
        var perms = new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string> { "psSalesRep", "psManager" },
            governedFields: new HashSet<string> { "Compensation__c", "Margin__c" },
            readersByField: new Dictionary<string, HashSet<string>>
            {
                ["Compensation__c"] = new() { "psManager" },
                ["Margin__c"] = new() { "psSalesRep", "psManager" },
            });

        var drops = FlsPolicy.ComputeDrops(
            perms, new[] { "Name", "Compensation__c", "Margin__c" }, FlsMode.Strict);

        var handler = FlsFixtures.Handler();
        handler.ApplyFlsDrops(drops.Select(d => d.Field));
        var item = FlsFixtures.ConvertOne(handler);

        // The field the rep may NOT read is gone from properties AND content.
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.CompensationSecret);
        // The field BOTH may read survives (it is content-routed here).
        Assert.Contains(FlsFixtures.MarginSecret, item.ToJsonString());
    }
}

/// <summary>Field-permission fetch: SOQL shape, describe fallback, and caching.</summary>
public class FlsFetcherTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("fls_cache_").FullName;
    private readonly IdentityStore _store;

    public FlsFetcherTests()
    {
        _store = new IdentityStore(Path.Combine(_tmp, "fls_test.db"), "test-conn");
    }

    public void Dispose()
    {
        _store.Close();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Scriptable Salesforce client — counts calls so cache reuse is observable.</summary>
    internal sealed class ScriptedSfClient : SalesforceClient
    {
        public readonly List<string> Soql = new();
        public readonly List<string> Describes = new();
        public Func<string, List<JsonObject>>? OnQueryAll;
        public Func<string, JsonObject>? OnDescribe;

        public ScriptedSfClient()
            : base(FlsFixtures.InstanceUrl, "60.0", "mock-token")
        {
        }

        public override Task<List<JsonObject>> QueryAllAsync(string soql, bool tooling = false)
        {
            Soql.Add(soql);
            if (OnQueryAll is null)
                throw new InvalidOperationException("QueryAllAsync not configured");
            return Task.FromResult(OnQueryAll(soql));
        }

        public override Task<JsonObject> DescribeSObjectAsync(string sobjectName)
        {
            Describes.Add(sobjectName);
            if (OnDescribe is null)
                throw new InvalidOperationException("DescribeSObjectAsync not configured");
            return Task.FromResult(OnDescribe(sobjectName));
        }
    }

    private static JsonObject FieldPermissionRow(string field, string parentId, bool read) => new()
    {
        ["Field"] = field,
        ["SobjectType"] = "Account",
        ["PermissionsRead"] = read,
        ["ParentId"] = parentId,
    };

    private static JsonObject AssignmentRow(string permissionSetId) => new()
    {
        ["PermissionSetId"] = permissionSetId,
    };

    private static ScriptedSfClient HappyClient()
    {
        var client = new ScriptedSfClient();
        client.OnQueryAll = soql =>
        {
            if (soql.Contains("FROM FieldPermissions", StringComparison.Ordinal))
            {
                return new List<JsonObject>
                {
                    FieldPermissionRow("Account.Compensation__c", "psManager", true),
                    FieldPermissionRow("Account.Compensation__c", "psSalesRep", false),
                    FieldPermissionRow("Account.Margin__c", "psManager", false),
                    FieldPermissionRow("Account.Margin__c", "psSalesRep", false),
                };
            }
            if (soql.Contains("FROM PermissionSetAssignment", StringComparison.Ordinal))
            {
                return new List<JsonObject> { AssignmentRow("psManager"), AssignmentRow("psSalesRep") };
            }
            throw new InvalidOperationException("unexpected SOQL: " + soql);
        };
        return client;
    }

    [Fact]
    public async Task FetchReadsFieldPermissionsAndScopesPrincipalsToObjectReaders()
    {
        var client = HappyClient();
        var fetcher = new FlsFetcher(client, _store, FlsFixtures.InstanceUrl);

        var snapshot = await fetcher.FetchAsync(new[] { "Account" });
        var perms = snapshot.Get("Account");

        Assert.NotNull(perms);
        Assert.Equal(new HashSet<string> { "psManager", "psSalesRep" }, perms!.PrincipalsInScope);
        Assert.Contains("Compensation__c", perms.GovernedFields);
        Assert.Contains("Margin__c", perms.GovernedFields);
        Assert.Equal(new HashSet<string> { "psManager" }, perms.ReadersByField["Compensation__c"]);
        Assert.Empty(perms.ReadersByField["Margin__c"]);
    }

    [Fact]
    public async Task FetchUsesTheExistingSalesforceClientNotANewHttpPath()
    {
        var client = HappyClient();
        var fetcher = new FlsFetcher(client, _store, FlsFixtures.InstanceUrl);
        await fetcher.FetchAsync(new[] { "Account" });

        Assert.Contains(client.Soql, s => s.Contains("FROM FieldPermissions", StringComparison.Ordinal));
        Assert.Contains(client.Soql, s => s.Contains("SobjectType", StringComparison.Ordinal));
        Assert.Contains(client.Soql, s => s.Contains("PermissionsRead", StringComparison.Ordinal));
        Assert.Contains(client.Soql, s => s.Contains("ParentId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlsCacheIsPopulatedOnFirstFetch()
    {
        var client = HappyClient();
        var fetcher = new FlsFetcher(client, _store, FlsFixtures.InstanceUrl);

        Assert.Null(_store.GetCachedFls(FlsFixtures.InstanceUrl, "Account"));
        await fetcher.FetchAsync(new[] { "Account" });

        var cached = _store.GetCachedFls(FlsFixtures.InstanceUrl, "Account");
        Assert.NotNull(cached);
        Assert.Contains("Compensation__c", cached!);
    }

    [Fact]
    public async Task FlsCacheIsReusedOnTheSecondFetch()
    {
        var client = HappyClient();
        var fetcher = new FlsFetcher(client, _store, FlsFixtures.InstanceUrl);
        await fetcher.FetchAsync(new[] { "Account" });
        var callsAfterFirst = client.Soql.Count;

        var second = await new FlsFetcher(client, _store, FlsFixtures.InstanceUrl)
            .FetchAsync(new[] { "Account" });

        Assert.Equal(callsAfterFirst, client.Soql.Count);   // no new SOQL
        Assert.Equal(
            new HashSet<string> { "psManager" },
            second.Get("Account")!.ReadersByField["Compensation__c"]);
    }

    [Fact]
    public async Task FlsCacheIsInvalidatedByClearFlsCache()
    {
        var client = HappyClient();
        await new FlsFetcher(client, _store, FlsFixtures.InstanceUrl).FetchAsync(new[] { "Account" });

        var deleted = _store.ClearFlsCache(FlsFixtures.InstanceUrl, "Account");
        Assert.Equal(1, deleted);
        Assert.Null(_store.GetCachedFls(FlsFixtures.InstanceUrl, "Account"));

        var callsBefore = client.Soql.Count;
        await new FlsFetcher(client, _store, FlsFixtures.InstanceUrl).FetchAsync(new[] { "Account" });
        Assert.True(client.Soql.Count > callsBefore, "cache cleared ⇒ Salesforce must be re-queried");
    }

    [Fact]
    public async Task FlsCacheIsInvalidatedByForceRefresh()
    {
        var client = HappyClient();
        var fetcher = new FlsFetcher(client, _store, FlsFixtures.InstanceUrl);
        await fetcher.FetchAsync(new[] { "Account" });
        var callsBefore = client.Soql.Count;

        await fetcher.FetchAsync(new[] { "Account" }, forceRefresh: true);
        Assert.True(client.Soql.Count > callsBefore);
    }

    [Fact]
    public async Task FlsCacheIsKeyedPerOrg()
    {
        var client = HappyClient();
        await new FlsFetcher(client, _store, FlsFixtures.InstanceUrl).FetchAsync(new[] { "Account" });

        Assert.Null(_store.GetCachedFls("https://other.my.salesforce.com", "Account"));
    }

    [Fact]
    public async Task DescribeFallbackWhenFieldPermissionsIsNotQueryable()
    {
        // Some orgs deny the integration user access to FieldPermissions. Fall
        // back to describe: fields Salesforce hides from the running user simply
        // do not appear in the describe payload, and must be dropped.
        var client = new ScriptedSfClient
        {
            OnQueryAll = soql => throw new InvalidOperationException(
                "Salesforce query failed [400]: INSUFFICIENT_ACCESS on FieldPermissions"),
            OnDescribe = _ => new JsonObject
            {
                ["fields"] = new JsonArray
                {
                    new JsonObject { ["name"] = "Id" },
                    new JsonObject { ["name"] = "Name" },
                    // Compensation__c and Margin__c are absent ⇒ invisible.
                },
            },
        };
        var fetcher = new FlsFetcher(client, _store, FlsFixtures.InstanceUrl);

        var snapshot = await fetcher.FetchAsync(new[] { "Account" });
        var perms = snapshot.Get("Account");

        Assert.NotNull(perms);
        Assert.True(perms!.FromDescribeFallback);
        Assert.Contains("Account", client.Describes);

        var drops = FlsPolicy.ComputeDrops(
            perms, new[] { "Name", "Compensation__c", "Margin__c" }, FlsMode.Strict);
        var dropped = drops.Select(d => d.Field).ToHashSet();
        Assert.Contains("Compensation__c", dropped);
        Assert.Contains("Margin__c", dropped);
        Assert.DoesNotContain("Name", dropped);
    }

    [Fact]
    public async Task FetchWithoutAStoreStillWorks()
    {
        var client = HappyClient();
        var fetcher = new FlsFetcher(client, store: null, instanceUrl: FlsFixtures.InstanceUrl);
        var snapshot = await fetcher.FetchAsync(new[] { "Account" });

        Assert.NotNull(snapshot.Get("Account"));
    }
}

/// <summary>Env gating: FLS_ENFORCEMENT default ON, FLS_MODE default strict.</summary>
[Collection("EnvVars")]
public sealed class FlsEnvGateTests : IDisposable
{
    private readonly Dictionary<string, string?> _saved = new();

    public FlsEnvGateTests()
    {
        foreach (var v in new[] { FlsSettings.EnforcementEnvVar, FlsSettings.ModeEnvVar })
        {
            _saved[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved)
            Environment.SetEnvironmentVariable(k, v);
    }

    [Fact]
    public void EnforcementDefaultsOn()
    {
        Assert.True(FlsSettings.Enforcement);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("OFF")]
    public void EnforcementCanBeTurnedOff(string raw)
    {
        Environment.SetEnvironmentVariable(FlsSettings.EnforcementEnvVar, raw);
        Assert.False(FlsSettings.Enforcement);
    }

    [Fact]
    public void ModeDefaultsToStrict()
    {
        Assert.Equal(FlsMode.Strict, FlsSettings.Mode);
    }

    [Fact]
    public void ModeCanBeSetToPermissive()
    {
        Environment.SetEnvironmentVariable(FlsSettings.ModeEnvVar, "permissive");
        Assert.Equal(FlsMode.Permissive, FlsSettings.Mode);
    }

    /// <summary>
    /// FLS_ENFORCEMENT=off is the escape hatch: the pre-WP-SF-2 behaviour must
    /// return BYTE-IDENTICALLY — including the old `flsFields` precedent that
    /// nulled the property and left the content body leaking.
    /// </summary>
    [Fact]
    public void EnforcementOffRestoresPreviousBehaviourByteIdentically()
    {
        // Baseline: no flsFields at all, enforcement irrelevant — the untouched item.
        var pristine = FlsFixtures.ConvertOne(FlsFixtures.Handler()).ToJsonString();

        Environment.SetEnvironmentVariable(FlsSettings.EnforcementEnvVar, "false");

        // With enforcement off, a fetched drop set is inert…
        var withFetched = FlsFixtures.Handler();
        withFetched.ApplyFlsDrops(new[] { "Compensation__c", "Margin__c" });
        Assert.Equal(pristine, FlsFixtures.ConvertOne(withFetched).ToJsonString());

        // …and the manual list behaves exactly as it did before: property nulled,
        // content body untouched (the historical leak, preserved verbatim).
        var manual = FlsFixtures.Handler(new[] { "Compensation" });
        var item = FlsFixtures.ConvertOne(manual);
        Assert.True(((JsonObject)item["properties"]!)["Compensation"] is null);
        Assert.Contains(FlsFixtures.MarginSecret, FlsFixtures.ContentBody(item));
    }

    [Fact]
    public void EnforcementOnIsTheDefaultAndDropsTheField()
    {
        var handler = FlsFixtures.Handler();
        handler.ApplyFlsDrops(new[] { "Compensation__c", "Margin__c" });
        FlsFixtures.AssertDroppedFromBothLoops(
            FlsFixtures.ConvertOne(handler), FlsFixtures.CompensationSecret);
    }
}

/// <summary>
/// The production wiring: FlsEnforcement.ApplyAsync is what Ingest calls. It must
/// fetch, compute drops, push them into the handlers, and emit the manifest — and
/// the resulting item must be clean in BOTH loops.
/// </summary>
public class FlsEnforcementWiringTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("fls_wire_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Fetcher returning a canned snapshot — no Salesforce, no store.</summary>
    private sealed class StubFetcher : FlsFetcher
    {
        private readonly FlsSnapshot _snapshot;
        public int FetchCount;

        public StubFetcher(FlsSnapshot snapshot)
            : base(new FlsFetcherTests.ScriptedSfClient(), null, FlsFixtures.InstanceUrl)
        {
            _snapshot = snapshot;
        }

        public override Task<FlsSnapshot> FetchAsync(IEnumerable<string> objectNames, bool forceRefresh = false)
        {
            FetchCount++;
            // Force enumeration so the caller's object list is exercised.
            _ = objectNames.ToList();
            return Task.FromResult(_snapshot);
        }
    }

    private static FlsSnapshot SnapshotWithSplitField()
    {
        var snapshot = new FlsSnapshot();
        var perms = new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string> { "psSalesRep", "psManager" },
            governedFields: new HashSet<string> { "Compensation__c", "Margin__c" },
            readersByField: new Dictionary<string, HashSet<string>>
            {
                ["Compensation__c"] = new() { "psManager" },   // 1 of 2 → strict drops
                ["Margin__c"] = new() { "psManager", "psSalesRep" },   // 2 of 2 → kept
            });
        snapshot.Set("Account", perms);
        return snapshot;
    }

    [Fact]
    public async Task ApplyAsyncDropsTheSplitFieldFromBothLoopsAndWritesTheManifest()
    {
        var handler = FlsFixtures.Handler();
        var handlers = new Dictionary<string, SalesforceObjectHandler> { ["Account"] = handler };
        var fetcher = new StubFetcher(SnapshotWithSplitField());

        var summary = await FlsEnforcement.ApplyAsync(
            fetcher, handlers, connectorId: "WireTest", mode: FlsMode.Strict, logsDir: _tmp);

        Assert.Equal(1, fetcher.FetchCount);
        Assert.Contains("Compensation__c", handler.EffectiveFlsFields);
        Assert.DoesNotContain("Margin__c", handler.EffectiveFlsFields);

        // The item is clean in BOTH loops for the dropped field, and keeps the other.
        var item = FlsFixtures.ConvertOne(handler);
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.CompensationSecret);
        Assert.Contains(FlsFixtures.MarginSecret, item.ToJsonString());

        // …and the audit artifact records what happened and why.
        Assert.Equal(2, summary["Account"].PrincipalsInScope);
        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(_tmp, "fls_manifest_WireTest.json")))!.AsObject();
        var dropped = manifest["objects"]!["Account"]!["dropped"]!.AsArray();
        Assert.Equal("Compensation__c", Assert.Single(dropped)!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task PermissiveModeKeepsTheSplitFieldThroughTheSameWiring()
    {
        var handler = FlsFixtures.Handler();
        var handlers = new Dictionary<string, SalesforceObjectHandler> { ["Account"] = handler };

        var summary = await FlsEnforcement.ApplyAsync(
            new StubFetcher(SnapshotWithSplitField()), handlers,
            connectorId: "WirePermissive", mode: FlsMode.Permissive, logsDir: _tmp);

        Assert.Empty(summary);
        Assert.Empty(handler.EffectiveFlsFields);
        // The documented weakening: the rep can now see a field Salesforce denies them.
        Assert.Contains(FlsFixtures.CompensationSecret, FlsFixtures.ConvertOne(handler).ToJsonString());
    }

    [Fact]
    public async Task ApplyAsyncPreservesTheOperatorsManualList()
    {
        // Fetched permissions say Margin__c is readable by everyone, but the
        // operator listed it explicitly — the union must keep it dropped.
        var handler = FlsFixtures.Handler(new[] { "Margin__c" });
        var handlers = new Dictionary<string, SalesforceObjectHandler> { ["Account"] = handler };

        await FlsEnforcement.ApplyAsync(
            new StubFetcher(SnapshotWithSplitField()), handlers,
            connectorId: "WireManual", mode: FlsMode.Strict, logsDir: _tmp);

        Assert.Contains("Margin__c", handler.EffectiveFlsFields);
        Assert.Contains("Compensation__c", handler.EffectiveFlsFields);
        var item = FlsFixtures.ConvertOne(handler);
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.MarginSecret);
        FlsFixtures.AssertDroppedFromBothLoops(item, FlsFixtures.CompensationSecret);
    }

    [Fact]
    public async Task ChildHandlersAreEnforcedToo()
    {
        // Inline child records run through the same two loops — they must not be
        // an unguarded back door.
        var parentConfig = FlsFixtures.ObjectConfig();
        var childConfig = new JsonObject
        {
            ["objectName"] = "Account",
            ["parentObjectName"] = "Account",
            ["objectNameAsChild"] = "Children",
            ["selectedFields"] = new JsonObject { ["Compensation__c"] = "Compensation" },
        };
        var child = new SalesforceObjectHandler(childConfig);
        var parent = new SalesforceObjectHandler(parentConfig, childHandlers: new List<SalesforceObjectHandler> { child })
        {
            GraphSchemaProperties = FlsFixtures.GraphSchemaProperties(),
        };

        await FlsEnforcement.ApplyAsync(
            new StubFetcher(SnapshotWithSplitField()),
            new Dictionary<string, SalesforceObjectHandler> { ["Account"] = parent },
            connectorId: "WireChild", mode: FlsMode.Strict, logsDir: _tmp);

        Assert.Contains("Compensation__c", child.EffectiveFlsFields);
    }
}

/// <summary>The audit artifact: logs/fls_manifest_{connectorId}.json.</summary>
public class FlsManifestTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("fls_manifest_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ManifestListsDroppedFieldsPerObjectWithReasons()
    {
        var drops = new Dictionary<string, FlsObjectDrops>
        {
            ["Account"] = new FlsObjectDrops(
                PrincipalsInScope: 2,
                Drops: new List<FlsDrop>
                {
                    new("Compensation__c", "strict: readable by 1 of 2 principal(s) in scope"),
                    new("Margin__c", "strict: readable by 0 of 2 principal(s) in scope"),
                }),
        };

        var path = FlsManifest.Write("SalesforceCRM", FlsMode.Strict, enforcement: true, drops, logsDir: _tmp);

        Assert.Equal(Path.Combine(_tmp, "fls_manifest_SalesforceCRM.json"), path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.Equal("SalesforceCRM", doc["connectorId"]!.GetValue<string>());
        Assert.Equal("strict", doc["mode"]!.GetValue<string>());
        Assert.True(doc["enforcement"]!.GetValue<bool>());
        Assert.NotNull(doc["generatedAt"]);

        var account = doc["objects"]!.AsObject()["Account"]!.AsObject();
        Assert.Equal(2, account["principalsInScope"]!.GetValue<int>());
        var dropped = account["dropped"]!.AsArray();
        Assert.Equal(2, dropped.Count);
        Assert.Equal("Compensation__c", dropped[0]!["field"]!.GetValue<string>());
        Assert.Contains("strict", dropped[0]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void ManifestIsStableAcrossRewrites()
    {
        // A sibling connector consumes this file — the shape (and field ordering)
        // must not drift between runs.
        var drops = new Dictionary<string, FlsObjectDrops>
        {
            ["Account"] = new FlsObjectDrops(1, new List<FlsDrop> { new("B__c", "r"), new("A__c", "r") }),
        };
        var first = File.ReadAllText(FlsManifest.Write("C1", FlsMode.Strict, true, drops, _tmp));
        var second = File.ReadAllText(FlsManifest.Write("C1", FlsMode.Strict, true, drops, _tmp));

        var a = JsonNode.Parse(first)!.AsObject();
        var b = JsonNode.Parse(second)!.AsObject();
        a.Remove("generatedAt");
        b.Remove("generatedAt");
        Assert.Equal(a.ToJsonString(), b.ToJsonString());

        // Dropped fields are sorted so diffs are meaningful.
        var fields = a["objects"]!["Account"]!["dropped"]!.AsArray()
            .Select(d => d!["field"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "A__c", "B__c" }, fields);
    }

    [Fact]
    public void ManifestIsWrittenEvenWhenNothingWasDropped()
    {
        var path = FlsManifest.Write(
            "C2", FlsMode.Permissive, enforcement: true,
            new Dictionary<string, FlsObjectDrops>(), _tmp);

        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("permissive", doc["mode"]!.GetValue<string>());
        Assert.Empty(doc["objects"]!.AsObject());
    }
}
