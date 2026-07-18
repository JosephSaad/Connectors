// HardeningFixesTests.cs
// ----------------------
// Focused tests for the bank-grade hardening fixes:
//   #3  restrictive filesystem permissions (0700 on POSIX)
//   #4  financial default → filter (protective) + loud tag-mode note
//   #5  identity sync on incremental default → true
//   #6  classification enforcement (advisory by default; enforce restricts)
//   #8  optional item TTL (expirationDateTime)
//   #10 webhook anti-replay authenticator (fresh/stale/replay/missing-per-flag)
//   #11 immutable hash-chained decision ledger (chain links, verify tamper)
//   #12 bounded webhook debouncer (drop-oldest back-pressure)

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Commands;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;
using ClarizenConnector.Webhook;

namespace ClarizenConnector.Tests;

// ── #3 filesystem permissions ────────────────────────────────────────────────

public class DirectoryHardeningTests
{
    [Fact]
    public void EnsureOwnerOnly_CreatesDirectory_0700_OnPosix()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "state");
        DirectoryHardening.EnsureOwnerOnly(target);

        Assert.True(Directory.Exists(target));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(target);
            Assert.Equal(DirectoryHardening.OwnerOnly, mode);   // rwx------ (0700)
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
            // No group/other bits.
            Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
            Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
        }
    }

    [Fact]
    public void EnsureOwnerOnly_NeverThrows_OnBlankPath()
    {
        DirectoryHardening.EnsureOwnerOnly("");   // must be a silent no-op
    }
}

// ── #4 financial default + #5 identity + #6/#8 config ────────────────────────

public class SafeDefaultsConfigTests
{
    private static EnvScope BaseEnv() => new(
        ("CONNECTOR_ID", "ClarizenAdaptiveWork"),
        ("CONNECTOR_NAME", "t"),
        ("CONNECTOR_DESCRIPTION", "t"),
        ("CLARIZEN_USERNAME", "u"),
        ("SECRET_CLARIZEN_PASSWORD", "p"),
        ("AAD_APP_TENANT_ID", "tenant"),
        ("AAD_APP_CLIENT_ID", "c"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
        ("USE_KEY_VAULT", null),
        ("FINANCIAL_DATA_MODE", null),
        ("FINANCIAL_DATA_GROUP_ID", null),
        ("CLASSIFICATION_ENFORCE_ACL", null),
        ("CLASSIFICATION_RESTRICTED_GROUP_ID", null),
        ("GRAPH_ITEM_TTL_DAYS", null));

    [Fact]
    public void FinancialDataMode_DefaultsToFilter()   // #4
    {
        using var scope = BaseEnv();
        Assert.Equal("filter", AppConfig.Load().FinancialDataMode);
    }

    [Fact]
    public void IdentitySyncOnIncremental_DefaultsOn()   // #5
    {
        using var scope = new EnvScope(("IDENTITY_SYNC_ON_INCREMENTAL", null));
        Assert.True(EnvFlags.IdentitySyncOnIncremental);

        scope.Set("IDENTITY_SYNC_ON_INCREMENTAL", "false");
        Assert.False(EnvFlags.IdentitySyncOnIncremental);

        scope.Set("IDENTITY_SYNC_ON_INCREMENTAL", "true");
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
    }

    [Fact]
    public void ClassificationEnforceAcl_WithoutGroup_Throws()   // #6
    {
        using var scope = BaseEnv();
        scope.Set("CLASSIFICATION_ENFORCE_ACL", "true");
        var exc = Assert.Throws<ArgumentException>(AppConfig.Load);
        Assert.Contains("CLASSIFICATION_RESTRICTED_GROUP_ID", exc.Message);
    }

    [Fact]
    public void ClassificationEnforceAcl_WithGroup_Loads()   // #6
    {
        using var scope = BaseEnv();
        scope.Set("CLASSIFICATION_ENFORCE_ACL", "true");
        scope.Set("CLASSIFICATION_RESTRICTED_GROUP_ID", "grp-restricted");
        var config = AppConfig.Load();
        Assert.True(config.ClassificationEnforceAcl);
        Assert.Equal("grp-restricted", config.ClassificationRestrictedGroupId);
    }

    [Fact]
    public void GraphItemTtlDays_ParsedAndClonePreserved()   // #8
    {
        using var scope = BaseEnv();
        scope.Set("GRAPH_ITEM_TTL_DAYS", "30");
        var config = AppConfig.Load();
        Assert.Equal(30, config.GraphItemTtlDays);
        Assert.Equal(30, config.CloneForConnection("Other").GraphItemTtlDays);
        Assert.True(config.CloneForConnection("Other").ClassificationEnforceAcl == config.ClassificationEnforceAcl);
    }
}

public class ValidateConfigHardeningTests
{
    private static string SchemaPath => Path.Combine(AppContext.BaseDirectory, "config", "schema.json");
    private static string GraphSchemaPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json");

    private static EnvScope GoodEnv() => new(
        ("CONNECTOR_ID", "ClarizenAdaptiveWork"),
        ("CLARIZEN_USERNAME", "svc@example.com"),
        ("SECRET_CLARIZEN_PASSWORD", "pw"),
        ("AAD_APP_TENANT_ID", "tenant"),
        ("AAD_APP_CLIENT_ID", "client"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "secret"),
        ("USE_KEY_VAULT", null),
        ("USE_SQL_SERVER", null),
        ("HA_MODE", null),
        ("FINANCIAL_DATA_MODE", null),
        ("FINANCIAL_DATA_GROUP_ID", null),
        ("CLASSIFICATION", null),
        ("CLASSIFICATION_ENFORCE_ACL", null),
        ("CLASSIFICATION_RESTRICTED_GROUP_ID", null),
        ("CLARIZEN_WEBHOOK_PORT", null));

    [Fact]
    public void TagMode_EmitsLoudNote()   // #4
    {
        using var scope = GoodEnv();
        scope.Set("FINANCIAL_DATA_MODE", "tag");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Contains(result.Warnings, w =>
            w.Contains("FINANCIAL_DATA_MODE=tag") && w.Contains("does NOT restrict"));
    }

    [Fact]
    public void FilterMode_Default_NoTagWarning()   // #4
    {
        using var scope = GoodEnv();   // FINANCIAL_DATA_MODE unset → filter
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("FINANCIAL_DATA_MODE=tag"));
    }

    [Fact]
    public void EnforceAclWithoutGroup_IsError()   // #6
    {
        using var scope = GoodEnv();
        scope.Set("CLASSIFICATION_ENFORCE_ACL", "true");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Contains(result.Errors, e => e.Contains("CLASSIFICATION_RESTRICTED_GROUP_ID"));
    }

    [Fact]
    public void EnforceAclWithoutClassification_IsWarning()   // #6
    {
        using var scope = GoodEnv();
        scope.Set("CLASSIFICATION_ENFORCE_ACL", "true");
        scope.Set("CLASSIFICATION_RESTRICTED_GROUP_ID", "grp");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Contains(result.Warnings, w =>
            w.Contains("CLASSIFICATION_ENFORCE_ACL") && w.Contains("CLASSIFICATION=true"));
    }
}

// ── #6 classification enforcement (unit) ─────────────────────────────────────

public class SensitivityEnforcementTests
{
    private static ExternalItem RestrictedItem() =>
        new ExternalItem
        {
            Id = "Task_1",
            Acl =
            {
                new AclEntry(AclEntryType.User, "entra-user", AclAccessType.Grant),
                new AclEntry(AclEntryType.Group, "entra-team", AclAccessType.Grant),
                new AclEntry(AclEntryType.User, "blocked", AclAccessType.Deny),
            },
        };

    [Fact]
    public void EnforceOff_IsAdvisory_AclUnchanged()
    {
        var item = RestrictedItem();
        var restricted = SensitivityClassifier.EnforceRestrictedAcl(
            item, SensitivityLabel.Restricted,
            TestConfig.Make(classification: true, classificationEnforceAcl: false));
        Assert.False(restricted);
        Assert.Equal(3, item.Acl.Count);   // untouched — the tag stays advisory
    }

    [Fact]
    public void EnforceOn_RestrictedItem_LockedToGroup_PreservingDenies()
    {
        var item = RestrictedItem();
        var restricted = SensitivityClassifier.EnforceRestrictedAcl(
            item, SensitivityLabel.Restricted,
            TestConfig.Make(
                classification: true, classificationEnforceAcl: true,
                classificationRestrictedGroupId: "grp-restricted"));
        Assert.True(restricted);
        var grant = Assert.Single(item.Acl, e => e.AccessType == AclAccessType.Grant);
        Assert.Equal(AclEntryType.Group, grant.Type);
        Assert.Equal("grp-restricted", grant.Value);
        var deny = Assert.Single(item.Acl, e => e.AccessType == AclAccessType.Deny);
        Assert.Equal("blocked", deny.Value);   // explicit denies preserved
    }

    [Fact]
    public void EnforceOn_BelowRestricted_NotTouched()
    {
        var item = RestrictedItem();
        var restricted = SensitivityClassifier.EnforceRestrictedAcl(
            item, SensitivityLabel.Confidential,
            TestConfig.Make(
                classification: true, classificationEnforceAcl: true,
                classificationRestrictedGroupId: "grp-restricted"));
        Assert.False(restricted);
        Assert.Equal(3, item.Acl.Count);
    }
}

// ── #8 item TTL ──────────────────────────────────────────────────────────────

public class ItemTtlTests
{
    private static ObjectConfig TaskConfig() => new()
    {
        ObjectName = "Task",
        DisplayName = "Task",
        SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
    };

    private static ClarizenRecord Record() =>
        new("Task", new JsonObject { ["id"] = "/Task/1", ["Name"] = "Alpha" });

    private static List<AclEntry> Acl() =>
        new() { new AclEntry(AclEntryType.User, "u", AclAccessType.Grant) };

    [Fact]
    public void TtlConfigured_StampsExpirationDateTime()
    {
        var before = DateTime.UtcNow.AddDays(30).AddMinutes(-1);
        var converter = new ItemConverter(TestConfig.Make(graphItemTtlDays: 30));
        var item = converter.Convert(Record(), TaskConfig(), Acl());

        Assert.NotNull(item.ExpirationDateTime);
        Assert.InRange(
            item.ExpirationDateTime!.Value, before, DateTime.UtcNow.AddDays(30).AddMinutes(1));
        Assert.NotNull(item.ToJson()["expirationDateTime"]);
    }

    [Fact]
    public void TtlUnset_NoExpiration()
    {
        var converter = new ItemConverter(TestConfig.Make());   // graphItemTtlDays defaults 0
        var item = converter.Convert(Record(), TaskConfig(), Acl());
        Assert.Null(item.ExpirationDateTime);
        Assert.Null(item.ToJson()["expirationDateTime"]);
    }
}

// ── #10 webhook anti-replay authenticator (deterministic clock) ──────────────

public class WebhookAuthenticatorTests
{
    private const string Secret = "unit-secret";
    private static readonly byte[] Body =
        System.Text.Encoding.UTF8.GetBytes("""{"events":[{"entityType":"Task","id":"1","operation":"update"}]}""");
    private static readonly DateTime Now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    private static WebhookAuthenticator Strict() =>
        new(Secret, requireTimestamp: true, TimeSpan.FromMinutes(5));

    private static string SignAt(DateTime when)
    {
        var ts = new DateTimeOffset(when).ToUnixTimeSeconds().ToString();
        return ts;
    }

    private static string Sig(string ts) =>
        new SignatureValidator(Secret).ComputeHex(ts, Body);

    [Fact]
    public void FreshTimestamp_ValidSignature_Ok()
    {
        var auth = Strict();
        var ts = SignAt(Now);
        Assert.Equal(WebhookAuthResult.Ok, auth.Verify(Body, Sig(ts), ts, Now));
    }

    [Fact]
    public void StaleTimestamp_Rejected()
    {
        var auth = Strict();
        var ts = SignAt(Now.AddMinutes(-10));   // outside the 5-min window
        Assert.Equal(WebhookAuthResult.StaleTimestamp, auth.Verify(Body, Sig(ts), ts, Now));
    }

    [Fact]
    public void FutureTimestamp_Rejected()
    {
        var auth = Strict();
        var ts = SignAt(Now.AddMinutes(10));
        Assert.Equal(WebhookAuthResult.StaleTimestamp, auth.Verify(Body, Sig(ts), ts, Now));
    }

    [Fact]
    public void ReplayWithinWindow_Rejected()
    {
        var auth = Strict();
        var ts = SignAt(Now);
        var sig = Sig(ts);
        Assert.Equal(WebhookAuthResult.Ok, auth.Verify(Body, sig, ts, Now));
        Assert.Equal(WebhookAuthResult.Replay, auth.Verify(Body, sig, ts, Now.AddSeconds(30)));
    }

    [Fact]
    public void TamperedTimestamp_BadSignature()
    {
        var auth = Strict();
        var ts = SignAt(Now);
        var sig = Sig(ts);
        // Attacker resends with a fresh timestamp but the old signature.
        var freshTs = SignAt(Now.AddSeconds(1));
        Assert.Equal(WebhookAuthResult.BadSignature, auth.Verify(Body, sig, freshTs, Now.AddSeconds(1)));
    }

    [Fact]
    public void MissingTimestamp_StrictRejected()
    {
        var auth = Strict();
        var bodyOnly = new SignatureValidator(Secret).ComputeHex(Body);
        Assert.Equal(WebhookAuthResult.MissingTimestamp, auth.Verify(Body, bodyOnly, null, Now));
    }

    [Fact]
    public void MissingTimestamp_MigrationAcceptsBodyOnly()
    {
        var auth = new WebhookAuthenticator(Secret, requireTimestamp: false, TimeSpan.FromMinutes(5));
        var bodyOnly = new SignatureValidator(Secret).ComputeHex(Body);
        Assert.Equal(WebhookAuthResult.Ok, auth.Verify(Body, bodyOnly, null, Now));
        // A wrong body-only signature is still rejected.
        Assert.Equal(WebhookAuthResult.BadSignature, auth.Verify(Body, "deadbeef", null, Now));
    }

    [Fact]
    public void MissingSignature_Rejected()
    {
        var auth = Strict();
        Assert.Equal(WebhookAuthResult.MissingSignature, auth.Verify(Body, null, SignAt(Now), Now));
    }

    [Fact]
    public void FromEnv_DefaultsToStrict_5Minutes()
    {
        using var env = new EnvScope(
            (WebhookAuthenticator.RequireTimestampEnvVar, null),
            (WebhookAuthenticator.ToleranceEnvVar, null),
            (WebhookAuthenticator.TimestampHeaderEnvVar, null));
        var auth = WebhookAuthenticator.FromEnv(Secret);
        Assert.True(auth.RequireTimestamp);
        Assert.Equal(WebhookAuthenticator.DefaultTimestampHeader, auth.TimestampHeader);
    }
}

// ── #11 decision ledger ──────────────────────────────────────────────────────

public class DecisionLedgerTests
{
    private const string Connector = "LedgerConn";

    [Fact]
    public void Chain_Links_AcrossRecords()
    {
        using var scope = new SyncStateScope();
        DecisionLedger.RecordExclusion(Connector, "Task_1", "no resolvable ACL principals");
        DecisionLedger.RecordRestriction(Connector, "Task_2", "financial (FINANCIAL_DATA_MODE=acl)");
        DecisionLedger.RecordRestriction(Connector, "Task_3", "classification=Restricted");

        var records = DecisionLedger.Read(Connector);
        Assert.Equal(3, records.Count);

        // seq increments from 1; genesis then chained prev_hash.
        Assert.Equal(1, records[0]["seq"]!.GetValue<long>());
        Assert.Equal(DecisionLedger.DecisionExclusion, records[0]["decision"]!.GetValue<string>());
        Assert.Equal("genesis", records[0]["prev_hash"]!.GetValue<string>());
        Assert.Equal(
            records[0]["hash"]!.GetValue<string>(), records[1]["prev_hash"]!.GetValue<string>());
        Assert.Equal(
            records[1]["hash"]!.GetValue<string>(), records[2]["prev_hash"]!.GetValue<string>());

        Assert.True(DecisionLedger.Verify(Connector));
    }

    [Fact]
    public void Verify_DetectsTamper()
    {
        using var scope = new SyncStateScope();
        DecisionLedger.RecordExclusion(Connector, "Task_1", "reason-a");
        DecisionLedger.RecordExclusion(Connector, "Task_2", "reason-b");
        Assert.True(DecisionLedger.Verify(Connector));

        // Tamper with a value on disk without recomputing the hash chain.
        var path = DecisionLedger.LedgerPath(Connector);
        var lines = File.ReadAllLines(path);
        var tampered = (JsonNode.Parse(lines[0]) as JsonObject)!;
        tampered["reason"] = "reason-a-EDITED";
        lines[0] = tampered.ToJsonString();
        File.WriteAllLines(path, lines);

        Assert.False(DecisionLedger.Verify(Connector));
    }

    [Fact]
    public void Verify_DetectsRemovedRecord()
    {
        using var scope = new SyncStateScope();
        DecisionLedger.RecordExclusion(Connector, "Task_1", "a");
        DecisionLedger.RecordExclusion(Connector, "Task_2", "b");
        DecisionLedger.RecordExclusion(Connector, "Task_3", "c");

        var path = DecisionLedger.LedgerPath(Connector);
        var lines = File.ReadAllLines(path).ToList();
        lines.RemoveAt(1);   // drop the middle record → seq gap + broken link
        File.WriteAllLines(path, lines);

        Assert.False(DecisionLedger.Verify(Connector));
    }

    [Fact]
    public void Disabled_RecordsNothing()
    {
        using var scope = new SyncStateScope();
        using var env = new EnvScope((DecisionLedger.EnvVar, "false"));
        DecisionLedger.RecordExclusion(Connector, "Task_1", "a");
        Assert.Empty(DecisionLedger.Read(Connector));
    }

    [Fact]
    public async Task Pipeline_RecordsExclusion_WhenNoAcl()
    {
        using var dir = new TempDir();
        using var scope = new SyncStateScope();
        var config = TestConfig.Make(connectorId: "ExclusionConn");
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task",
                    DisplayName = "Task",
                    AclMode = "ownerOnly",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["Owner"] = "Owner",
                    },
                },
            },
        };
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var clarizen = new ClarizenClient(config, new ApiBudget(1_000_000), handler);
        using var store = new IdentityStore("Exclusion", Path.Combine(dir.Path, "id.db"));
        var mapper = new PrincipalMapper(store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var pipeline = new IngestPipeline(
            config, schema, clarizen, new FakeGraphClient(config), resolver, new ItemConverter(config),
            ha: null, inventoryFactory: id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db")));

        var objectConfig = schema.FindObject("Task")!;
        // Owner /User/99 is not in the store and there is no fallback → no ACL.
        var record = new ClarizenRecord("Task", new JsonObject
        {
            ["id"] = "/Task/5",
            ["Name"] = "Orphan",
            ["Owner"] = new JsonObject { ["id"] = "/User/99" },
        });
        var (ok, _) = await pipeline.IngestSingleAsync(record, objectConfig);
        Assert.False(ok);

        var entry = Assert.Single(DecisionLedger.Read("ExclusionConn"));
        Assert.Equal("Task_5", entry["item_id"]!.GetValue<string>());
        Assert.Equal(DecisionLedger.DecisionExclusion, entry["decision"]!.GetValue<string>());
        Assert.True(DecisionLedger.Verify("ExclusionConn"));
    }
}

// ── #12 bounded debouncer ────────────────────────────────────────────────────

public class EventDebouncerBoundTests
{
    private static readonly DateTime T0 = new(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Cap_Holds_DropsOldest()
    {
        var debouncer = new EventDebouncer(TimeSpan.FromMinutes(5), maxPending: 3);

        for (var i = 0; i < 10; i++)
            debouncer.Offer(new WebhookEvent("Task", i.ToString(), ChangeKind.Upsert), T0.AddSeconds(i));

        Assert.Equal(3, debouncer.PendingCount);        // never exceeds the cap
        Assert.Equal(3, debouncer.MaxPending);
        Assert.Equal(7, debouncer.Dropped);             // 10 distinct, 3 kept

        // The three NEWEST entities survive (oldest dropped).
        var drained = debouncer.DrainAll();
        var kept = drained.Select(e => e.LocalId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "7", "8", "9" }, kept);
    }

    [Fact]
    public void ReOffer_DoesNotGrow_NorDrop()
    {
        var debouncer = new EventDebouncer(TimeSpan.FromMinutes(5), maxPending: 2);
        debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Upsert), T0);
        debouncer.Offer(new WebhookEvent("Task", "2", ChangeKind.Upsert), T0);
        // Re-offering existing entities must not evict anything.
        debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Delete), T0.AddSeconds(1));
        debouncer.Offer(new WebhookEvent("Task", "2", ChangeKind.Delete), T0.AddSeconds(1));

        Assert.Equal(2, debouncer.PendingCount);
        Assert.Equal(0, debouncer.Dropped);
    }
}
