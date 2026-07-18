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

    // Helper: parse only the well-formed JSONL entries (a torn final line is
    // skipped) and return them ordered as they appear in the file.
    private static List<JsonObject> ParseIntactEntries(string path)
    {
        var entries = new List<JsonObject>();
        foreach (var raw in File.ReadAllLines(path))
        {
            if (raw.Trim().Length == 0)
                continue;
            try
            {
                entries.Add(JsonNode.Parse(raw)!.AsObject());
            }
            catch
            {
                // torn/garbled line — not an intact entry
            }
        }
        return entries;
    }

    [Fact]
    public void Reopen_AfterTornFinalLine_ContinuesChainInsteadOfResettingToGenesis()
    {
        using var dir = new TempDir();
        var ledger = new DecisionLedger(Connector, dir.Path);
        ledger.Record("A1", "Contact", DecisionLedger.Exclusion, "r1");
        ledger.Record("B2", "Account", DecisionLedger.AclRestriction, "r2");

        // Simulate a process killed mid-append: a partial, unterminated final
        // line is left on the file (no trailing newline). The last INTACT entry
        // is seq 2.
        var intactBefore = ParseIntactEntries(ledger.Path);
        Assert.Equal(2, intactBefore.Count);
        var seq2Hash = intactBefore[1]["hash"]!.GetValue<string>();
        File.AppendAllText(ledger.Path, "{\"seq\":3,\"timestamp\":\"2026-07-18T00:00:00");

        // Restart: a fresh instance must CONTINUE the chain from the last intact
        // entry (seq 2), not silently reset to genesis (which would restart the
        // seq at 1 and orphan the audit history), and the new entry must land on
        // its own parseable line (not glued onto the torn partial).
        var reopened = new DecisionLedger(Connector, dir.Path);
        reopened.Record("C3", "Lead", DecisionLedger.Exclusion, "r3-after-crash");

        var intactAfter = ParseIntactEntries(reopened.Path);
        var appended = intactAfter[^1];
        Assert.Equal("C3", appended["item_id"]!.GetValue<string>());
        Assert.Equal(3, appended["seq"]!.GetValue<long>());
        Assert.Equal(seq2Hash, appended["prev_hash"]!.GetValue<string>());
    }

    [Fact]
    public void Reopen_AfterUnterminatedButCompleteFinalLine_ParsesAndContinues()
    {
        using var dir = new TempDir();
        var ledger = new DecisionLedger(Connector, dir.Path);
        ledger.Record("A1", "Contact", DecisionLedger.Exclusion, "r1");

        // A COMPLETE final entry that just lost its trailing newline (buffer
        // flushed the JSON but not the newline). It is valid JSON and must be
        // adopted as the chain head; the reopen also seals the boundary so the
        // next append is a clean line.
        var line1 = File.ReadAllLines(ledger.Path)[0];
        var obj1 = JsonNode.Parse(line1)!.AsObject();
        var hash1 = obj1["hash"]!.GetValue<string>();
        File.WriteAllText(ledger.Path, line1 + "\n" + line1.Replace("\"seq\":1", "\"seq\":2")
            .Replace("GENESIS", hash1));
        // Recompute a proper seq-2 line so the file is a valid 2-entry chain with
        // no trailing newline.
        var seq2 = new JsonObject
        {
            ["seq"] = 2,
            ["timestamp"] = "2026-07-18T00:00:00.0000000+00:00",
            ["item_id"] = "B2",
            ["object_type"] = "Account",
            ["decision"] = DecisionLedger.AclRestriction,
            ["reason"] = "r2",
            ["prev_hash"] = hash1,
        };
        var hash2 = DecisionLedger.ComputeHash(
            2, seq2["timestamp"]!.GetValue<string>(), "B2", "Account",
            DecisionLedger.AclRestriction, "r2", hash1);
        seq2["hash"] = hash2;
        File.WriteAllText(ledger.Path, line1 + "\n" + seq2.ToJsonString());  // no trailing newline

        var reopened = new DecisionLedger(Connector, dir.Path);
        reopened.Record("C3", "Lead", DecisionLedger.Exclusion, "r3");

        var entries = ParseIntactEntries(reopened.Path);
        Assert.Equal(3, entries.Count);  // no data lost, no glued line
        Assert.Equal(3, entries[2]["seq"]!.GetValue<long>());
        Assert.Equal(hash2, entries[2]["prev_hash"]!.GetValue<string>());
        Assert.True(DecisionLedger.Verify(reopened.Path).Ok);  // full chain still valid
    }

    [Fact]
    public void Record_ConcurrentAppends_ChainStaysValidAndContiguous()
    {
        using var dir = new TempDir();
        var ledger = new DecisionLedger(Connector, dir.Path);

        const int threads = 16;
        const int perThread = 400;
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                ledger.Record($"item-{t}-{i}", "Contact", DecisionLedger.Exclusion, $"reason {t}/{i}");
        });

        // No torn/interleaved lines: every line is valid JSON, the hash chain
        // verifies, and the sequence numbers are exactly 1..N with no gaps or
        // duplicates.
        var verify = DecisionLedger.Verify(ledger.Path);
        Assert.True(verify.Ok, verify.Error);
        Assert.Equal(threads * perThread, verify.Entries);

        var seqs = File.ReadAllLines(ledger.Path).Where(l => l.Trim().Length > 0)
            .Select(l => JsonNode.Parse(l)!.AsObject()["seq"]!.GetValue<long>())
            .OrderBy(s => s).ToList();
        Assert.Equal(Enumerable.Range(1, threads * perThread).Select(i => (long)i), seqs);
    }
}
