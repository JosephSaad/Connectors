// DecisionLedgerInteriorTearTests.cs
// ---------------------------------
// Round 4 of the decision-ledger tear work. Rounds 2 and 3 fixed the two tear
// shapes that live at the very END of the file (the newline-boundary tear and
// the blank-line tear). This file pins the two defects that survived them:
//
//   BLOCKER 1 — INTERIOR-NEWLINE TEAR. ResumeTail only rescued a complete,
//   unterminated record when it was the EXACT last bytes of the file
//   ('needsTerminator && committedBytes == totalBytes'). Lose ONE interior '\n'
//   and two flushed, acknowledged records glue into a single line that no
//   longer parses; every record from that point to EOF then fell outside the
//   committed mark and was TRUNCATED AWAY on the next resume. The surviving
//   prefix plus the new run's entries still form an internally consistent
//   chain, so Verify() reported CLEAN over a ledger that had silently lost
//   records — the one failure mode a tamper-evident log must never have. Its
//   blast radius is bigger than the two shapes already fixed: those cost the
//   last record, this one costs every record after the damaged boundary.
//
//   BLOCKER 2 — NULL-MEMBER "RECORD". ResumeTail treated any line that
//   deserialized to a non-null DecisionLedgerEntry as a complete record. A JSON
//   object that carries none of the fields ("{}", or one missing members)
//   deserializes just fine, leaving the non-nullable string members NULL. That
//   null Hash became the resume anchor, and the next Append dereferenced it in
//   ComputeHash -> AppendField ('value.Length') as an unhandled
//   NullReferenceException — on the ContentGate quarantine path
//   (Graph/Ingest.cs, Ledger.Append(..., DecisionQuarantine, ...)), i.e. a
//   malformed byte in an audit file could take down the malware /
//   prompt-injection quarantine of a whole crawl.
//
// The invariant both fixes share: RESUME NEVER DISCARDS BYTES THAT BELONG TO A
// COMPLETE, PARSEABLE RECORD, and a line that is not a record never becomes the
// chain anchor.

using SeismicConnector.Infrastructure;

namespace SeismicConnector.Tests;

public class DecisionLedgerInteriorTearTests
{
    private static string NewLedgerPath() =>
        Path.Combine(Path.GetTempPath(), "ledger-tear4-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>Write c1..cN through the real writer, then hand back the on-disk lines.</summary>
    private static string[] SeedRecords(string path, params string[] itemIds)
    {
        using (var ledger = new DecisionLedger(path))
        {
            foreach (var id in itemIds)
                ledger.Append(id, DecisionLedger.DecisionExclude, "reason-" + id);
        }
        var lines = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(itemIds.Length, lines.Length);
        return lines;
    }

    // ── BLOCKER 1: interior newline lost ─────────────────────────────────────

    /// <summary>
    /// THE PROOF SHAPE. Three flushed records; the '\n' terminating record seq 1
    /// is lost, so records 1 and 2 arrive as one glued line at EOF and nothing
    /// parseable follows them.
    /// <para>
    /// Pre-fix: the glued line was unresolved junk, the committed mark stayed at
    /// the end of record 0, and resume TRUNCATED both records off the file. The
    /// next run then re-used seqs 1 and 2 on top of record 0 and Verify()
    /// returned Valid — two acknowledged audit records erased without a trace.
    /// </para>
    /// </summary>
    [Fact]
    public void ResumingOverALostInteriorNewline_KeepsEveryRecordAfterTheDamagedBoundary()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = SeedRecords(path, "c0", "c1", "c2");
            // Lose exactly one interior terminator: the one after record seq 1.
            File.WriteAllText(path, lines[0] + "\n" + lines[1] + lines[2] + "\n");
            var damaged = File.ReadAllText(path);

            using (var second = new DecisionLedger(path))
            {
                var appended = second.Append("c3", DecisionLedger.DecisionExclude, "reason-c3");
                // The chain must resume from the LAST record on disk (seq 2), not
                // from the last one before the damaged boundary.
                Assert.Equal(3, appended.Seq);
                Assert.True(second.Verify().Valid);
            }

            // Nothing that was already flushed may be gone from the file.
            var after = File.ReadAllText(path);
            Assert.Contains(lines[1], after);
            Assert.Contains(lines[2], after);
            Assert.StartsWith(damaged, after);

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "c0", "c1", "c2", "c3" }, all.Select(e => e.ItemId).ToArray());
            Assert.Equal(new long[] { 0, 1, 2, 3 }, all.Select(e => e.Seq).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The same damage with GOOD RECORDS FOLLOWING it: the '\n' after record 0
    /// is lost, records 2 and 3 are intact behind the glued line. Nothing may be
    /// truncated, the record COUNT must include the glued pair (so the
    /// seq/count consistency check does not fire a spurious alarm), and the
    /// resumed chain continues from the true tail.
    /// </summary>
    [Fact]
    public void ResumingOverALostInteriorNewline_WithGoodRecordsFollowing_LosesNothing()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = SeedRecords(path, "c0", "c1", "c2", "c3");
            File.WriteAllText(path, lines[0] + lines[1] + "\n" + lines[2] + "\n" + lines[3] + "\n");
            var damaged = File.ReadAllText(path);

            using (var second = new DecisionLedger(path))
            {
                var appended = second.Append("c4", DecisionLedger.DecisionExclude, "reason-c4");
                Assert.Equal(4, appended.Seq);
                Assert.True(second.Verify().Valid);
            }

            var after = File.ReadAllText(path);
            Assert.StartsWith(damaged, after);

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "c0", "c1", "c2", "c3", "c4" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The mixed case: a lost interior '\n' AND a crash part-way through the
    /// next record, so one physical line is "record, record, torn fragment".
    /// Only the fragment is uncommitted — the two records in front of it must
    /// survive, the fragment must be cut at its exact byte offset, and the
    /// terminator must be restored so the next append does not glue onto them.
    /// </summary>
    [Fact]
    public void ResumingOverRecordsGluedInFrontOfATornFragment_KeepsTheRecords_CutsOnlyTheFragment()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = SeedRecords(path, "c0", "c1", "c2");
            File.WriteAllText(
                path,
                lines[0] + "\n" + lines[1] + lines[2] + """{"Seq":3,"ItemId":"c3","Deci""");

            using (var second = new DecisionLedger(path))
            {
                var appended = second.Append("c3", DecisionLedger.DecisionExclude, "reason-c3");
                Assert.Equal(3, appended.Seq);
            }

            var after = File.ReadAllText(path);
            Assert.Contains(lines[1], after);
            Assert.Contains(lines[2], after);
            // The fragment carried ItemId "c3", and so does the record appended
            // over it: exactly ONE of them may survive. (Substring-counting
            // rather than DoesNotContain, because the fragment is a prefix of a
            // legitimate record.)
            Assert.Equal(1, Occurrences(after, """"ItemId":"c3""""));
            // c0 | c1+c2 glued | c3 — the fragment is gone and the terminator
            // was restored, so c3 is a line of its own.
            Assert.Equal(3, File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l)));

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "c0", "c1", "c2", "c3" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// ReadFile is the auditor's entry point, so it must see the glued records
    /// too — otherwise the records ResumeTail preserved would be unreachable and
    /// the ledger would read as corrupt for the rest of its life.
    /// </summary>
    [Fact]
    public void ReadFile_SeesRecordsGluedByALostNewline_RatherThanRefusingTheFile()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = SeedRecords(path, "c0", "c1", "c2");
            File.WriteAllText(path, lines[0] + "\n" + lines[1] + lines[2] + "\n");

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "c0", "c1", "c2" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The rescue must stay NARROW: gluing is only forgiven when the line really
    /// does split into complete records. Genuine interior corruption with
    /// records after it is still left in place and still surfaced.
    /// </summary>
    [Fact]
    public void GenuineInteriorCorruption_IsStillNotSwallowedByTheGlueRescue()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = SeedRecords(path, "c0", "c1");
            File.WriteAllText(path, lines[0] + "\n{ not a record at all\n" + lines[1] + "\n");

            using (var second = new DecisionLedger(path))
                second.Append("c2", DecisionLedger.DecisionExclude, "reason-c2");

            Assert.Contains("{ not a record at all", File.ReadAllText(path));
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── BLOCKER 2: lines that deserialize to an entry with NULL members ───────

    /// <summary>
    /// "{}" deserializes to a DecisionLedgerEntry whose every string member is
    /// null. Pre-fix it became the resume anchor and the next Append threw an
    /// unhandled NullReferenceException out of ComputeHash -> AppendField
    /// ('value.Length' on the null PrevHash). Here that Append is a ContentGate
    /// QUARANTINE — the security-critical decision the ledger exists to record.
    /// <para>
    /// The resume must still not throw and still not anchor on it. What CHANGED:
    /// these trailing bytes are complete JSON, which an interrupted write cannot
    /// produce, so they are no longer truncated away — they are kept as evidence
    /// (a single overwritten byte in the last record's key names produces exactly
    /// this shape, and discarding it destroyed the record silently). The file is
    /// therefore refused by ReadFile from here on, which is the loudness the
    /// truncation used to suppress.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"Seq":7,"ItemId":"ghost"}""")]
    [InlineData("""{"Seq":7,"ItemId":"ghost","Decision":"exclude","Reason":"r","Timestamp":"t"}""")]
    public void ResumingOverALineThatIsNotARecord_DoesNotThrow_AndDoesNotAnchorTheChainOnIt(
        string notARecord)
    {
        var path = NewLedgerPath();
        try
        {
            var lines = SeedRecords(path, "c0", "c1");
            File.WriteAllText(path, lines[0] + "\n" + lines[1] + "\n" + notARecord + "\n");

            DecisionLedgerEntry appended;
            using (var second = new DecisionLedger(path))
            {
                // The quarantine path: must not throw, must chain onto c1.
                appended = second.Append("c2", DecisionLedger.DecisionQuarantine, "malware-scan");
                Assert.True(second.Verify().Valid);
            }

            Assert.Equal(2, appended.Seq);
            Assert.NotNull(appended.PrevHash);
            Assert.NotEqual(DecisionLedger.GenesisHash, appended.PrevHash);

            // Kept, not truncated — and loud.
            Assert.Contains(notARecord, File.ReadAllText(path));
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// A file whose ONLY content is a null-member "entry" must resume as a fresh
    /// chain (genesis anchor), not as a chain hanging off a null hash.
    /// </summary>
    [Fact]
    public void ResumingOverAFileOfOnlyNullMemberEntries_StartsAFreshChain()
    {
        var path = NewLedgerPath();
        try
        {
            File.WriteAllText(path, "{}\nnull\n");

            using var ledger = new DecisionLedger(path);
            var appended = ledger.Append("c0", DecisionLedger.DecisionQuarantine, "prompt-injection");

            Assert.Equal(0, appended.Seq);
            Assert.Equal(DecisionLedger.GenesisHash, appended.PrevHash);
            Assert.True(ledger.Verify().Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The auditor path has the same hole: a null-member entry handed back by
    /// ReadFile detonates inside Verify's ComputeHash. ReadFile must not emit
    /// one. The tolerable crash-tail is a TORN one — an incomplete JSON value,
    /// the only shape an interrupted write can leave — and over that the intact
    /// prefix must come back and verify. (A final "{}" is complete JSON, so it
    /// is damage now, not a crash-tail; DecisionLedgerMangledFinalRecordTests
    /// covers that.)
    /// </summary>
    [Fact]
    public void ReadFile_NeverReturnsANullMemberEntry_SoVerifyCannotNre()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = SeedRecords(path, "c0", "c1");
            // Drop the closing brace: an incomplete JSON value, i.e. exactly what
            // a partially flushed write leaves behind.
            File.WriteAllText(path, lines[0] + "\n" + lines[1] + "\n" + lines[1][..^1] + "\n");

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "c0", "c1" }, all.Select(e => e.ItemId).ToArray());
            Assert.All(all, e =>
            {
                Assert.NotNull(e.ItemId);
                Assert.NotNull(e.Decision);
                Assert.NotNull(e.Reason);
                Assert.NotNull(e.Timestamp);
                Assert.NotNull(e.PrevHash);
                Assert.NotNull(e.Hash);
            });
            Assert.True(DecisionLedger.Verify(all).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>An INTERIOR "{}" is corruption, not a crash-tail: still surfaced.</summary>
    [Fact]
    public void ReadFile_InteriorNullMemberEntry_IsStillSurfaced()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = SeedRecords(path, "c0", "c1");
            File.WriteAllText(path, lines[0] + "\n{}\n" + lines[1] + "\n");

            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── Regression: the two shapes fixed in the previous round still hold ─────

    /// <summary>Shape 2 (newline-boundary tear) — the acknowledged final record survives.</summary>
    [Fact]
    public void Regression_NewlineBoundaryTear_StillKeepsTheAcknowledgedFinalRecord()
    {
        var path = NewLedgerPath();
        try
        {
            SeedRecords(path, "c0", "c1", "c2");
            var onDisk = File.ReadAllText(path);
            File.WriteAllText(path, onDisk[..^1]);   // drop ONLY the trailing '\n'

            using (var second = new DecisionLedger(path))
                second.Append("c3", DecisionLedger.DecisionExclude, "reason-c3");

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "c0", "c1", "c2", "c3" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
            Assert.Equal(4, File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l)));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Shape 3 (blank-line tear) — the torn line is not committed by the blank tail.</summary>
    [Fact]
    public void Regression_BlankLineTear_StillDoesNotBrickTheFile()
    {
        var path = NewLedgerPath();
        try
        {
            SeedRecords(path, "c0", "c1");
            File.AppendAllText(path, "{\"Seq\":2,\"ItemId\":\"c2\",\"Deci\n\n");

            using (var second = new DecisionLedger(path))
                second.Append("c3", DecisionLedger.DecisionExclude, "reason-c3");

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "c0", "c1", "c3" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Shape 1 (plain torn fragment) — still discarded, never a record.</summary>
    [Fact]
    public void Regression_PlainTornFragment_IsStillDiscarded()
    {
        var path = NewLedgerPath();
        try
        {
            SeedRecords(path, "c0", "c1");
            File.AppendAllText(path, """{"Seq":2,"ItemId":"c2","Deci""");

            using (var second = new DecisionLedger(path))
                second.Append("c3", DecisionLedger.DecisionExclude, "reason-c3");

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "c0", "c1", "c3" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
