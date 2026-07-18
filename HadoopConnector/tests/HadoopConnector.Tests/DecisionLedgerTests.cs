// Immutable decision audit (#11, Infrastructure/DecisionLedger.cs): an
// append-only, SHA-256 hash-chained ledger of the low-volume access decisions
// (EXCLUSION + ACL_RESTRICTION). The chain links each entry to the previous one;
// Verify() re-derives every hash and detects any edit, deletion or reorder.

using System.Text.Json.Nodes;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class DecisionLedgerTests
{
    private const string Connector = "LedgerConn";

    [Fact]
    public void Record_LinksEntries_GenesisThenChained()
    {
        using var dir = new TempDir();
        var ledger = new DecisionLedger(Connector, dir.Path);

        ledger.Record("A1", "Contact", DecisionLedger.Exclusion, "no ACL principals");
        ledger.Record("B2", "Account", DecisionLedger.AclRestriction, "narrowed to group g");
        ledger.Record("C3", "Lead", DecisionLedger.Exclusion, "no ACL principals");

        var lines = File.ReadAllLines(ledger.Path).Where(l => l.Trim().Length > 0)
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(3, lines.Count);

        Assert.Equal(1, lines[0]["seq"]!.GetValue<long>());
        Assert.Equal("GENESIS", lines[0]["prev_hash"]!.GetValue<string>());
        Assert.Equal(DecisionLedger.Exclusion, lines[0]["decision"]!.GetValue<string>());

        // Each entry's prev_hash is the previous entry's hash; seq increments.
        Assert.Equal(2, lines[1]["seq"]!.GetValue<long>());
        Assert.Equal(lines[0]["hash"]!.GetValue<string>(), lines[1]["prev_hash"]!.GetValue<string>());
        Assert.Equal(DecisionLedger.AclRestriction, lines[1]["decision"]!.GetValue<string>());

        Assert.Equal(3, lines[2]["seq"]!.GetValue<long>());
        Assert.Equal(lines[1]["hash"]!.GetValue<string>(), lines[2]["prev_hash"]!.GetValue<string>());

        var verify = DecisionLedger.Verify(ledger.Path);
        Assert.True(verify.Ok);
        Assert.Equal(3, verify.Entries);
    }

    [Fact]
    public void Verify_MissingFile_IsValidEmptyChain()
    {
        using var dir = new TempDir();
        var result = DecisionLedger.Verify(Path.Combine(dir.Path, "decisions_none.jsonl"));
        Assert.True(result.Ok);
        Assert.Equal(0, result.Entries);
    }

    [Fact]
    public void Verify_DetectsEditedField()
    {
        using var dir = new TempDir();
        var ledger = new DecisionLedger(Connector, dir.Path);
        ledger.Record("A1", "Contact", DecisionLedger.Exclusion, "original reason");
        ledger.Record("B2", "Account", DecisionLedger.AclRestriction, "narrowed");
        Assert.True(DecisionLedger.Verify(ledger.Path).Ok);

        // Tamper: change the recorded reason on line 1 (hash no longer matches).
        var lines = File.ReadAllLines(ledger.Path).ToList();
        var first = JsonNode.Parse(lines[0])!.AsObject();
        first["reason"] = "reason was altered after the fact";
        lines[0] = first.ToJsonString();
        File.WriteAllLines(ledger.Path, lines);

        var result = DecisionLedger.Verify(ledger.Path);
        Assert.False(result.Ok);
        Assert.Contains("tamper", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_DetectsDeletedEntry()
    {
        using var dir = new TempDir();
        var ledger = new DecisionLedger(Connector, dir.Path);
        ledger.Record("A1", "Contact", DecisionLedger.Exclusion, "r1");
        ledger.Record("B2", "Account", DecisionLedger.Exclusion, "r2");
        ledger.Record("C3", "Lead", DecisionLedger.Exclusion, "r3");

        // Remove the middle entry — the chain link (and seq) breaks.
        var lines = File.ReadAllLines(ledger.Path).Where(l => l.Trim().Length > 0).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(ledger.Path, lines);

        Assert.False(DecisionLedger.Verify(ledger.Path).Ok);
    }

    [Fact]
    public void Record_ContinuesChain_AcrossInstances()
    {
        using var dir = new TempDir();
        new DecisionLedger(Connector, dir.Path).Record("A1", "Contact", DecisionLedger.Exclusion, "r1");

        // A fresh instance (process restart) picks up the chain head and appends.
        var reopened = new DecisionLedger(Connector, dir.Path);
        reopened.Record("B2", "Account", DecisionLedger.AclRestriction, "r2");

        var lines = File.ReadAllLines(reopened.Path).Where(l => l.Trim().Length > 0)
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(2, lines[1]["seq"]!.GetValue<long>());
        Assert.Equal(lines[0]["hash"]!.GetValue<string>(), lines[1]["prev_hash"]!.GetValue<string>());
        Assert.True(DecisionLedger.Verify(reopened.Path).Ok);
    }
}
