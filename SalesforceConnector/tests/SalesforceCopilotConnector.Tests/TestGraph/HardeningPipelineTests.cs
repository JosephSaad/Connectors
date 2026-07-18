// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// HardeningPipelineTests.cs
// -------------------------
// End-to-end wiring of the ingest-time hardening features through
// Ingest.IngestChunkGraphBatchAsync: classification enforcement (#6b) narrows
// the uploaded item ACL, the ACL scale guard (#9) collapses/drops oversized
// ACLs, item TTL (#8) stamps expirationDateTime on the uploaded body, and the
// decision ledger (#11) records the restriction/exclusion decisions.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestGraph;

[Collection("EnvVars")]
public sealed class HardeningPipelineTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("hardening_pipe_").FullName;
    private readonly Dictionary<string, string?> _savedEnv = new();

    private static readonly string[] TouchedVars =
    {
        Classification.FieldEnvVar, Classification.EnforceAclEnvVar, Classification.RestrictedGroupEnvVar,
        Classification.RestrictedValuesEnvVar,
        AclScaleGuard.ThresholdEnvVar, AclScaleGuard.ActionEnvVar, AclScaleGuard.GroupEnvVar,
        ItemExpiry.TtlDaysEnvVar,
    };

    public HardeningPipelineTests()
    {
        foreach (var v in TouchedVars)
        {
            _savedEnv[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }
    }

    public void Dispose()
    {
        foreach (var (k, v) in _savedEnv)
            Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    /// <summary>Transformer that echoes the record id (as a "__rid" property) and the
    /// ACL it received into the output item, so the effective (post-guard /
    /// post-enforcement) ACL is observable in the captured upload bodies.</summary>
    private sealed class EchoAclTransformer : SalesforceItemTransformer
    {
        public EchoAclTransformer()
            : base("https://test.my.salesforce.com", new JsonArray(), new CheckpointSupport.StubConverter())
        {
        }

        public override Dictionary<string, SalesforceObjectHandler> Handlers => new();

        public override List<JsonObject> TransformRecord(JsonObject item, List<Dictionary<string, string>>? acl = null)
        {
            var aclArray = new JsonArray();
            foreach (var ace in acl ?? new List<Dictionary<string, string>>())
            {
                var o = new JsonObject();
                foreach (var (k, v) in ace)
                    o[k] = v;
                aclArray.Add(o);
            }
            return new List<JsonObject>
            {
                new()
                {
                    ["id"] = item["Id"]!.GetValue<string>(),
                    ["properties"] = new JsonObject
                    {
                        ["ObjectName"] = "Account",
                        ["__rid"] = item["Id"]!.GetValue<string>(),
                    },
                    ["content"] = new JsonObject { ["value"] = "" },
                    ["acl"] = aclArray,
                },
            };
        }
    }

    private static List<Dictionary<string, string>> UserAcl(int n)
    {
        var acl = new List<Dictionary<string, string>>(n);
        for (var i = 0; i < n; i++)
            acl.Add(new() { ["accessType"] = "grant", ["type"] = "user", ["value"] = $"user-{i}" });
        return acl;
    }

    /// <summary>Run one chunk through the real ingest path, capturing every uploaded body.</summary>
    private static async Task<List<JsonObject>> RunChunkAsync(
        List<JsonObject> records,
        Dictionary<string, List<Dictionary<string, string>>> aclMap,
        DecisionLedger? ledger)
    {
        var captured = new List<JsonObject>();
        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = payload =>
            {
                var responses = new List<JsonObject>();
                foreach (var req in payload)
                {
                    if (req["body"] is JsonObject body)
                        captured.Add(body);
                    responses.Add(new JsonObject { ["id"] = req["id"]!.GetValue<string>(), ["status"] = 200 });
                }
                return responses;
            },
        };

        await Ingest.IngestChunkGraphBatchAsync(
            TestFixtures.TestConfig(), client, new EchoAclTransformer(), records, aclMap,
            new IngestionStats(), 20, objectType: "Account",
            concurrency: new AdaptiveConcurrency(1), statsLock: new object(), decisionLedger: ledger);

        return captured;
    }

    /// <summary>Return the ACL entries of the uploaded body for record <paramref name="id"/>.</summary>
    private static List<JsonObject> AclOf(List<JsonObject> bodies, string id)
    {
        foreach (var b in bodies)
        {
            if (b["properties"] is JsonObject p &&
                p.TryGetPropertyValue("__rid", out var rid) &&
                rid!.GetValue<string>() == id)
            {
                return (b["acl"] as JsonArray)!.Select(n => n!.AsObject()).ToList();
            }
        }
        throw new InvalidOperationException($"no uploaded body for {id}");
    }

    private static JsonObject BodyOf(List<JsonObject> bodies, string id) =>
        bodies.Single(b => b["properties"] is JsonObject p &&
            p.TryGetPropertyValue("__rid", out var rid) && rid!.GetValue<string>() == id);

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClassificationEnforcementRestrictsRestrictedItemAndStampsTtlAndLedgers()
    {
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Classification__c");
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, "true");
        Environment.SetEnvironmentVariable(Classification.RestrictedGroupEnvVar, "grp-restricted-guid");
        Environment.SetEnvironmentVariable(ItemExpiry.TtlDaysEnvVar, "30");

        var restricted = new JsonObject { ["Id"] = "001", ["objectType"] = "Account", ["Classification__c"] = "Restricted" };
        var normal = new JsonObject { ["Id"] = "002", ["objectType"] = "Account", ["Classification__c"] = "Internal" };
        var aclMap = new Dictionary<string, List<Dictionary<string, string>>>
        {
            ["001"] = UserAcl(3),
            ["002"] = UserAcl(3),
        };
        var ledger = new DecisionLedger("hp", _tmp);

        var bodies = await RunChunkAsync(new List<JsonObject> { restricted, normal }, aclMap, ledger);

        // #6b — Restricted item: ACL collapsed to a single Entra group grant.
        var acl001 = AclOf(bodies, "001");
        var ace = Assert.Single(acl001);
        Assert.Equal("grant", ace["accessType"]!.GetValue<string>());
        Assert.Equal("group", ace["type"]!.GetValue<string>());
        Assert.Equal("grp-restricted-guid", ace["value"]!.GetValue<string>());

        // Non-restricted item: ACL left untouched (3 user ACEs).
        Assert.Equal(3, AclOf(bodies, "002").Count);

        // #8 — both items stamped with an expiration.
        Assert.True(BodyOf(bodies, "001").ContainsKey(ItemExpiry.ExpirationProperty));
        Assert.True(BodyOf(bodies, "002").ContainsKey(ItemExpiry.ExpirationProperty));

        // #11 — exactly one acl-restriction decision recorded; chain verifies.
        var entry = Assert.Single(ledger.ReadAll());
        Assert.Equal(DecisionKinds.AclRestriction, entry.Decision);
        Assert.Equal("001", entry.ItemId);
        Assert.True(ledger.Verify(out _));
    }

    [Fact]
    public async Task ScaleGuardFallbackExcludesOversizedItemAndLedgersExclusion()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, "2");
        Environment.SetEnvironmentVariable(AclScaleGuard.ActionEnvVar, "fallback");

        var big = new JsonObject { ["Id"] = "001", ["objectType"] = "Account" };
        var aclMap = new Dictionary<string, List<Dictionary<string, string>>> { ["001"] = UserAcl(50) };
        var ledger = new DecisionLedger("hp", _tmp);

        var bodies = await RunChunkAsync(new List<JsonObject> { big }, aclMap, ledger);

        var ace = Assert.Single(AclOf(bodies, "001"));
        Assert.Equal("deny", ace["accessType"]!.GetValue<string>());
        Assert.Equal("everyone", ace["type"]!.GetValue<string>());

        var entry = Assert.Single(ledger.ReadAll());
        Assert.Equal(DecisionKinds.Exclusion, entry.Decision);
        Assert.Equal("001", entry.ItemId);
    }

    [Fact]
    public async Task ScaleGuardGroupActionCollapsesOversizedAcl()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, "10");
        Environment.SetEnvironmentVariable(AclScaleGuard.ActionEnvVar, "group");
        Environment.SetEnvironmentVariable(AclScaleGuard.GroupEnvVar, "big-org-group");

        var big = new JsonObject { ["Id"] = "001", ["objectType"] = "Account" };
        var aclMap = new Dictionary<string, List<Dictionary<string, string>>> { ["001"] = UserAcl(500) };
        var ledger = new DecisionLedger("hp", _tmp);

        var bodies = await RunChunkAsync(new List<JsonObject> { big }, aclMap, ledger);

        var ace = Assert.Single(AclOf(bodies, "001"));
        Assert.Equal("group", ace["type"]!.GetValue<string>());
        Assert.Equal("big-org-group", ace["value"]!.GetValue<string>());
        Assert.Equal(DecisionKinds.AclRestriction, Assert.Single(ledger.ReadAll()).Decision);
    }

    [Fact]
    public async Task DefaultsAreByteIdentical_NoExpiry_NoAclChange_NoLedger()
    {
        // All features off (env cleared in ctor): ACL unchanged, no expiry stamped,
        // and a null ledger records nothing.
        var record = new JsonObject { ["Id"] = "001", ["objectType"] = "Account", ["Classification__c"] = "Restricted" };
        var aclMap = new Dictionary<string, List<Dictionary<string, string>>> { ["001"] = UserAcl(3) };

        var bodies = await RunChunkAsync(new List<JsonObject> { record }, aclMap, ledger: null);

        Assert.Equal(3, AclOf(bodies, "001").Count);   // untouched
        Assert.False(BodyOf(bodies, "001").ContainsKey(ItemExpiry.ExpirationProperty));
    }
}
