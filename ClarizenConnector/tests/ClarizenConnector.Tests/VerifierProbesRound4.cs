// VerifierProbesRound4.cs
// -----------------------
// ADVERSARIAL VERIFIER probes for the bank-grade hardening round (fixes
// #2/#4/#10). These exist to DISPROVE the implementer's "safe default" claims by
// exercising the real code paths end-to-end, not the units in isolation:
//
//   V1 (#2) With DEADLETTER_PAYLOAD_MODE UNSET, a real AppConfig.Load() +
//           dead-letter write must leave no raw value on disk (default redacts).
//   V2 (#2) A typo mode value must THROW at AppConfig.Load (never silently mean
//           "full").
//   V3 (#4) With FINANCIAL_DATA_MODE UNSET, the finished item produced by the
//           real ItemConverter (default filter) must NOT carry the financial
//           value — neither as a property nor in the serialized Graph payload.
//   V4 (#10) A stale signed request that is REPLAYED after the freshness window
//            expires (and its dedupe entry has been pruned) must STILL be
//            rejected — cache pruning must not reopen a replay window.
//   V5 (#10) A captured (ts, body, signature) reused verbatim inside the window
//            is rejected as a replay; the same signature over a different body
//            never validates.

using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;
using ClarizenConnector.Webhook;
using Xunit;

namespace ClarizenConnector.Tests;

public class VerifierRound4SafeDefaultProbes
{
    private const string Connector = "ClarizenAdaptiveWork";

    private static EnvScope RequiredEnv(params (string, string?)[] extra)
    {
        var baseVars = new (string, string?)[]
        {
            ("CONNECTOR_ID", "ClarizenAdaptiveWork"),
            ("CLARIZEN_USERNAME", "svc@example.com"),
            ("SECRET_CLARIZEN_PASSWORD", "pw"),
            ("AAD_APP_TENANT_ID", "t"),
            ("AAD_APP_CLIENT_ID", "c"),
            ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
            ("USE_KEY_VAULT", null),
            ("DEADLETTER_PAYLOAD_MODE", null),
            ("FINANCIAL_DATA_MODE", null),
            ("FINANCIAL_DATA_GROUP_ID", null),
        };
        return new EnvScope(baseVars.Concat(extra).ToArray());
    }

    // ── V1 (#2): unset dead-letter mode redacts through the real load path ──────
    [Fact]
    public void V1_UnsetDeadLetterMode_RedactsOnDisk()
    {
        using var state = new SyncStateScope();
        using var env = RequiredEnv();

        // Sanity: the shipped default resolves to redacted through config load.
        var config = AppConfig.Load();   // must not throw with mode unset
        Assert.True(DeadLetterRedactor.RedactionEnabled);

        var item = new ExternalItem { Id = "Project_42" };
        item.Properties["Title"] = "TOPSECRET-sentinel-value";
        item.Properties["PlannedCost"] = 4242424.24;
        item.Content = "cost is 4242424.24";
        item.Acl.Add(new AclEntry(AclEntryType.User, "leak-user", AclAccessType.Grant));

        SyncState.AppendFailedRecords(
            Connector,
            new List<(string, string)> { (item.Id, "HTTP 400") },
            "Project",
            new Dictionary<string, JsonNode?> { [item.Id] = item.ToJson() },
            responseBodies: new Dictionary<string, JsonNode?>
            {
                [item.Id] = new JsonObject { ["echo"] = "TOPSECRET-sentinel-value 4242424.24" },
            });

        var raw = File.ReadAllText(SyncState.FailedRecordsPath(Connector));
        Assert.DoesNotContain("TOPSECRET-sentinel-value", raw);
        Assert.DoesNotContain("4242424.24", raw);
        Assert.DoesNotContain("leak-user", raw);
        Assert.Contains("sha256:", raw);   // proof it went through the redactor
    }

    // ── V2 (#2): typo mode throws, never silently "full" ───────────────────────
    // Note: config validation does NOT trim (so even "full " with a trailing
    // space fails fast — stricter than the redactor's own trim, which is safe).
    [Theory]
    [InlineData("redcated")]  // classic typo
    [InlineData("none")]
    [InlineData("plain")]
    [InlineData("full ")]     // stray space: fail-fast, never silently "full"
    public void V2_UnknownDeadLetterMode_Throws(string mode)
    {
        using var env = RequiredEnv(("DEADLETTER_PAYLOAD_MODE", mode));
        var exc = Assert.Throws<ArgumentException>(AppConfig.Load);
        Assert.Contains("DEADLETTER_PAYLOAD_MODE", exc.Message);
    }

    // ── V3 (#4): unset financial mode → converter strips the value end-to-end ──
    [Fact]
    public void V3_UnsetFinancialMode_FinishedItemHasNoFinancialValue()
    {
        using var env = RequiredEnv();
        var config = AppConfig.Load();
        Assert.Equal("filter", config.FinancialDataMode);

        var objectConfig = new ObjectConfig
        {
            ObjectName = "Project",
            DisplayName = "Project",
            SelectedFields = new Dictionary<string, string>
            {
                ["Name"] = "Title",
                ["PlannedCost"] = "PlannedCost",         // property-mapped financial
                ["Description"] = "_cz_Description",      // content-mapped (non-financial)
            },
            FinancialFields = new List<string> { "PlannedCost" },
        };

        var record = new ClarizenRecord("Project", new JsonObject
        {
            ["id"] = "/Project/7",
            ["Name"] = "Apollo",
            ["PlannedCost"] = 987654.21,
            ["Description"] = "ordinary text",
        });

        var converter = new ItemConverter(config, appBaseUrl: "https://app.example");
        var acl = new List<AclEntry> { new(AclEntryType.User, "u", AclAccessType.Grant) };
        var item = converter.Convert(record, objectConfig, acl);

        // Classified but the financial value is gone from the item AND the wire.
        Assert.Equal("financial", item.Properties[FinancialFieldClassifier.ClassificationProperty]);
        Assert.False(item.Properties.ContainsKey("PlannedCost"));
        var json = item.ToJson().ToJsonString();
        Assert.DoesNotContain("987654.21", json);
        Assert.DoesNotContain("987654", json);
    }

    // ── V3b (#4): content-mapped financial field is redacted from content body ──
    [Fact]
    public void V3b_UnsetFinancialMode_ContentMappedFinancial_Redacted()
    {
        using var env = RequiredEnv();
        var config = AppConfig.Load();

        var objectConfig = new ObjectConfig
        {
            ObjectName = "Project",
            DisplayName = "Project",
            SelectedFields = new Dictionary<string, string>
            {
                ["Name"] = "Title",
                ["SecretCost"] = "_cz_SecretCost",   // financial routed to CONTENT
            },
            FinancialFields = new List<string> { "SecretCost" },
        };
        var record = new ClarizenRecord("Project", new JsonObject
        {
            ["id"] = "/Project/8",
            ["Name"] = "Zeus",
            ["SecretCost"] = "budget-sentinel-55501",
        });

        var converter = new ItemConverter(config, appBaseUrl: "https://app.example");
        var item = converter.Convert(
            record, objectConfig, new List<AclEntry> { new(AclEntryType.User, "u", AclAccessType.Grant) });

        Assert.Equal("financial", item.Properties[FinancialFieldClassifier.ClassificationProperty]);
        Assert.DoesNotContain("budget-sentinel-55501", item.Content);
        Assert.DoesNotContain("budget-sentinel-55501", item.ToJson().ToJsonString());
    }
}

public class VerifierRound4WebhookReplayProbes
{
    private const string Secret = "probe-secret-xyz";
    private static readonly byte[] Body =
        Encoding.UTF8.GetBytes("""{"events":[{"entityType":"Task","id":"9","operation":"update"}]}""");
    private static readonly DateTime Now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    private static string Ts(DateTime when) =>
        new DateTimeOffset(when).ToUnixTimeSeconds().ToString();

    private static string Sig(string ts) => new SignatureValidator(Secret).ComputeHex(ts, Body);

    // ── V4 (#10): pruning the dedupe entry must NOT reopen the replay window ────
    [Fact]
    public void V4_ReplayAfterWindowExpiry_StillRejected_AsStale()
    {
        var tolerance = TimeSpan.FromMinutes(5);
        var auth = new WebhookAuthenticator(Secret, requireTimestamp: true, tolerance);
        var ts = Ts(Now);
        var sig = Sig(ts);

        // Accepted once, fresh.
        Assert.Equal(WebhookAuthResult.Ok, auth.Verify(Body, sig, ts, Now));

        // Replay LONG after the freshness window (the seen-cache entry expired at
        // ts+tolerance and would be pruned). The timestamp is now far too old, so
        // the freshness guard must reject it — pruning must never make an old
        // request acceptable again.
        var later = Now.AddMinutes(20);
        Assert.Equal(WebhookAuthResult.StaleTimestamp, auth.Verify(Body, sig, ts, later));
    }

    // ── V5 (#10): verbatim in-window replay rejected; sig is body+ts bound ──────
    [Fact]
    public void V5_InWindowVerbatimReplay_Rejected_AndSigIsBodyBound()
    {
        var auth = new WebhookAuthenticator(Secret, requireTimestamp: true, TimeSpan.FromMinutes(5));
        var ts = Ts(Now);
        var sig = Sig(ts);

        Assert.Equal(WebhookAuthResult.Ok, auth.Verify(Body, sig, ts, Now));
        // Same signature, same ts, a few seconds later — a captured replay.
        Assert.Equal(WebhookAuthResult.Replay, auth.Verify(Body, sig, ts, Now.AddSeconds(45)));

        // The signature is bound to THIS body: it never validates over a different
        // body (fresh authenticator so replay-cache is not the thing rejecting it).
        var auth2 = new WebhookAuthenticator(Secret, requireTimestamp: true, TimeSpan.FromMinutes(5));
        var tamperedBody = Encoding.UTF8.GetBytes(
            """{"events":[{"entityType":"Task","id":"9","operation":"delete"}]}""");
        Assert.Equal(WebhookAuthResult.BadSignature, auth2.Verify(tamperedBody, sig, ts, Now));
    }

    // ── V5b (#10): a signer without the secret cannot forge a fresh request ─────
    [Fact]
    public void V5b_WrongSecretSignature_Rejected()
    {
        var auth = new WebhookAuthenticator(Secret, requireTimestamp: true, TimeSpan.FromMinutes(5));
        var ts = Ts(Now);
        var forged = new SignatureValidator("attacker-secret").ComputeHex(ts, Body);
        Assert.Equal(WebhookAuthResult.BadSignature, auth.Verify(Body, forged, ts, Now));
    }
}
