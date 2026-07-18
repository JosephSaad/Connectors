// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// HardeningStressTests.cs
// -----------------------
// Adversarial / concurrency stress + regression coverage for the bank-grade
// hardening controls (#6b classification enforcement, #8 item TTL, #9 ACL scale
// guard, #11 decision ledger, #3 owner-only dirs). Everything runs offline
// through the real Ingest.IngestChunkGraphBatchAsync pipeline (or the real
// control types) — no network, no Salesforce, no Graph.
//
// Regressions fixed here (red→green):
//   * A decision-ledger fault (corrupt/tampered chain or disk error) used to
//     propagate out of the transform loop and silently drop EVERY record in the
//     chunk. The append is now best-effort — the ACL decision is still applied,
//     the item still ingests, and the fault is logged. See
//     LedgerFaultDoesNotDropTheChunk.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestGraph;

[Collection("EnvVars")]
public sealed class HardeningStressTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("hardening_stress_").FullName;
    private readonly Dictionary<string, string?> _savedEnv = new();

    private static readonly string[] TouchedVars =
    {
        Classification.FieldEnvVar, Classification.EnforceAclEnvVar, Classification.RestrictedGroupEnvVar,
        Classification.RestrictedValuesEnvVar,
        AclScaleGuard.ThresholdEnvVar, AclScaleGuard.ActionEnvVar, AclScaleGuard.GroupEnvVar,
        ItemExpiry.TtlDaysEnvVar,
    };

    public HardeningStressTests()
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

    // ── Harness (mirrors HardeningPipelineTests) ──────────────────────────────

    /// <summary>Transformer that echoes the record id + the ACL it received so the
    /// effective (post-guard / post-enforcement) ACL is observable in the uploaded body.</summary>
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

    private static async Task<List<JsonObject>> RunChunkAsync(
        List<JsonObject> records,
        Dictionary<string, List<Dictionary<string, string>>> aclMap,
        DecisionLedger? ledger,
        IngestionStats? stats = null)
    {
        var captured = new List<JsonObject>();
        var captureLock = new object();
        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = payload =>
            {
                var responses = new List<JsonObject>();
                foreach (var req in payload)
                {
                    if (req["body"] is JsonObject body)
                    {
                        lock (captureLock)
                            captured.Add(body);
                    }
                    responses.Add(new JsonObject { ["id"] = req["id"]!.GetValue<string>(), ["status"] = 200 });
                }
                return responses;
            },
        };

        await Ingest.IngestChunkGraphBatchAsync(
            TestFixtures.TestConfig(), client, new EchoAclTransformer(), records, aclMap,
            stats ?? new IngestionStats(), 20, objectType: "Account",
            concurrency: new AdaptiveConcurrency(1), statsLock: new object(), decisionLedger: ledger);

        return captured;
    }

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

    // ── #11 regression: a ledger fault must not drop the chunk ────────────────

    [Fact]
    public async Task LedgerFaultDoesNotDropTheChunk()
    {
        // A decision-ledger whose chain is already corrupt: any Append() throws
        // InvalidDataException ("refusing to append to a corrupt chain").
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Classification__c");
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, "true");
        Environment.SetEnvironmentVariable(Classification.RestrictedGroupEnvVar, "grp-restricted-guid");

        var seed = new DecisionLedger("faulty", _tmp);
        seed.Append(DecisionKinds.AclRestriction, "000", "Account", "seed");
        File.AppendAllText(seed.Path, "{ not valid json — torn/tampered line\n");
        // Fresh instance so no cached tail masks the corruption; its Append throws.
        var corrupt = new DecisionLedger("faulty", _tmp);
        Assert.Throws<InvalidDataException>(() =>
            corrupt.Append(DecisionKinds.AclRestriction, "zz", "Account", "should throw"));

        var restricted = new JsonObject { ["Id"] = "001", ["objectType"] = "Account", ["Classification__c"] = "Restricted" };
        var normal = new JsonObject { ["Id"] = "002", ["objectType"] = "Account", ["Classification__c"] = "Internal" };
        var aclMap = new Dictionary<string, List<Dictionary<string, string>>>
        {
            ["001"] = UserAcl(3),
            ["002"] = UserAcl(3),
        };
        var stats = new IngestionStats();

        // Before the fix this threw InvalidDataException out of the transform loop,
        // aborting the whole chunk (both records silently dropped, never
        // dead-lettered). After the fix the append is best-effort: the ACL
        // restriction is still applied and both records ingest.
        var bodies = await RunChunkAsync(
            new List<JsonObject> { restricted, normal }, aclMap, corrupt, stats);

        Assert.Equal(2, stats.SuccessCount);
        Assert.Equal(0, stats.FailedCount);

        // The Restricted item is STILL locked to the Entra group despite the ledger
        // fault — the security control does not depend on the audit write.
        var ace = Assert.Single(AclOf(bodies, "001"));
        Assert.Equal("group", ace["type"]!.GetValue<string>());
        Assert.Equal("grp-restricted-guid", ace["value"]!.GetValue<string>());
        Assert.Equal(3, AclOf(bodies, "002").Count);   // untouched
    }

    // ── #11 concurrency: shared ledger, many concurrent appends via the pipeline ──

    [Fact]
    public async Task DecisionLedger_ConcurrentPipelineAppends_ChainStaysValid()
    {
        // One ledger shared by many parallel "object worker" chunk calls, exactly as
        // IngestContentAsync shares a single ledger across workers. Every restricted
        // item appends one acl-restriction line; the hash chain must stay valid with
        // no torn/interleaved lines and a dense 1..N sequence.
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Classification__c");
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, "true");
        Environment.SetEnvironmentVariable(Classification.RestrictedGroupEnvVar, "grp-restricted-guid");

        var ledger = new DecisionLedger("concurrent", _tmp);
        const int workers = 12;
        const int perWorker = 40;

        var tasks = new List<Task>();
        for (var w = 0; w < workers; w++)
        {
            var worker = w;
            tasks.Add(Task.Run(async () =>
            {
                var records = new List<JsonObject>();
                var aclMap = new Dictionary<string, List<Dictionary<string, string>>>();
                for (var i = 0; i < perWorker; i++)
                {
                    var id = $"w{worker}-{i}";
                    records.Add(new JsonObject
                    {
                        ["Id"] = id,
                        ["objectType"] = "Account",
                        ["Classification__c"] = "Restricted",
                    });
                    aclMap[id] = UserAcl(2);
                }
                await RunChunkAsync(records, aclMap, ledger);
            }));
        }
        await Task.WhenAll(tasks);

        // Chain integrity: Verify passes and every entry parses.
        Assert.True(ledger.Verify(out var broken), $"chain broke at seq {broken}");
        var all = ledger.ReadAll();
        Assert.Equal(workers * perWorker, all.Count);

        // Dense, unique 1..N sequence — no torn or interleaved lines.
        var seqs = all.Select(e => e.Seq).OrderBy(s => s).ToList();
        for (var i = 0; i < seqs.Count; i++)
            Assert.Equal(i + 1, seqs[i]);

        // Every line is a well-formed JSON object (no partial writes on disk).
        foreach (var line in File.ReadAllLines(ledger.Path).Where(l => l.Trim().Length > 0))
            Assert.NotNull(JsonNode.Parse(line));
    }

    [Fact]
    public void DecisionLedger_ChainContinuesAcrossRestart()
    {
        // Instance A writes; a brand-new instance B (fresh tail, same path) — a
        // process restart — continues the same chain and Verify still passes.
        var a = new DecisionLedger("restart", _tmp);
        var e1 = a.Append(DecisionKinds.AclRestriction, "001", "Account", "r1");
        var e2 = a.Append(DecisionKinds.Exclusion, "002", "Case", "r2");

        var b = new DecisionLedger("restart", _tmp);
        var e3 = b.Append(DecisionKinds.AclRestriction, "003", "Lead", "r3");

        Assert.Equal(3, e3.Seq);
        Assert.Equal(e2.Hash, e3.PrevHash);   // link survives the "restart"
        Assert.True(b.Verify(out var broken));
        Assert.Equal(0, broken);
        Assert.Equal(3, b.ReadAll().Count);
        Assert.Equal(e1.Hash, b.ReadAll()[0].Hash);
    }

    // ── #6b + #9: a Restricted item is never re-widened by the scale guard ────

    [Fact]
    public async Task RestrictedItemsNeverReWidened_UnderConcurrentBatchWorkers()
    {
        // Classification enforcement locks Restricted items to the Entra group; the
        // scale guard runs AFTER, on the already-narrowed (1-ACE) ACL, so it must
        // never re-expand a Restricted item back to its (large) source principals.
        // Threshold=2 with fallback would deny-everyone a >2-ACE item — proving the
        // guard would have fired on the ORIGINAL 50-user ACL had it run first.
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Classification__c");
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, "true");
        Environment.SetEnvironmentVariable(Classification.RestrictedGroupEnvVar, "grp-restricted-guid");
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, "2");
        Environment.SetEnvironmentVariable(AclScaleGuard.ActionEnvVar, "fallback");

        var ledger = new DecisionLedger("rewiden", _tmp);
        var wrong = new ConcurrentQueue<string>();
        const int workers = 8;
        const int perWorker = 25;

        var tasks = new List<Task>();
        for (var w = 0; w < workers; w++)
        {
            var worker = w;
            tasks.Add(Task.Run(async () =>
            {
                var records = new List<JsonObject>();
                var aclMap = new Dictionary<string, List<Dictionary<string, string>>>();
                for (var i = 0; i < perWorker; i++)
                {
                    var id = $"w{worker}-{i}";
                    records.Add(new JsonObject
                    {
                        ["Id"] = id,
                        ["objectType"] = "Account",
                        ["Classification__c"] = "Restricted",
                    });
                    aclMap[id] = UserAcl(50);   // oversized on purpose
                }
                var bodies = await RunChunkAsync(records, aclMap, ledger);
                foreach (var rec in records)
                {
                    var id = rec["Id"]!.GetValue<string>();
                    var acl = AclOf(bodies, id);
                    if (acl.Count != 1)
                        wrong.Enqueue($"{id}: {acl.Count} ACEs (expected 1 group grant)");
                    else
                    {
                        var ace = acl[0];
                        if (ace["type"]!.GetValue<string>() != "group" ||
                            ace["value"]!.GetValue<string>() != "grp-restricted-guid")
                            wrong.Enqueue($"{id}: re-widened to {ace["type"]}/{ace["value"]}");
                    }
                }
            }));
        }
        await Task.WhenAll(tasks);

        Assert.True(wrong.IsEmpty,
            "Restricted item re-widened / not locked to the enforcement group: " +
            string.Join(" | ", wrong.Take(5)));
        Assert.True(ledger.Verify(out _));
        // Every restricted item recorded exactly one acl-restriction decision.
        Assert.Equal(workers * perWorker, ledger.ReadAll().Count);
    }
}
