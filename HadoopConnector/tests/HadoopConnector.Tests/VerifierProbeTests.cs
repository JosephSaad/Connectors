// ADVERSARIAL VERIFIER probes (independent of the implementer's tests).
// These use fresh PII strings the implementer never referenced, and push the
// decision-ledger tamper detection to its edge, to independently confirm the
// safe-default flip (#2) and characterise the ledger's tamper-evidence (#11).

using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class VerifierProbeTests : IDisposable
{
    private const string Connector = "VerifierProbe";

    public VerifierProbeTests() => DeadLetterRedaction.ResetForTests();

    public void Dispose() => DeadLetterRedaction.ResetForTests();

    // #2 — with the env var completely UNSET, the shipped default must redact:
    // no record VALUE may reach the dead-letter file. Fresh values, not the
    // implementer's.
    [Fact]
    public void DeadLetter_DefaultUnset_RedactsBeforeDisk()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, null));
        using var scope = new SyncStateScope();
        Assert.True(DeadLetterRedaction.RedactionEnabled);

        var body = new JsonObject
        {
            ["id"] = "PROBE-9",
            ["properties"] = new JsonObject
            {
                ["FullName"] = "Bartholomew Q. Nightingale",
                ["Ssn"] = "078-05-1120",
                ["Salary"] = 987654,
            },
            ["content"] = new JsonObject { ["value"] = "SSN 078-05-1120 salary 987654", ["type"] = "text" },
            ["acl"] = new JsonArray(
                new JsonObject { ["type"] = "user", ["value"] = "secret-principal-77", ["accessType"] = "grant" }),
        };
        SyncState.AppendFailedRecords(
            Connector, new[] { ("PROBE-9", "HTTP 400") }, "Contact",
            new Dictionary<string, JsonNode?> { ["PROBE-9"] = body });

        var raw = File.ReadAllText(SyncState.FailedRecordsPath(Connector));
        Assert.DoesNotContain("Bartholomew", raw);
        Assert.DoesNotContain("078-05-1120", raw);
        Assert.DoesNotContain("987654", raw);
        Assert.DoesNotContain("secret-principal-77", raw);

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.True(entry["request_body"]!["redacted"]!.GetValue<bool>());
    }

    // #2 — a typo must fail fast, not silently pick a mode.
    [Fact]
    public void DeadLetter_TypoMode_Throws_AtReadAndConfigLoad()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redactd"));
        Assert.Throws<ArgumentException>(() => DeadLetterRedaction.RedactionEnabled);

        using var full = new EnvScope(
            ("CONNECTOR_ID", "BdhHadoopMart"),
            ("AAD_APP_TENANT_ID", "t"),
            ("AAD_APP_CLIENT_ID", "c"),
            ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
            ("HDFS_MODE", "webhdfs"),
            ("HDFS_NAMENODE_URL", "http://nn.example:9870/webhdfs/v1"));
        Assert.Throws<ArgumentException>(() => AppConfig.Load());
    }

    // #11 — CHARACTERISATION: an append-only hash chain with no external anchor
    // cannot detect truncation of the TAIL. This documents the residual limit of
    // the "detects deletions" claim (interior deletes/edits/reorders ARE caught).
    [Fact]
    public void DecisionLedger_TailTruncation_IsNotDetected()
    {
        using var dir = new TempDir();
        var ledger = new DecisionLedger(Connector, dir.Path);
        ledger.Record("A1", "Contact", DecisionLedger.AclRestriction, "narrowed to grp");
        ledger.Record("B2", "Account", DecisionLedger.AclRestriction, "narrowed to grp");
        ledger.Record("C3", "Lead", DecisionLedger.Exclusion, "no ACL");
        Assert.True(DecisionLedger.Verify(ledger.Path).Ok);

        // Drop the last entry to hide the most recent decision.
        var lines = File.ReadAllLines(ledger.Path).Where(l => l.Trim().Length > 0).ToList();
        lines.RemoveAt(lines.Count - 1);
        File.WriteAllLines(ledger.Path, lines);

        // The remaining prefix is self-consistent, so Verify still passes: tail
        // truncation is undetectable without an external head anchor.
        Assert.True(DecisionLedger.Verify(ledger.Path).Ok);
    }
}
