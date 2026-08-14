// DecisionLedgerTests.cs
// ----------------------
// Connector.Chassis/DecisionLedger.cs — the append-only, SHA-256 hash-chained
// audit ledger. It is the one chassis module whose output is EVIDENCE: if it
// loses a record, or reports a file clean that is not, nobody downstream can
// tell. So these tests are written around the invariant the module states about
// itself rather than around its implementation:
//
//     NO ACKNOWLEDGED RECORD IS EVER SILENTLY LOST — it is recovered, or the
//     damage is loud. Never lost while Verify() says clean.
//
// What is covered here: the append/read round trip, the chain (intact, edited,
// re-hashed, reordered, deleted), NEWLINE DISCIPLINE AT THE BYTE LEVEL, the
// crash shapes a reader has to survive (torn tail, lost terminator, glued
// boundary, lone CR, mangled separator, damaged tail), Truncate's "only ever an
// interrupted write" rule, and the seq range at both ends.
//
// NEWLINE ASSERTIONS ARE MADE ON THE BYTES, DELIBERATELY. The shipped defect
// here was StreamWriter.WriteLine emitting Environment.NewLine — CRLF on Windows
// — into a format whose reader scans for '\n' and treats a lone CR between two
// records as DAMAGE. Measured: a whole-file CRLF ledger reads back as 2 of 2
// entries, IsClean=true, Verify=true — TrimTrailingWhitespace eats the CR at
// the end of every line — so a test that parses cannot fail on Windows and
// cannot catch the regression. Counting 0x0D is the only thing that can.
//
// GLOBALS: none. This module is constructed per-instance around a path, so
// nothing here touches Logging.RunDirectory, Chassis.Identity, Alerting or the
// environment — the only process state is the temp directory, deleted in
// Dispose. DECISION_LEDGER is the host's opt-in gate and is not read by the
// class, so it is not set either.
//
// PRIVATE TYPES: LedgerTail is private and is observed through the surface it
// feeds — NextSeq→BaseSeq, RecordCount→ResumedRecordCount, Damage→ResumedDamage,
// NeedsTerminator→the '\n' the constructor writes back.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Connector.Chassis.Tests;

public class DecisionLedgerTests
{
    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A private temp directory per test. Recursive delete is best-effort:
    /// on Windows a still-open StreamWriter would block it, and a leaked temp
    /// directory must never be the reason a suite goes red.
    /// </summary>
    private sealed class LedgerDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("chassis-ledger-").FullName;

        /// <summary>A ledger path inside this directory, built with the platform separator.</summary>
        public string PathTo(string name = "decision_ledger_chassis.jsonl") =>
            System.IO.Path.Combine(Root, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort; see the summary.
            }
        }
    }

    /// <summary>
    /// The writer's own serializer settings, so hand-built lines are
    /// byte-comparable with the ones Append emits.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Write <paramref name="itemIds"/> through the REAL writer and close it.</summary>
    private static void Seed(string path, params string[] itemIds)
    {
        using var ledger = new DecisionLedger(path);
        foreach (var id in itemIds)
            ledger.Append(id, DecisionLedger.DecisionExclude, "reason-" + id);
    }

    /// <summary>
    /// Split on the NEWLINE BYTE and nothing else — the same split the writer
    /// and both readers use. File.ReadAllLines would also break on a lone CR and
    /// would therefore hide exactly the damage several of these tests build.
    /// </summary>
    private static List<byte[]> SplitLf(byte[] bytes)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n')
                continue;
            lines.Add(bytes[start..i]);
            start = i + 1;
        }
        if (start < bytes.Length)
            lines.Add(bytes[start..]);
        return lines;
    }

    /// <summary>Rejoin lines, each LF-terminated (the shape a clean ledger has).</summary>
    private static byte[] JoinLf(IEnumerable<byte[]> lines)
    {
        var buf = new MemoryStream();
        foreach (var line in lines)
        {
            buf.Write(line);
            buf.WriteByte((byte)'\n');
        }
        return buf.ToArray();
    }

    private static DecisionLedgerEntry ParseLine(byte[] line) =>
        JsonSerializer.Deserialize<DecisionLedgerEntry>(line, Json)!;

    private static byte[] RenderLine(DecisionLedgerEntry entry) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json));

    private static long FileLength(string path) => new FileInfo(path).Length;

    /// <summary>Open, observe what the resume scan decided, close. Appends nothing.</summary>
    private static (long BaseSeq, bool Continued, long Records, LedgerFileDamage Damage) Resume(string path)
    {
        using var ledger = new DecisionLedger(path);
        return (ledger.BaseSeq, ledger.ContinuedExistingChain, ledger.ResumedRecordCount, ledger.ResumedDamage);
    }

    // ── round trip ───────────────────────────────────────────────────────────

    /// <summary>
    /// The base case everything else is a deviation from: what Append returned,
    /// what the instance holds and what an auditor reads back off disk are the
    /// same records, in the same order, with every field intact, and the chain
    /// verifies from genesis. If this breaks, the ledger is not evidence.
    /// </summary>
    [Fact]
    public void Append_ThenReadFile_RoundTripsEveryFieldAndVerifiesFromGenesis()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();

        List<DecisionLedgerEntry> written;
        using (var ledger = new DecisionLedger(path))
        {
            written = new List<DecisionLedgerEntry>
            {
                ledger.Append("item-A", DecisionLedger.DecisionExclude, "no MNE principals"),
                ledger.Append("item-B", DecisionLedger.DecisionAclRestrict, "narrowed to group g"),
                ledger.Append("item-C", DecisionLedger.DecisionQuarantine, "payload blocked by CS-1"),
            };
            Assert.Equal(3, ledger.Count);
            Assert.Equal(written, ledger.Entries);
            Assert.Equal(path, ledger.FilePath);
            Assert.False(ledger.ContinuedExistingChain);
            Assert.Equal(0, ledger.BaseSeq);
        }

        var read = DecisionLedger.ReadFile(path, out var damage);
        Assert.Equal(written, read);
        Assert.True(damage.IsClean);

        // Genesis anchoring and monotone seq are the two things an auditor
        // checks by eye before trusting the chain at all.
        Assert.Equal(new long[] { 0, 1, 2 }, read.Select(e => e.Seq).ToArray());
        Assert.Equal(DecisionLedger.GenesisHash, read[0].PrevHash);
        Assert.Equal(read[0].Hash, read[1].PrevHash);
        Assert.Equal(read[1].Hash, read[2].PrevHash);

        var verification = DecisionLedger.Verify(read);
        Assert.True(verification.Valid, verification.Detail);
        Assert.Null(verification.FirstBrokenSeq);
        Assert.Equal(LedgerVerification.Ok, verification);
    }

    /// <summary>
    /// The in-memory ledger is the no-op attachment every host wires
    /// unconditionally. It must chain exactly like the file-backed one and must
    /// not create a file — a no-op ledger that quietly wrote somewhere would put
    /// audit records outside the operator's retention and permission model.
    /// </summary>
    [Fact]
    public void InMemoryLedger_WritesNoFile_ButStillChainsAndVerifies()
    {
        using var dir = new LedgerDir();
        using var ledger = new DecisionLedger();

        ledger.Append("a", DecisionLedger.DecisionExclude, "r1");
        ledger.Append("b", DecisionLedger.DecisionExclude, "r2");

        Assert.Null(ledger.FilePath);
        Assert.Equal(2, ledger.Count);
        Assert.Equal(DecisionLedger.GenesisHash, ledger.Entries[0].PrevHash);
        Assert.True(ledger.Verify().Valid);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    /// <summary>
    /// Entries hands back a COPY. A live view would let a caller (or a later
    /// append) mutate a chain an auditor is midway through verifying.
    /// </summary>
    [Fact]
    public void Entries_IsASnapshot_NotALiveView()
    {
        using var ledger = new DecisionLedger();
        ledger.Append("a", DecisionLedger.DecisionExclude, "r1");
        var snapshot = ledger.Entries;

        ledger.Append("b", DecisionLedger.DecisionExclude, "r2");

        Assert.Single(snapshot);
        Assert.Equal(2, ledger.Count);
        Assert.NotSame(snapshot, ledger.Entries);
    }

    /// <summary>Dispose is the normal shutdown path and runs twice in try/using nests.</summary>
    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var dir = new LedgerDir();
        var ledger = new DecisionLedger(dir.PathTo());
        ledger.Append("a", DecisionLedger.DecisionExclude, "r");
        ledger.Dispose();
        ledger.Dispose();
        Assert.Single(DecisionLedger.ReadFile(dir.PathTo()));
    }

    // ── newline discipline (asserted on the bytes) ───────────────────────────

    /// <summary>
    /// EVERY record is terminated by one bare 0x0A and the file contains no 0x0D
    /// at all. This is the CRLF regression, and it is only visible in the bytes:
    /// Append using WriteLine emits Environment.NewLine, so on Windows every
    /// boundary becomes CRLF — a lone CR that the ledger's own reader classifies
    /// as a MANGLED SEPARATOR, i.e. damage, on a file the writer just wrote.
    /// Parsing the file would still succeed (the CR is trimmed as trailing
    /// whitespace), which is why nothing but a byte count can catch this.
    /// </summary>
    [Fact]
    public void Append_TerminatesEveryRecordWithOneBareLf_AndWritesNoCr()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(3, bytes.Count(b => b == (byte)'\n'));
        Assert.False(
            bytes.Contains((byte)'\r'),
            "the ledger is an LF-delimited format: a CR in the file is the CRLF regression "
            + "(WriteLine / Environment.NewLine), and its own reader reads a lone CR as damage.");
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal(3, SplitLf(bytes).Count);
    }

    /// <summary>
    /// Field text is attacker-influenced (item ids and reasons come from remote
    /// content), so the serializer's escaping is a security boundary, not a
    /// formatting detail: an unescaped '\n' in a reason would forge a physical
    /// line and let a caller inject a record into an append-only audit log. The
    /// bytes must hold exactly one 0x0A and no 0x0D however hostile the text is,
    /// and the values must round-trip unchanged.
    /// </summary>
    [Fact]
    public void AppendedTextWithControlCharacters_IsEscaped_AndCannotForgeAPhysicalLine()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();

        const string hostileId = "id\\with\"quotes\"/and\tslashes";
        // Escapes rather than literal characters, so the test does not depend on
        // this source file's own encoding surviving a round trip.
        const string hostileReason =
            "first\nsecond\r\n{\"Seq\":999,\"ItemId\":\"forged\"}  ünïcode \U0001F600";

        DecisionLedgerEntry written;
        using (var ledger = new DecisionLedger(path))
            written = ledger.Append(hostileId, DecisionLedger.DecisionExclude, hostileReason);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(1, bytes.Count(b => b == (byte)'\n'));
        Assert.False(bytes.Contains((byte)'\r'), "a CR from field text must be escaped, not emitted raw");

        var read = DecisionLedger.ReadFile(path, out var damage);
        Assert.True(damage.IsClean);
        var only = Assert.Single(read);
        Assert.Equal(written, only);
        Assert.Equal(hostileId, only.ItemId);
        Assert.Equal(hostileReason, only.Reason);
        Assert.True(DecisionLedger.Verify(read).Valid);
    }

    // ── the chain ────────────────────────────────────────────────────────────

    /// <summary>
    /// Edit a field in place and leave the stored hash alone: the record no
    /// longer hashes to what it claims. This is the crudest tamper and the one
    /// the per-record hash catches on its own.
    /// </summary>
    [Fact]
    public void Verify_DetectsAnEditedRecord()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        lines[1] = RenderLine(ParseLine(lines[1]) with { Reason = "reason was altered after the fact" });
        File.WriteAllBytes(path, JoinLf(lines));

        var verification = DecisionLedger.Verify(DecisionLedger.ReadFile(path));
        Assert.False(verification.Valid);
        Assert.Equal(1, verification.FirstBrokenSeq);
        Assert.Contains("tampered", verification.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case a per-record hash alone would MISS, and the reason this is a
    /// chain: edit a record AND recompute its own hash correctly. The record is
    /// now internally consistent, so the break surfaces one record later, where
    /// the next entry's PrevHash no longer names it. If this ever reports Valid,
    /// the ledger has stopped being tamper-evident while still looking like it.
    /// </summary>
    [Fact]
    public void Verify_DetectsAnEditedRecordEvenWhenItsOwnHashWasRecomputed()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        var target = ParseLine(lines[1]) with { Reason = "quietly rewritten" };
        target = target with
        {
            Hash = DecisionLedger.ComputeHash(
                target.Seq, target.ItemId, target.Decision, target.Reason, target.Timestamp, target.PrevHash),
        };
        lines[1] = RenderLine(target);
        File.WriteAllBytes(path, JoinLf(lines));

        var entries = DecisionLedger.ReadFile(path);
        // The forged record passes on its own terms...
        Assert.True(DecisionLedger.Verify(entries.Take(2).ToList()).Valid);
        // ...and the chain still catches it, at the record that links to it.
        var verification = DecisionLedger.Verify(entries);
        Assert.False(verification.Valid);
        Assert.Equal(2, verification.FirstBrokenSeq);
        Assert.Contains("chain broken", verification.Detail!, StringComparison.Ordinal);
    }

    /// <summary>Reordering two records breaks the seq run before the hashes are even reached.</summary>
    [Fact]
    public void Verify_DetectsReorderedRecords()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        (lines[0], lines[1]) = (lines[1], lines[0]);
        File.WriteAllBytes(path, JoinLf(lines));

        var verification = DecisionLedger.Verify(DecisionLedger.ReadFile(path));
        Assert.False(verification.Valid);
        Assert.Equal(1, verification.FirstBrokenSeq);
        Assert.Contains("seq out of order at index 0", verification.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deleting a record is the tamper that leaves the fewest traces — the file
    /// is still well-formed JSONL and nothing is physically damaged. The seq run
    /// is what exposes it.
    /// </summary>
    [Fact]
    public void Verify_DetectsADeletedRecord()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        lines.RemoveAt(1);
        File.WriteAllBytes(path, JoinLf(lines));

        // Nothing is physically wrong with the file: the damage report is clean
        // and only the chain knows.
        var entries = DecisionLedger.ReadFile(path, out var damage);
        Assert.True(damage.IsClean);
        Assert.Equal(2, entries.Count);

        var verification = DecisionLedger.Verify(entries);
        Assert.False(verification.Valid);
        Assert.Equal(2, verification.FirstBrokenSeq);
        Assert.Contains("seq out of order at index 1", verification.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The instance Verify() checks THIS RUN's segment against the anchor it
    /// resumed from, which is why it can pass on a resumed ledger that starts at
    /// a non-zero seq. The static default (seq 0 from genesis) must NOT pass on
    /// that same segment: the two overloads answer different questions, and
    /// conflating them would have a resumed run's self-check silently verifying
    /// against a genesis it never linked to.
    /// </summary>
    [Fact]
    public void Verify_OfAResumedSegment_UsesTheAnchor_WhileTheDefaultsAreForWholeChains()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b");

        IReadOnlyList<DecisionLedgerEntry> segment;
        using (var second = new DecisionLedger(path))
        {
            Assert.True(second.ContinuedExistingChain);
            Assert.Equal(2, second.BaseSeq);
            second.Append("c", DecisionLedger.DecisionExclude, "r3");
            second.Append("d", DecisionLedger.DecisionExclude, "r4");
            segment = second.Entries;
            Assert.True(second.Verify().Valid);
        }

        Assert.Equal(new long[] { 2, 3 }, segment.Select(e => e.Seq).ToArray());
        Assert.False(DecisionLedger.Verify(segment).Valid);
        Assert.True(DecisionLedger.Verify(DecisionLedger.ReadFile(path)).Valid);
    }

    /// <summary>
    /// Fields are LENGTH-PREFIXED into the hash so no rearrangement of field
    /// boundaries can collide. Under a naive "join with |" these two entries
    /// hash the same, and an auditor could be shown either one.
    /// </summary>
    [Fact]
    public void ComputeHash_IsLengthPrefixed_SoFieldBoundariesCannotBeShifted()
    {
        var ts = "2026-08-14T00:00:00.0000000+00:00";
        var left = DecisionLedger.ComputeHash(7, "ab", "c", "r", ts, DecisionLedger.GenesisHash);
        var right = DecisionLedger.ComputeHash(7, "a", "bc", "r", ts, DecisionLedger.GenesisHash);
        Assert.NotEqual(left, right);

        // Deterministic, and the shape ScanHex64 requires of every hash member:
        // exactly 64 lowercase hex characters. An uppercase hash would be
        // classified as damage by the recovery path.
        Assert.Equal(left, DecisionLedger.ComputeHash(7, "ab", "c", "r", ts, DecisionLedger.GenesisHash));
        Assert.Equal(64, left.Length);
        Assert.All(left, c => Assert.True(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f', $"'{c}' is not lowercase hex"));
        Assert.Equal(64, DecisionLedger.GenesisHash.Length);
    }

    // ── crash shapes: what a reopen may and may not throw away ───────────────

    /// <summary>
    /// Reopening an UNDAMAGED ledger must not touch a byte of it. Truncate is
    /// only ever allowed to remove an interrupted write, so "no interrupted
    /// write" has to mean "no write at all" — a reopen that rewrote or trimmed
    /// anything here would be destroying evidence on the happy path.
    /// </summary>
    [Fact]
    public void Reopen_OfACleanLedger_IsByteForBytePreserving_AndContinuesTheChain()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b");
        var before = File.ReadAllBytes(path);

        var resumed = Resume(path);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(2, resumed.BaseSeq);
        Assert.True(resumed.Continued);
        Assert.Equal(2, resumed.Records);
        Assert.True(resumed.Damage.IsClean);

        using (var second = new DecisionLedger(path))
        {
            var next = second.Append("c", DecisionLedger.DecisionExclude, "r3");
            Assert.Equal(2, next.Seq);
            // The first entry of a resumed run links to the LAST entry of the
            // previous run — that link is the whole between-runs continuity claim.
            Assert.Equal(ParseLine(SplitLf(before)[^1]).Hash, next.PrevHash);
        }

        // The clean prefix is still there, byte for byte, with the new record after it.
        var after = File.ReadAllBytes(path);
        Assert.Equal(before, after[..before.Length]);
        Assert.True(DecisionLedger.Verify(DecisionLedger.ReadFile(path)).Valid);
    }

    /// <summary>
    /// SHAPE 2 — the final record is COMPLETE but its '\n' never landed. Its
    /// bytes are all on disk, so Append already told the caller it was durable:
    /// discarding it is the worst outcome available, because the surviving prefix
    /// plus the next run's entries still verify CLEAN over the hole.
    /// <para>
    /// The repair is asserted byte-for-byte against what the writer would have
    /// produced: the restored terminator must be a bare 0x0A, not CRLF, or the
    /// repair path re-creates the mangled-separator damage it exists to undo.
    /// </para>
    /// </summary>
    [Fact]
    public void Reopen_AfterALostFinalNewline_KeepsTheRecordAndRestoresABareLf()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");
        var intact = File.ReadAllBytes(path);
        File.WriteAllBytes(path, intact[..^1]);   // the crash ate only the terminator

        using (var reopened = new DecisionLedger(path))
        {
            Assert.Equal(3, reopened.ResumedRecordCount);   // the record was NOT dropped
            Assert.Equal(3, reopened.BaseSeq);
            Assert.True(reopened.ResumedDamage.IsClean);    // a lost terminator is not file damage
        }

        Assert.Equal(intact, File.ReadAllBytes(path));

        using (var reopened = new DecisionLedger(path))
            Assert.Equal(3, reopened.Append("d", DecisionLedger.DecisionExclude, "r4").Seq);

        var entries = DecisionLedger.ReadFile(path, out var damage);
        Assert.True(damage.IsClean);
        Assert.Equal(new[] { "a", "b", "c", "d" }, entries.Select(e => e.ItemId).ToArray());
        Assert.True(DecisionLedger.Verify(entries).Valid);
    }

    /// <summary>
    /// SHAPE 1 — a genuinely interrupted write: the final record's bytes stop
    /// partway. Nothing acknowledged it, so it is the one thing Truncate may
    /// delete, and it must delete EXACTLY it: the committed mark has to land on
    /// the byte after the last good terminator whatever depth the write reached.
    /// Cutting one byte too few leaves interior corruption behind the next
    /// append; one too many destroys a durable record.
    /// </summary>
    [Theory]
    [InlineData(1)]     // just the opening '{'
    [InlineData(8)]     // inside the Seq literal
    [InlineData(40)]    // inside a text value
    [InlineData(-1)]    // everything but the closing '}'
    public void Reopen_AfterATornFinalWrite_TruncatesExactlyTheFragment(int depth)
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        var committed = JoinLf(lines.Take(2));
        var fragment = depth < 0 ? lines[2][..^1] : lines[2][..depth];
        File.WriteAllBytes(path, committed.Concat(fragment).ToArray());

        // Before any repair: an auditor can still read the intact prefix, and a
        // torn tail is a crash-tail, NOT damage — the file is reported clean.
        var beforeRepair = DecisionLedger.ReadFile(path, out var damage);
        Assert.Equal(2, beforeRepair.Count);
        Assert.True(damage.IsClean);
        Assert.True(DecisionLedger.Verify(beforeRepair).Valid);

        var resumed = Resume(path);
        Assert.Equal(committed.Length, FileLength(path));
        Assert.Equal(2, resumed.Records);
        Assert.Equal(2, resumed.BaseSeq);
        Assert.True(resumed.Damage.IsClean);

        using (var reopened = new DecisionLedger(path))
            Assert.Equal(2, reopened.Append("c-again", DecisionLedger.DecisionExclude, "r").Seq);

        var entries = DecisionLedger.ReadFile(path, out var afterDamage);
        Assert.True(afterDamage.IsClean);
        Assert.Equal(new[] { "a", "b", "c-again" }, entries.Select(e => e.ItemId).ToArray());
        Assert.True(DecisionLedger.Verify(entries).Valid);
    }

    /// <summary>
    /// A torn FIRST write — the file holds nothing but a fragment. There is no
    /// chain to resume, so the fragment goes and the ledger starts at genesis;
    /// the file must be trimmed to empty rather than left with a fragment that
    /// the next append turns into permanent interior corruption.
    /// </summary>
    [Fact]
    public void Reopen_WhenTheOnlyLineIsATornWrite_StartsAFreshChainAndTrimsToEmpty()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a");
        var fragment = SplitLf(File.ReadAllBytes(path))[0][..30];
        File.WriteAllBytes(path, fragment);

        var resumed = Resume(path);
        Assert.Equal(0, FileLength(path));
        Assert.Equal(0, resumed.BaseSeq);
        Assert.False(resumed.Continued);
        Assert.Equal(0, resumed.Records);
        Assert.True(resumed.Damage.IsClean);

        using (var fresh = new DecisionLedger(path))
            Assert.Equal(DecisionLedger.GenesisHash, fresh.Append("a", DecisionLedger.DecisionExclude, "r").PrevHash);
        Assert.True(DecisionLedger.Verify(DecisionLedger.ReadFile(path)).Valid);
    }

    /// <summary>
    /// SHAPE 6 — damage INSIDE the final record: one byte of a key name is
    /// overwritten, so the line is complete, valid JSON that simply is not a
    /// record. No interrupted write can produce that, so the bytes must be KEPT
    /// (they are the only evidence a record was ever there) and ReadFile must
    /// REFUSE the file — nothing is behind the last record, so no seq gap can
    /// ever appear and refusal is the only channel the auditor has.
    /// </summary>
    [Fact]
    public void ADamagedFinalRecord_IsKeptAsEvidence_AndReadFileRefusesTheFile()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        lines[2] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(lines[2]).Replace("\"Seq\":", "\"Xeq\":", StringComparison.Ordinal));
        var damaged = JoinLf(lines);
        File.WriteAllBytes(path, damaged);

        var refused = Assert.Throws<JsonException>(() => DecisionLedger.ReadFile(path));
        Assert.Contains("DAMAGED", refused.Message, StringComparison.Ordinal);

        // The resume keeps every byte and reports the damage rather than healing
        // it: truncating here would leave a prefix that verifies CLEAN over a
        // destroyed record.
        var resumed = Resume(path);
        Assert.Equal(damaged.Length, FileLength(path));
        Assert.True(resumed.Damage.DamagedTail);
        Assert.False(resumed.Damage.IsClean);
        Assert.Equal(2, resumed.Records);
        Assert.Equal(2, resumed.BaseSeq);

        using (var reopened = new DecisionLedger(path))
            reopened.Append("d", DecisionLedger.DecisionExclude, "r");

        // The evidence survives the next append, and the file stays refused.
        Assert.Contains("\"Xeq\":", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => DecisionLedger.ReadFile(path));
    }

    /// <summary>
    /// SHAPE 4 — the '\n' between two flushed records is simply LOST, gluing them
    /// onto one line. Both were acknowledged, so both must come back and the seqs
    /// must stay contiguous; but the file is physically damaged and IsClean must
    /// say so. This is the gap LedgerFileDamage exists for: a chain that verifies
    /// perfectly over a file an integrity monitor should still be shouting about.
    /// </summary>
    [Fact]
    public void AGluedRecordBoundary_RecoversBothRecords_ButIsReportedAsDamage()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        File.WriteAllBytes(path, JoinLf(new[] { lines[0], lines[1].Concat(lines[2]).ToArray() }));

        var entries = DecisionLedger.ReadFile(path, out var damage);
        Assert.Equal(new[] { "a", "b", "c" }, entries.Select(e => e.ItemId).ToArray());
        Assert.True(DecisionLedger.Verify(entries).Valid);
        Assert.Equal(1, damage.GluedLines);
        Assert.Equal(0, damage.ResyncedRegions);
        Assert.False(damage.IsClean);

        // Both readers of a ledger must agree about what the file holds; a
        // disagreement is itself the defect ResumedRecordCount documents.
        var resumed = Resume(path);
        Assert.Equal(3, resumed.Records);
        Assert.Equal(3, resumed.BaseSeq);
        Assert.Equal(1, resumed.Damage.GluedLines);
    }

    /// <summary>
    /// A LONE CR where the '\n' should be. Append only ever writes '\n', so a CR
    /// sitting between two records means the terminator was OVERWRITTEN — damage.
    /// File.ReadLines heals a lone CR into an ordinary line break, and while
    /// ReadFile used it this file read back CLEAN while the byte-scanning resume
    /// path called it glued: the two readers of one ledger disagreeing. Both must
    /// now report the damage. If IsClean ever goes true here, someone has put a
    /// CR-splitting reader back.
    /// </summary>
    [Fact]
    public void ALoneCrBetweenRecords_IsReportedAsDamageByBothReaders()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        var crJoined = lines[1].Concat(new[] { (byte)'\r' }).Concat(lines[2]).ToArray();
        File.WriteAllBytes(path, JoinLf(new[] { lines[0], crJoined }));

        var entries = DecisionLedger.ReadFile(path, out var damage);
        Assert.Equal(new[] { "a", "b", "c" }, entries.Select(e => e.ItemId).ToArray());
        Assert.True(DecisionLedger.Verify(entries).Valid);
        Assert.False(damage.IsClean, "a lone CR between two records is a destroyed terminator, not a line break");
        Assert.Equal(1, damage.GluedLines);

        var resumed = Resume(path);
        Assert.Equal(3, resumed.Records);
        Assert.Equal(1, resumed.Damage.GluedLines);
    }

    /// <summary>
    /// SHAPE 5 — the separator was OVERWRITTEN by a NUL (the classic
    /// allocated-but-unwritten block a crash leaves). The scan must
    /// RESYNCHRONISE onto the record behind it: stopping at the bad byte put
    /// every later record outside the committed mark and Truncate then destroyed
    /// them, with the survivors still verifying clean.
    /// </summary>
    [Fact]
    public void AMangledSeparator_ResynchronisesOntoTheRecordBehindIt_AndReportsTheRegion()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        var mangled = lines[1].Concat(new byte[] { 0x00 }).Concat(lines[2]).ToArray();
        var onDisk = JoinLf(new[] { lines[0], mangled });
        File.WriteAllBytes(path, onDisk);

        var entries = DecisionLedger.ReadFile(path, out var damage);
        Assert.Equal(new[] { "a", "b", "c" }, entries.Select(e => e.ItemId).ToArray());
        Assert.True(DecisionLedger.Verify(entries).Valid);
        Assert.Equal(1, damage.ResyncedRegions);
        Assert.False(damage.IsClean);

        // Nothing is truncated, the destroyed byte is kept, and the next append
        // resumes from the TRUE tail rather than from in front of the damage.
        using (var reopened = new DecisionLedger(path))
        {
            Assert.Equal(3, reopened.ResumedRecordCount);
            Assert.Equal(1, reopened.ResumedDamage.ResyncedRegions);
            Assert.Equal(3, reopened.Append("d", DecisionLedger.DecisionExclude, "r").Seq);
        }
        Assert.Equal(onDisk, File.ReadAllBytes(path)[..onDisk.Length]);
        Assert.True(DecisionLedger.Verify(DecisionLedger.ReadFile(path)).Valid);
    }

    /// <summary>
    /// A malformed line with good records AFTER it is interior corruption, not a
    /// crash-tail. Dropping it would punch a hole no seq gap reveals if the
    /// records around it happen to stay contiguous, so ReadFile refuses the file
    /// outright.
    /// </summary>
    [Fact]
    public void ReadFile_RefusesInteriorCorruption_RatherThanSkippingIt()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        lines.Insert(1, Encoding.UTF8.GetBytes("this line is not a decision record"));
        File.WriteAllBytes(path, JoinLf(lines));

        var refused = Assert.Throws<JsonException>(() => DecisionLedger.ReadFile(path));
        Assert.Contains("malformed interior record", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// DOCUMENTS A DEFECT (reported, not fixed here). Interior corruption is
    /// refused by ReadFile — see above — but the RESUME scan reading the same
    /// bytes reports the file physically CLEAN, logs nothing at all, and appends
    /// after it. The seq-consistency check does not fire either, because a line
    /// inserted between two records displaces no seq. So the connector keeps
    /// running on a ledger that is permanently unreadable to an auditor, and
    /// ResumedDamage — the channel an operator watches for exactly this — says
    /// nothing is wrong. LedgerFileDamage has no counter for "a whole line that
    /// held no record": the scan tracks it internally as unresolvedJunk and
    /// drops it on the floor.
    /// <para>
    /// The assertions below are what the code does TODAY. If the resume scan
    /// starts reporting this file as damaged, this test should be updated, not
    /// the report deleted.
    /// </para>
    /// </summary>
    [Fact]
    public void InteriorCorruption_IsRefusedByReadFile_ButTheResumeScanReportsTheFileClean()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b");

        var lines = SplitLf(File.ReadAllBytes(path));
        lines.Insert(1, Encoding.UTF8.GetBytes("this line is not a decision record"));
        var onDisk = JoinLf(lines);
        File.WriteAllBytes(path, onDisk);

        Assert.Throws<JsonException>(() => DecisionLedger.ReadFile(path));

        var resumed = Resume(path);
        Assert.Equal(2, resumed.Records);
        Assert.Equal(2, resumed.BaseSeq);
        Assert.True(resumed.Damage.IsClean, "current behaviour: interior corruption is invisible to the resume scan");
        Assert.Equal(onDisk.Length, FileLength(path));   // not truncated, at least

        using (var reopened = new DecisionLedger(path))
            Assert.Equal(2, reopened.Append("c", DecisionLedger.DecisionExclude, "r").Seq);

        // Still refused, now for the life of the file.
        Assert.Throws<JsonException>(() => DecisionLedger.ReadFile(path));
    }

    /// <summary>
    /// DOCUMENTS A DEFECT (reported, not fixed here). A final line holding a
    /// COMPLETE record followed by a torn fragment can only exist if the '\n'
    /// between them was lost — Append writes the record and its terminator into
    /// the same flush, so a later record's fragment can never legitimately share
    /// a line with an earlier record. The resume scan agrees and reports
    /// GluedLines=1; ReadFile calls the same bytes a plain crash-tail and reports
    /// IsClean=true, because its glued counter only fires on lines holding
    /// several RECORDS. An integrity monitor using ReadFile's damage report —
    /// the documented way to notice a physically damaged ledger whose chain still
    /// verifies — is told the file is fine.
    /// <para>
    /// The recovery itself is correct and is pinned here too: the cut is made at
    /// the fragment's exact byte offset INSIDE the line, so the record sharing
    /// that line survives and the terminator is restored.
    /// </para>
    /// </summary>
    [Fact]
    public void ARecordFollowedByATornFragment_IsDamageToTheResumeScan_ButCleanToReadFile()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b", "c");

        var lines = SplitLf(File.ReadAllBytes(path));
        var clean = JoinLf(lines.Take(2));
        File.WriteAllBytes(path, JoinLf(new[] { lines[0] }).Concat(lines[1]).Concat(lines[2][..30]).ToArray());

        var entries = DecisionLedger.ReadFile(path, out var damage);
        Assert.Equal(new[] { "a", "b" }, entries.Select(e => e.ItemId).ToArray());
        Assert.Equal(0, damage.GluedLines);
        Assert.True(damage.IsClean, "current behaviour: ReadFile does not report the lost terminator");

        var resumed = Resume(path);
        Assert.Equal(1, resumed.Damage.GluedLines);      // ...but the resume scan does
        Assert.False(resumed.Damage.IsClean);
        Assert.Equal(2, resumed.Records);

        // Only the fragment went, and the record that shared its line kept its
        // restored terminator: the repaired file is the clean 2-record prefix.
        Assert.Equal(clean, File.ReadAllBytes(path));
    }

    /// <summary>
    /// DOCUMENTS A DEFECT (reported, not fixed here). Append adds the entry to
    /// the in-memory chain and advances _lastHash BEFORE flushing it, so a write
    /// that fails — ENOSPC on the log volume is the realistic one; use-after-
    /// dispose is the portable way to force it — leaves the instance holding a
    /// record the file does not have, and its own Verify() reports that chain
    /// VALID. "Verify says clean over a record that is not on disk" is the exact
    /// failure this module is built to make impossible on the read side.
    /// </summary>
    [Fact]
    public void Append_AdvancesTheInMemoryChainBeforeTheWrite_SoAFailedWriteLeavesThemDisagreeing()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        var ledger = new DecisionLedger(path);
        ledger.Append("a", DecisionLedger.DecisionExclude, "r1");
        ledger.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => ledger.Append("b", DecisionLedger.DecisionExclude, "r2"));

        Assert.Equal(2, ledger.Count);                       // the failed write is in the chain
        Assert.True(ledger.Verify().Valid);                  // ...and Verify() is happy about it
        Assert.Single(DecisionLedger.ReadFile(path));        // while the file has one record
    }

    /// <summary>
    /// Blank lines are separators, not records: a crash that lands a newline and
    /// nothing else must not shift the record count or the chain anchor.
    /// </summary>
    [Fact]
    public void BlankLines_AreSkipped_AndDoNotDisturbTheChain()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        Seed(path, "a", "b");

        var lines = SplitLf(File.ReadAllBytes(path));
        File.WriteAllBytes(path, JoinLf(new[] { lines[0], Array.Empty<byte>(), lines[1], Array.Empty<byte>() }));

        var entries = DecisionLedger.ReadFile(path, out var damage);
        Assert.Equal(2, entries.Count);
        Assert.True(damage.IsClean);
        Assert.True(DecisionLedger.Verify(entries).Valid);

        var resumed = Resume(path);
        Assert.Equal(2, resumed.Records);
        Assert.Equal(2, resumed.BaseSeq);
    }

    /// <summary>
    /// An empty ledger file is a valid empty chain, and a MISSING one is not the
    /// same thing: ReadFile throws rather than returning an empty list, so an
    /// auditor who mistypes a path cannot be shown "no decisions were recorded".
    /// The writer, by contrast, creates the file and starts at genesis.
    /// </summary>
    [Fact]
    public void ReadFile_OnAnEmptyFileIsAnEmptyChain_ButOnAMissingFileItThrows()
    {
        using var dir = new LedgerDir();
        var empty = dir.PathTo("empty.jsonl");
        File.WriteAllBytes(empty, Array.Empty<byte>());

        Assert.Empty(DecisionLedger.ReadFile(empty, out var damage));
        Assert.True(damage.IsClean);
        Assert.True(DecisionLedger.Verify(Array.Empty<DecisionLedgerEntry>()).Valid);

        var missing = dir.PathTo("never-written.jsonl");
        Assert.Throws<FileNotFoundException>(() => DecisionLedger.ReadFile(missing));

        using (var created = new DecisionLedger(missing))
            Assert.Equal(0, created.Append("a", DecisionLedger.DecisionExclude, "r").Seq);
        Assert.True(File.Exists(missing));
    }

    // ── the seq range, at both ends ──────────────────────────────────────────

    /// <summary>
    /// A persisted seq outside <c>[0, MaxSeq]</c> is not something this writer
    /// could have issued, so it is not a record and must never become the resume
    /// anchor: a negative anchor made the chain issue negative seqs, and a
    /// <c>long.MaxValue</c> anchor made the very next seq overflow to
    /// <c>long.MinValue</c> with Verify() still reporting true. The line is kept
    /// as evidence (it is damage, not a torn write) and the file stays refused.
    /// </summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void AnAnchorSeqOutsideTheIssuableRange_IsNotARecord(long seq)
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        var line = RenderLine(new DecisionLedgerEntry(
            seq, "anchor", DecisionLedger.DecisionExclude, "r",
            "2026-08-14T00:00:00.0000000+00:00", DecisionLedger.GenesisHash, new string('a', 64)));
        var onDisk = JoinLf(new[] { line });
        File.WriteAllBytes(path, onDisk);

        var resumed = Resume(path);
        Assert.Equal(0, resumed.Records);
        Assert.Equal(0, resumed.BaseSeq);
        Assert.False(resumed.Continued);
        Assert.True(resumed.Damage.DamagedTail);
        Assert.Equal(onDisk.Length, FileLength(path));   // kept, not truncated

        using (var ledger = new DecisionLedger(path))
        {
            var first = ledger.Append("after", DecisionLedger.DecisionExclude, "r");
            Assert.Equal(0, first.Seq);
            Assert.Equal(DecisionLedger.GenesisHash, first.PrevHash);
        }
        Assert.Throws<JsonException>(() => DecisionLedger.ReadFile(path));
    }

    /// <summary>
    /// MaxSeq is the largest seq Append will issue, and the bound has to hold at
    /// the point of ISSUE, not only at the point of acceptance: an anchor of
    /// MaxSeq is legitimate, so the guard that stops the next append is the only
    /// thing between the chain and a wrapped, negative seq that Verify() cannot
    /// see. Reachable only through a crafted anchor — 2^63 real appends is not a
    /// test.
    /// </summary>
    [Fact]
    public void Append_AtTheTopOfTheSeqSpace_ThrowsRatherThanWrapping()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        const string ts = "2026-08-14T00:00:00.0000000+00:00";
        var hash = DecisionLedger.ComputeHash(
            DecisionLedger.MaxSeq, "last", DecisionLedger.DecisionExclude, "r", ts, DecisionLedger.GenesisHash);
        var onDisk = JoinLf(new[]
        {
            RenderLine(new DecisionLedgerEntry(
                DecisionLedger.MaxSeq, "last", DecisionLedger.DecisionExclude, "r", ts,
                DecisionLedger.GenesisHash, hash)),
        });
        File.WriteAllBytes(path, onDisk);

        using (var ledger = new DecisionLedger(path))
        {
            Assert.Equal(long.MaxValue, ledger.BaseSeq);
            var exhausted = Assert.Throws<InvalidOperationException>(
                () => ledger.Append("one-too-many", DecisionLedger.DecisionExclude, "r"));
            Assert.Contains("seq space is exhausted", exhausted.Message, StringComparison.Ordinal);
            Assert.Equal(0, ledger.Count);
        }

        // The refusal is total: nothing was written, so no negative seq exists.
        Assert.Equal(onDisk, File.ReadAllBytes(path));
    }

    // ── paths and legacy files ───────────────────────────────────────────────

    /// <summary>
    /// The ledger lives at the LOGS ROOT under a per-connector name, not inside
    /// logs/{run}/ — a per-run path both restarts the chain every run and puts
    /// the audit records inside what LOG_RETENTION_DAYS deletes. Built with
    /// Path.Combine so the assertion holds on both separators.
    /// </summary>
    [Fact]
    public void PathFor_IsTheStablePerConnectorPathAtTheLogsRoot()
    {
        var logs = Path.Combine("var", "connector", "logs");
        var path = DecisionLedger.PathFor(logs, "seismic");

        Assert.Equal(Path.Combine(logs, "decision_ledger_seismic.jsonl"), path);
        Assert.Equal(logs, Path.GetDirectoryName(path));
        Assert.Equal("decision_ledger_seismic.jsonl", Path.GetFileName(path));
    }

    /// <summary>
    /// Legacy per-run ledgers are separate chains rooted at their own genesis and
    /// are NOT continued, so they have to be surfaced rather than orphaned. Only
    /// SUBDIRECTORY files qualify: the current ledger sits at the root and
    /// reporting it as legacy would send an operator chasing the live file.
    /// </summary>
    [Fact]
    public void FindLegacyRunLedgers_ReturnsSubdirectoryLedgersOnly_InOrdinalOrder()
    {
        using var dir = new LedgerDir();
        var current = DecisionLedger.PathFor(dir.Root, "seismic");
        File.WriteAllText(current, "");

        var runB = Path.Combine(dir.Root, "ingest_b");
        var runA = Path.Combine(dir.Root, "ingest_a");
        Directory.CreateDirectory(runB);
        Directory.CreateDirectory(runA);
        var legacyB = Path.Combine(runB, "decision_ledger_seismic.jsonl");
        var legacyA = Path.Combine(runA, "decision_ledger_seismic.jsonl");
        File.WriteAllText(legacyB, "");
        File.WriteAllText(legacyA, "");
        File.WriteAllText(Path.Combine(runA, "ingest.log"), "");

        Assert.Equal(new[] { legacyA, legacyB }, DecisionLedger.FindLegacyRunLedgers(dir.Root));

        // Never throws — a missing logs root must not take a crawl down.
        Assert.Empty(DecisionLedger.FindLegacyRunLedgers(Path.Combine(dir.Root, "does-not-exist")));
    }

    // ── concurrency ──────────────────────────────────────────────────────────

    /// <summary>
    /// Crawl workers append concurrently. The lock has to make the whole
    /// seq/prev-hash/write triple atomic: a race that interleaves two writes
    /// produces either a duplicate seq or a torn line, and both destroy the
    /// chain. Verified through the FILE, not the in-memory list, because the
    /// in-memory list would look fine even if the writes interleaved on disk.
    /// </summary>
    [Fact]
    public void ConcurrentAppends_ProduceOneContiguousVerifiableChainOnDisk()
    {
        using var dir = new LedgerDir();
        var path = dir.PathTo();
        const int threads = 8;
        const int perThread = 125;

        using (var ledger = new DecisionLedger(path))
        {
            Parallel.For(0, threads, t =>
            {
                for (var i = 0; i < perThread; i++)
                    ledger.Append($"item-{t}-{i}", DecisionLedger.DecisionExclude, $"reason {t}/{i}");
            });
        }

        var entries = DecisionLedger.ReadFile(path, out var damage);
        Assert.True(damage.IsClean);
        Assert.Equal(threads * perThread, entries.Count);
        Assert.Equal(
            Enumerable.Range(0, threads * perThread).Select(i => (long)i).ToArray(),
            entries.Select(e => e.Seq).ToArray());
        var verification = DecisionLedger.Verify(entries);
        Assert.True(verification.Valid, verification.Detail);
    }
}
