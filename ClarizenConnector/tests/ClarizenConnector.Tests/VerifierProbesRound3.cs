// VerifierProbesRound3.cs
// -----------------------
// ADVERSARIAL VERIFIER probes for the round-3 stress work. These are NOT part of
// the stress agent's suite; they exist to DISPROVE claimed invariants the
// round-3 harness left partially covered. If any assertion here fails, the
// round-3 "all green, no gaps" claim is wrong.
//
//   P1. Round-3 B1 proved financial FILTER mode redacts a content-mapped (_cz_)
//       field from the content body. It never proved ACL mode. In acl mode the
//       value STAYS in the content body by design — so the ONLY thing protecting
//       it is the ACL rewrite to the finance group. If the converter fails to
//       propagate the content-financial signal into acl enforcement, a
//       content-mapped financial value would ship readable to the item's
//       ordinary grantees. Probe the converter end-to-end, under concurrency.
//   P2. The dead-letter redactor's content branch only recognises the
//       {value,type} object shape. Probe a request body whose `content` is a
//       BARE STRING carrying a sentinel: it must not survive verbatim.

using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;
using Xunit;

namespace ClarizenConnector.Tests;

public class VerifierAclContentFinancialProbe
{
    private static ObjectConfig ContentMappedFinancialObject() => new()
    {
        ObjectName = "Project",
        DisplayName = "Project",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            // Financial field routed to the CONTENT body, NOT a Graph property.
            ["SecretCost"] = "_cz_SecretCost",
        },
        FinancialFields = new List<string> { "SecretCost" },
    };

    // acl mode + ONLY a content-mapped financial field populated (no financial
    // PROPERTY). The converter must classify the item financial AND rewrite its
    // grants to the finance group (denies preserved), because the sensitive
    // value remains in the content body in acl mode.
    [Fact]
    public void AclMode_ContentMappedFinancial_RestrictsToFinanceGroup_ValueStaysInContentButAclProtected()
    {
        var converter = new ItemConverter(
            TestConfig.Make(financialMode: "acl", financialGroupId: "fin-group"));
        var objectConfig = ContentMappedFinancialObject();

        var record = new ClarizenRecord("Project", new JsonObject
        {
            ["id"] = "/Project/7",
            ["Name"] = "Project 7",
            ["SecretCost"] = "FIN-SENTINEL-7 confidential rate",
        });

        var originalAcl = new List<AclEntry>
        {
            new(AclEntryType.User, "user-alice", AclAccessType.Grant),
            new(AclEntryType.User, "user-bob", AclAccessType.Grant),
            new(AclEntryType.User, "user-mallory", AclAccessType.Deny),
        };

        var item = converter.Convert(record, objectConfig, originalAcl);

        // Classified financial even though no financial PROPERTY exists.
        Assert.Equal("financial", item.Properties[FinancialFieldClassifier.ClassificationProperty]);
        Assert.Equal(true, item.Properties[FinancialFieldClassifier.ContainsFinancialProperty]);

        // acl mode does NOT strip content — the value is still indexed...
        Assert.Contains("FIN-SENTINEL-7", item.Content);

        // ...so the ACL MUST have been rewritten to the finance group only.
        var grants = item.Acl.Where(e => e.AccessType == AclAccessType.Grant).ToList();
        Assert.Single(grants);
        Assert.Equal(AclEntryType.Group, grants[0].Type);
        Assert.Equal("fin-group", grants[0].Value);

        // Original user grants are GONE (would otherwise read the financial content).
        Assert.DoesNotContain(item.Acl, e => e.Value == "user-alice");
        Assert.DoesNotContain(item.Acl, e => e.Value == "user-bob");

        // Deny is preserved (defense in depth).
        Assert.Contains(item.Acl, e => e.Value == "user-mallory" && e.AccessType == AclAccessType.Deny);
    }

    // Same invariant under a concurrent flood: every item independently gets the
    // finance-group rewrite; no cross-talk / torn ACL leaves an ordinary grantee
    // on a financial item.
    [Fact]
    public void AclMode_ContentMappedFinancial_ConcurrentFlood_NeverLeaksOrdinaryGrant()
    {
        var converter = new ItemConverter(
            TestConfig.Make(financialMode: "acl", financialGroupId: "fin-group"));
        var objectConfig = ContentMappedFinancialObject();

        const int n = 20_000;
        long notClassified = 0, ordinaryGrantSurvived = 0, denyLost = 0, contentMissing = 0;

        Parallel.For(0, n, i =>
        {
            var record = new ClarizenRecord("Project", new JsonObject
            {
                ["id"] = $"/Project/{i}",
                ["Name"] = $"Project {i}",
                ["SecretCost"] = $"FIN-SENTINEL-{i} rate",
            });
            var acl = new List<AclEntry>
            {
                new(AclEntryType.User, $"user-{i}", AclAccessType.Grant),
                new(AclEntryType.User, $"deny-{i}", AclAccessType.Deny),
            };
            var item = converter.Convert(record, objectConfig, acl);

            if (item.Properties.GetValueOrDefault(FinancialFieldClassifier.ClassificationProperty) is not "financial")
                Interlocked.Increment(ref notClassified);
            // No ordinary USER grant may survive on a financial item.
            if (item.Acl.Any(e => e.Type == AclEntryType.User && e.AccessType == AclAccessType.Grant))
                Interlocked.Increment(ref ordinaryGrantSurvived);
            if (!item.Acl.Any(e => e.Value == $"deny-{i}" && e.AccessType == AclAccessType.Deny))
                Interlocked.Increment(ref denyLost);
            if (!item.Content.Contains($"FIN-SENTINEL-{i}", StringComparison.Ordinal))
                Interlocked.Increment(ref contentMissing);   // acl mode keeps the value in content
        });

        Assert.Equal(0, Interlocked.Read(ref notClassified));
        Assert.Equal(0, Interlocked.Read(ref ordinaryGrantSurvived));
        Assert.Equal(0, Interlocked.Read(ref denyLost));
        Assert.Equal(0, Interlocked.Read(ref contentMissing));
    }
}

public class VerifierRedactorBareContentProbe
{
    // The redactor's content handling only recognises {value,type}. A request
    // body whose `content` is a BARE STRING (an unexpected shape) must NOT pass
    // the sentinel through verbatim — the redactor only re-emits fields it
    // explicitly constructs, so nothing unrecognised should survive.
    [Fact]
    public void RedactRequestBody_BareStringContent_DoesNotLeakSentinel()
    {
        var payload = new JsonObject
        {
            ["id"] = "Project_1",
            ["properties"] = new JsonObject { ["BillingRate"] = "FIN-SECRET-1" },
            ["content"] = "raw FIN-SECRET-1 body text",   // bare string, not {value,type}
            ["acl"] = new JsonArray(
                new JsonObject { ["type"] = "user", ["value"] = "u1", ["accessType"] = "grant" }),
        };

        var redacted = DeadLetterRedactor.RedactRequestBody(payload);
        var text = redacted!.ToJsonString();

        Assert.DoesNotContain("FIN-SECRET-1", text);
        // id + hashed property name kept; acl collapsed to a count.
        var obj = Assert.IsType<JsonObject>(redacted);
        Assert.Equal("Project_1", obj["id"]!.GetValue<string>());
        Assert.StartsWith("sha256:", (obj["properties"] as JsonObject)!["BillingRate"]!.GetValue<string>());
        Assert.Equal(1, obj["acl_count"]!.GetValue<int>());
    }
}
