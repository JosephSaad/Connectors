// DecisionLedgerMangledSeparatorTests.cs
// -------------------------------------
// Round 5 of the decision-ledger tear work, and the round that stops fixing
// SHAPES and starts fixing the CLASS.
//
// Rounds 2-4 each fixed the tear shape that had just been demonstrated, and each
// time a new shape appeared behind it. The reason they did not converge is
// visible in the tests they shipped: every one of them built its damage by
// STRING CONCATENATION of whole lines. Concatenating lines can only ever model a
// separator that VANISHED. Neither DecisionLedgerDurabilityTests nor
// DecisionLedgerInteriorTearTests contains a single stray byte at a record
// boundary, so the entire family of "the separator was OVERWRITTEN" damage was
// structurally invisible to the suite.
//
// It is also the likelier physical damage. A crash that loses a '\n' outright is
// a torn write; a crash on APFS/ext4 that leaves an allocated-but-unwritten
// block gives you a NUL — the separator is still there, it is just no longer a
// separator. ScanRecords stopped at the first byte that was not the start of a
// record, EndOfLine then read everything from that offset to EOF as an
// uncommitted crash-tail, and Truncate DESTROYED every acknowledged record
// behind the damage. The next run re-used their seqs, and ReadFile + Verify
// reported CLEAN over a ledger that had lost audit evidence.
//
// The fix is structural, not another special case:
//
//   1. RESYNCHRONISATION. A parse failure inside a line no longer ends the scan.
//      It steps forward to the next plausible record start and keeps committing,
//      so no amount of destroyed separator can put a later record behind the
//      truncation cliff. A merely LOST separator needs no resync at all, which
//      is why the previous round's fix looked like it worked.
//   2. TRUNCATE ONLY INTERRUPTED WRITES. Trailing bytes are discarded only when
//      they are an INCOMPLETE JSON value (the signature of a partial flush) or a
//      complete JSON value that was never a record. Bytes that are INVALID where
//      they sit cannot be produced by an interrupted write — something
//      overwrote already-flushed data — so they are kept as evidence and
//      ReadFile refuses the file.
//   3. THE CONTRACT, NOT A CHECKLIST. Every member of DecisionLedgerEntry is
//      [JsonRequired]. The previous null-check could not see a missing Seq at
//      all: a non-nullable long default-fills to 0, and a "record" with seq 0
//      became the resume anchor and reset the chain's next seq to 1, re-issuing
//      seqs that live records already held.
//
// So the tests below do not hand-pick cases either. CorruptionSweep generates
// damage systematically — every byte offset crossed with a set of corruption
// operators — and asserts one invariant:
//
//     NO ACKNOWLEDGED RECORD IS EVER SILENTLY LOST.
//     Either it is recovered, or the damage is loud. Never lost while Verify
//     says clean.

using System.Text;
using System.Text.Json;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Tests;

public class DecisionLedgerMangledSeparatorTests
{
    private static string NewLedgerPath() =>
        Path.Combine(Path.GetTempPath(), "ledger-mangled-" + Guid.NewGuid().ToString("N") + ".jsonl");

    /// <summary>Write ids through the real writer and hand back the on-disk lines.</summary>
    private static string[] Seed(string path, params string[] itemIds)
    {
        using (var ledger = new DecisionLedger(path))
        {
            foreach (var id in itemIds)
                ledger.Append(id, DecisionLedger.DecisionExclude, "reason-" + id);
        }
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        Assert.Equal(itemIds.Length, lines.Length);
        return lines;
    }

    // ── The blocker: a MANGLED separator, not a lost one ──────────────────────

    /// <summary>
    /// Every byte a crash can plausibly leave where a '\n' used to be. NUL is the
    /// classic filesystem artifact (an allocated block the crash never wrote, so
    /// the filesystem hands back zeroes); the rest cover a stray ASCII byte, a
    /// byte that is not valid UTF-8 at all, half a truncated UTF-8 sequence, a
    /// JSON-significant byte, the C0 whitespace JSON does *not* accept, and the
    /// two Unicode separators editors and log shippers like to introduce.
    /// The empty case is the control: the separator that was merely LOST, which
    /// the previous round already handled and which must keep working.
    /// </summary>
    public static TheoryData<string, byte[], bool> GlueBytes()
    {
        var glues = new (string Name, byte[] Bytes)[]
        {
            ("lost (control)", Array.Empty<byte>()),
            ("NUL", new byte[] { 0x00 }),
            ("NUL x4096", Enumerable.Repeat((byte)0x00, 4096).ToArray()),
            ("ascii X", new byte[] { (byte)'X' }),
            ("0xFF", new byte[] { 0xFF }),
            ("half UTF-8", new byte[] { 0xE2, 0x82 }),
            ("comma", new byte[] { (byte)',' }),
            ("VT", new byte[] { 0x0B }),
            ("FF", new byte[] { 0x0C }),
            ("U+2028", new byte[] { 0xE2, 0x80, 0xA8 }),
            ("NBSP", new byte[] { 0xC2, 0xA0 }),
            ("CR only", new byte[] { (byte)'\r' }),
        };
        var data = new TheoryData<string, byte[], bool>();
        foreach (var (name, bytes) in glues)
        {
            data.Add(name, bytes, true);
            data.Add(name, bytes, false);
        }
        return data;
    }

    /// <summary>
    /// THE PROOF SHAPE. Ten flushed, acknowledged records; the separators after
    /// record 2 onwards are replaced by <paramref name="glue"/>, so records 3..9
    /// all land on the file's LAST line — which is exactly where a crash puts the
    /// damage, and the only place the loss is silent (on an interior line a later
    /// record advances the committed mark and ReadFile correctly refuses the
    /// file).
    /// <para>
    /// Pre-fix, observed: bytes 2734 → 1098, records 10 → 4, seqs [0,1,2,3],
    /// ZZSENTINEL3..9 gone from the file, and ReadFile + Verify reporting
    /// Valid=True over the hole while the next run re-used seq 3. Every one of
    /// these cases reproduced it except the "lost" control.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(GlueBytes))]
    public void AMangledSeparator_LosesNoAcknowledgedRecord(string name, byte[] glue, bool terminated)
    {
        var path = NewLedgerPath();
        try
        {
            var ids = Enumerable.Range(0, 10).Select(i => "SENT" + i).ToArray();
            var lines = Seed(path, ids);

            var buf = new MemoryStream();
            void W(string s) => buf.Write(Encoding.UTF8.GetBytes(s));
            for (var i = 0; i < 3; i++)
            {
                W(lines[i]);
                buf.WriteByte((byte)'\n');
            }
            for (var i = 3; i < lines.Length; i++)
            {
                W(lines[i]);
                if (i < lines.Length - 1)
                    buf.Write(glue);
            }
            if (terminated)
                buf.WriteByte((byte)'\n');
            var damaged = buf.ToArray();
            File.WriteAllBytes(path, damaged);

            DecisionLedgerEntry appended;
            using (var second = new DecisionLedger(path))
                appended = second.Append("AFTER", DecisionLedger.DecisionExclude, "r");

            var after = File.ReadAllText(path);
            foreach (var id in ids)
                Assert.Contains($"\"ItemId\":\"{id}\"", after);

            // The seq must resume from the TRUE tail. Pre-fix it resumed from 3.
            Assert.Equal(10, appended.Seq);

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(ids.Append("AFTER").ToArray(), all.Select(e => e.ItemId).ToArray());
            Assert.Equal(Enumerable.Range(0, 11).Select(i => (long)i).ToArray(),
                         all.Select(e => e.Seq).ToArray());
            var verification = DecisionLedger.Verify(all);
            Assert.True(verification.Valid, $"{name}/{terminated}: {verification.Detail}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The same damage on an INTERIOR line. Nothing may be truncated here either,
    /// and every record must still be reachable — the pre-fix code did not lose
    /// records in this position, but only because a later line moved the
    /// committed mark past the damage by accident, so it is pinned deliberately.
    /// </summary>
    [Theory]
    [MemberData(nameof(GlueBytes))]
    public void AMangledSeparator_OnAnInteriorLine_LosesNoAcknowledgedRecord(
        string name, byte[] glue, bool terminated)
    {
        _ = terminated;
        var path = NewLedgerPath();
        try
        {
            var ids = new[] { "A0", "A1", "A2", "A3", "A4" };
            var lines = Seed(path, ids);

            var buf = new MemoryStream();
            void W(string s) => buf.Write(Encoding.UTF8.GetBytes(s));
            W(lines[0]);
            buf.WriteByte((byte)'\n');
            W(lines[1]);
            buf.Write(glue);              // damaged interior boundary
            W(lines[2]);
            buf.WriteByte((byte)'\n');
            W(lines[3]);
            buf.WriteByte((byte)'\n');
            W(lines[4]);
            buf.WriteByte((byte)'\n');
            File.WriteAllBytes(path, buf.ToArray());
            var damagedText = File.ReadAllText(path);

            using (var second = new DecisionLedger(path))
                Assert.Equal(5, second.Append("A5", DecisionLedger.DecisionExclude, "r").Seq);

            Assert.StartsWith(damagedText, File.ReadAllText(path));

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(ids.Append("A5").ToArray(), all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid, name);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// A whole record's separator AND its neighbours destroyed at once — a
    /// zero-filled block swallowing the boundary and a chunk of the records
    /// either side. Nothing may be truncated, and because the block ate a record
    /// outright the result must be LOUD: either ReadFile refuses the file or the
    /// chain it returns fails Verify. What it must never be is a shorter file
    /// with a chain that verifies clean over the hole.
    /// </summary>
    [Fact]
    public void AZeroFilledBlockThatEatsAWholeRecord_IsLoud_NotASilentHole()
    {
        var path = NewLedgerPath();
        try
        {
            var ids = new[] { "B0", "B1", "B2", "B3", "B4" };
            var lines = Seed(path, ids);
            var bytes = File.ReadAllBytes(path);

            // Zero-fill from the middle of record 2 to the middle of record 3.
            var start = lines[0].Length + 1 + lines[1].Length + 1 + (lines[2].Length / 2);
            var end = lines[0].Length + 1 + lines[1].Length + 1 + lines[2].Length + 1
                      + lines[3].Length + 1 + (lines[4].Length / 2);
            for (var i = start; i < end && i < bytes.Length; i++)
                bytes[i] = 0;
            File.WriteAllBytes(path, bytes);
            var damagedLength = bytes.Length;

            using (var second = new DecisionLedger(path))
                second.Append("B5", DecisionLedger.DecisionExclude, "r");

            // Nothing was deleted: resume may only ever grow the file here.
            Assert.True(new FileInfo(path).Length >= damagedLength,
                        "resume truncated bytes that were damage, not an interrupted write");

            // Records 0 and 1 are untouched, so they must still be in the file.
            var text = File.ReadAllText(path);
            Assert.Contains("\"ItemId\":\"B0\"", text);
            Assert.Contains("\"ItemId\":\"B1\"", text);

            // And the loss of B2/B3 must reach the auditor by one channel or the
            // other — never as a clean read.
            try
            {
                var all = DecisionLedger.ReadFile(path);
                Assert.False(DecisionLedger.Verify(all).Valid,
                             "records were destroyed but ReadFile returned a chain that verified clean");
            }
            catch (JsonException)
            {
                // Refused outright. Equally loud.
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Damage inside the LAST record's body, with nothing behind it. That record
    /// is genuinely unrecoverable — but it must not be silently deleted, because
    /// deleting it leaves a prefix that verifies perfectly over the hole. The
    /// bytes stay, and ReadFile refuses the file: refusing is the only channel
    /// left, since there is no later record for a seq gap to appear in.
    /// </summary>
    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0xFF)]
    [InlineData((byte)'\v')]
    public void DamageInsideTheFinalRecord_IsPreservedAndRefused_NeverSilentlyDropped(byte bad)
    {
        var path = NewLedgerPath();
        try
        {
            var lines = Seed(path, "C0", "C1", "C2");
            var bytes = File.ReadAllBytes(path);
            // Land the bad byte inside the last record's ItemId value.
            var lastStart = lines[0].Length + 1 + lines[1].Length + 1;
            var offset = lastStart + lines[2].IndexOf("\"ItemId\":\"C2", StringComparison.Ordinal) + 12;
            bytes[offset] = bad;
            File.WriteAllBytes(path, bytes);
            var damagedLength = bytes.Length;

            using (var second = new DecisionLedger(path))
                second.Append("C3", DecisionLedger.DecisionExclude, "r");

            Assert.True(new FileInfo(path).Length >= damagedLength,
                        "the damaged final record was truncated away — silent evidence loss");
            Assert.Throws<JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// A file whose ONLY content is damaged bytes still must not be silently
    /// emptied, and must still be refused by the auditor's reader.
    /// </summary>
    [Fact]
    public void AFileOfNothingButDamagedBytes_IsKept_AndRefused()
    {
        var path = NewLedgerPath();
        try
        {
            File.WriteAllBytes(path, new byte[] { (byte)'{', 0x00, 0x00, (byte)'"', (byte)'a' });
            using (var ledger = new DecisionLedger(path))
                Assert.Equal(0, ledger.Append("D0", DecisionLedger.DecisionExclude, "r").Seq);

            var text = File.ReadAllText(path);
            Assert.StartsWith("{", text);
            Assert.Contains("\0", text);   // the damaged bytes themselves survived
            Assert.Throws<JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The counterweight: an INTERRUPTED WRITE — an incomplete JSON value — is
    /// still discarded, whatever byte it stops on. If the "keep damaged bytes"
    /// rule leaked into this case, every crash-tail would become permanent
    /// interior corruption and one crash would brick the file for life (the exact
    /// failure round 3 fixed). Swept over every truncation point of a real
    /// record, so the boundary between the two rules is tested, not just its
    /// middle.
    /// </summary>
    [Fact]
    public void EveryTruncationPointOfATornFinalRecord_IsStillDiscarded()
    {
        var lines = Array.Empty<string>();
        var template = NewLedgerPath();
        try
        {
            lines = Seed(template, "E0", "E1", "E2");
        }
        finally
        {
            try { File.Delete(template); } catch { }
        }

        var prefix = lines[0] + "\n" + lines[1] + "\n";
        var failures = new List<string>();
        // cut = 0 is "no fragment at all"; cut = full length is a whole record
        // (a newline-boundary tear, which is KEPT), so stop one short of it.
        for (var cut = 1; cut < lines[2].Length; cut++)
        {
            var path = NewLedgerPath();
            try
            {
                File.WriteAllText(path, prefix + lines[2][..cut]);
                using (var second = new DecisionLedger(path))
                    second.Append("E3", DecisionLedger.DecisionExclude, "r");

                var all = DecisionLedger.ReadFile(path);
                var ids = all.Select(e => e.ItemId).ToArray();
                if (!DecisionLedger.Verify(all).Valid || ids.Length != 3 || ids[^1] != "E3")
                    failures.Add($"cut={cut}: [{string.Join(",", ids)}]");
            }
            catch (Exception exc)
            {
                failures.Add($"cut={cut}: {exc.GetType().Name}");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures.Take(10)));
    }

    /// <summary>
    /// THE SCALE BOUNDARY, in both directions. A zero-filled block does not
    /// mangle one separator, it mangles every separator it covers — so a real
    /// crash can put hundreds of records on ONE line with a destroyed boundary
    /// between each pair. Resynchronising has to keep going across all of them,
    /// not just the first: stopping early would leave the rest behind the
    /// committed mark, which is the original blocker again at a different size.
    /// <para>
    /// It is also the cost question. Resync costs one parse ATTEMPT per candidate
    /// '{', so this pins that a heavily damaged line resumes in bounded time
    /// rather than hanging every crawl that opens the ledger.
    /// </para>
    /// </summary>
    [Fact]
    public void HundredsOfMangledSeparatorsOnOneLine_AreAllResynchronised()
    {
        var path = NewLedgerPath();
        try
        {
            var ids = Enumerable.Range(0, 400).Select(i => "N" + i).ToArray();
            var lines = Seed(path, ids);

            var buf = new MemoryStream();
            for (var i = 0; i < lines.Length; i++)
            {
                buf.Write(Encoding.UTF8.GetBytes(lines[i]));
                if (i < lines.Length - 1)
                    buf.WriteByte(0x00);      // every separator destroyed
            }
            buf.WriteByte((byte)'\n');
            File.WriteAllBytes(path, buf.ToArray());

            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var second = new DecisionLedger(path))
            {
                Assert.Equal(400, second.ResumedRecordCount);
                Assert.Equal(399, second.ResumedDamage.ResyncedRegions);
                Assert.Equal(400, second.Append("N400", DecisionLedger.DecisionExclude, "r").Seq);
            }
            clock.Stop();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30),
                        $"resume took {clock.Elapsed} on a heavily damaged line");

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(401, all.Count);
            Assert.True(DecisionLedger.Verify(all).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The same pressure with no records to find: a line of hundreds of thousands
    /// of record-SHAPED prefixes, which is the worst case for a scan that retries
    /// at every candidate. Must not hang, and must not cost the real records in
    /// front of it.
    /// </summary>
    [Fact]
    public void APathologicalLineOfRecordShapedPrefixes_DoesNotHangTheResume()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = Seed(path, "O0", "O1");
            var poison = string.Concat(Enumerable.Repeat("{\"Seq\":1,\"ItemId\":\"", 200_000));
            File.WriteAllText(path, lines[0] + "\n" + lines[1] + "\n" + poison);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var second = new DecisionLedger(path))
                Assert.Equal(2, second.Append("O2", DecisionLedger.DecisionExclude, "r").Seq);
            clock.Stop();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30),
                        $"resume took {clock.Elapsed} on a pathological line");

            var text = File.ReadAllText(path);
            Assert.Contains("\"ItemId\":\"O0\"", text);
            Assert.Contains("\"ItemId\":\"O1\"", text);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── The systematic sweep ─────────────────────────────────────────────────

    private sealed record Corruption(string Name, Func<byte[], int, byte[]?> Apply, int Width);

    /// <summary>
    /// The corruption operators. Between them they cover the ways bytes on disk
    /// actually go wrong: overwritten (with several byte classes, because JSON
    /// treats them differently), lost, gained, stopped short, and zero-filled a
    /// block at a time.
    /// </summary>
    public static TheoryData<string> Operators() =>
        new("replace-NUL", "replace-X", "replace-FF", "replace-comma", "replace-brace",
            "delete", "insert-NUL", "truncate", "zerofill-16");

    private static Corruption OperatorFor(string name) => name switch
    {
        "replace-NUL" => new(name, (b, i) => Replace(b, i, 0x00), 1),
        "replace-X" => new(name, (b, i) => Replace(b, i, (byte)'X'), 1),
        "replace-FF" => new(name, (b, i) => Replace(b, i, 0xFF), 1),
        "replace-comma" => new(name, (b, i) => Replace(b, i, (byte)','), 1),
        "replace-brace" => new(name, (b, i) => Replace(b, i, (byte)'{'), 1),
        "delete" => new(name, (b, i) => b.Take(i).Concat(b.Skip(i + 1)).ToArray(), 1),
        "insert-NUL" => new(name, (b, i) => b.Take(i).Append((byte)0x00).Concat(b.Skip(i)).ToArray(), 0),
        "truncate" => new(name, (b, i) => b.Take(i).ToArray(), int.MaxValue),
        "zerofill-16" => new(name, (b, i) => ZeroFill(b, i, 16), 16),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static byte[] Replace(byte[] bytes, int i, byte with)
    {
        var copy = (byte[])bytes.Clone();
        copy[i] = with;
        return copy;
    }

    private static byte[] ZeroFill(byte[] bytes, int i, int width)
    {
        var copy = (byte[])bytes.Clone();
        for (var k = i; k < i + width && k < copy.Length; k++)
            copy[k] = 0;
        return copy;
    }

    /// <summary>
    /// THE CLASS-LEVEL TEST. For every byte offset of a real ledger, crossed with
    /// every corruption operator, resume the file, append a record, and assert
    /// the one invariant that matters:
    /// <para>
    /// EITHER every record the damage did not physically touch is still returned
    /// by ReadFile, OR the damage is loud — ReadFile throws, or Verify reports a
    /// break. What must never happen is the third outcome: records gone from the
    /// file and a chain that verifies clean over the hole. That third outcome is
    /// what every shipped tear shape has been, and it is the only one this
    /// asserts against, so a shape nobody has thought of yet still fails here.
    /// </para>
    /// <para>
    /// Records the damage landed INSIDE are excluded from the "must be returned"
    /// set, and deliberately so: bytes that were overwritten cannot be conjured
    /// back, and a write truncated mid-record is indistinguishable from one that
    /// was still in flight. The invariant is about SILENCE, not omniscience.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Operators))]
    public void CorruptionSweep_NeverSilentlyLosesAnAcknowledgedRecord(string operatorName)
    {
        var op = OperatorFor(operatorName);
        var ids = new[] { "S0", "S1", "S2", "S3" };

        byte[] clean;
        var ranges = new List<(string Id, int Start, int End)>();
        var template = NewLedgerPath();
        try
        {
            var lines = Seed(template, ids);
            clean = File.ReadAllBytes(template);
            var at = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                ranges.Add((ids[i], at, at + lines[i].Length));
                at += lines[i].Length + 1;      // + '\n'
            }
        }
        finally
        {
            try { File.Delete(template); } catch { }
        }

        var failures = new List<string>();
        var cases = 0;
        var loud = 0;
        for (var offset = 0; offset < clean.Length; offset++)
        {
            var damaged = op.Apply(clean, offset);
            if (damaged is null || damaged.Length == 0)
                continue;
            cases++;

            // Records whose own bytes the damage did not touch. Everything else
            // is fair game to lose — but never silently.
            var damageEnd = op.Width == int.MaxValue
                ? clean.Length
                : offset + op.Width;
            var untouched = ranges
                .Where(r => r.End <= offset || r.Start >= damageEnd)
                .Select(r => r.Id)
                .ToArray();

            var path = NewLedgerPath();
            try
            {
                File.WriteAllBytes(path, damaged);
                using (var second = new DecisionLedger(path))
                    second.Append("AFTER", DecisionLedger.DecisionExclude, "r");

                List<DecisionLedgerEntry> all;
                try
                {
                    all = DecisionLedger.ReadFile(path);
                }
                catch (JsonException)
                {
                    loud++;
                    continue;   // LOUD: the auditor is told. Invariant holds.
                }

                if (!DecisionLedger.Verify(all).Valid)
                {
                    loud++;
                    continue;   // LOUD: the chain reports the break. Invariant holds.
                }

                // The chain says clean, so nothing untouched may be missing.
                var present = all.Select(e => e.ItemId).ToHashSet(StringComparer.Ordinal);
                var lost = untouched.Where(id => !present.Contains(id)).ToArray();
                if (lost.Length > 0)
                    failures.Add($"offset={offset} lost=[{string.Join(",", lost)}] "
                                 + $"kept=[{string.Join(",", present)}]");
            }
            catch (Exception exc)
            {
                failures.Add($"offset={offset} threw {exc.GetType().Name}: {exc.Message}");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        // The sweep must not pass by not running. A 4-record ledger is ~1kB, so
        // anything under a few hundred offsets means the generator broke.
        Assert.True(cases > 500, $"{op.Name}: only {cases} offsets exercised");

        // And it must not pass trivially. Every DESTRUCTIVE operator has to
        // surface damage loudly somewhere — if none ever did, the loud channels
        // are not wired up and "either recovered or loud" is being satisfied by
        // recovering everything, including things that cannot be recovered.
        // 'truncate' is the deliberate exception: cutting a file short is
        // exactly an interrupted write, so it is ALWAYS cleanly recoverable and
        // must never be loud. Asserting that direction pins the other half of
        // the rule — that the preserve-damage branch has not swallowed the
        // ordinary crash-tail and started bricking files.
        if (op.Name == "truncate")
            Assert.True(loud == 0, $"truncate was surfaced as damage at {loud} offset(s)");
        else
            Assert.True(loud > 0, $"{op.Name}: no offset was ever surfaced loudly");

        Assert.True(
            failures.Count == 0,
            $"{op.Name}: {failures.Count} silent-loss offset(s) of {cases}: "
            + string.Join(" | ", failures.Take(8)));
    }

    /// <summary>
    /// The sweep's own control. If the corruption operators were not actually
    /// reaching the interesting bytes, the sweep would pass vacuously, so this
    /// pins that the CLEAN file resumes cleanly and that the operators really do
    /// produce files that differ from it.
    /// </summary>
    [Fact]
    public void CorruptionSweep_OperatorsActuallyDamageTheFile()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "S0", "S1", "S2", "S3");
            var clean = File.ReadAllBytes(path);
            foreach (var name in new[] { "replace-NUL", "delete", "insert-NUL", "truncate", "zerofill-16" })
            {
                var op = OperatorFor(name);
                var mid = clean.Length / 2;
                var damaged = op.Apply(clean, mid);
                Assert.NotNull(damaged);
                Assert.False(clean.AsSpan().SequenceEqual(damaged), name);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── Per-member coverage of the record contract ───────────────────────────

    private const string GoodSeq = "\"Seq\":99";
    private const string GoodItemId = "\"ItemId\":\"ghost\"";
    private const string GoodDecision = "\"Decision\":\"exclude\"";
    private const string GoodReason = "\"Reason\":\"r\"";
    private const string GoodTimestamp = "\"Timestamp\":\"t\"";
    private const string GoodPrevHash = "\"PrevHash\":\"" + DecisionLedger.GenesisHash + "\"";
    private const string GoodHash = "\"Hash\":\"" + DecisionLedger.GenesisHash + "\"";

    private static readonly string[] AllMembers =
        { GoodSeq, GoodItemId, GoodDecision, GoodReason, GoodTimestamp, GoodPrevHash, GoodHash };

    /// <summary>
    /// A JSON object carrying every member EXCEPT the named one. Seq is included:
    /// it is a non-nullable long, so its absence is invisible to a null check and
    /// was the one member the shipped guard did not cover.
    /// </summary>
    public static TheoryData<string, string> MemberOmissions()
    {
        var data = new TheoryData<string, string>();
        foreach (var member in AllMembers)
        {
            var name = member[1..member.IndexOf('"', 1)];
            data.Add(name, "{" + string.Join(",", AllMembers.Where(m => m != member)) + "}");
        }
        return data;
    }

    /// <summary>
    /// A JSON object carrying every member, with the named one explicitly null.
    /// Seq has no null case (a long cannot be null), hence the separate theory.
    /// </summary>
    public static TheoryData<string, string> MemberNulls()
    {
        var data = new TheoryData<string, string>();
        foreach (var member in AllMembers.Where(m => m != GoodSeq))
        {
            var name = member[1..member.IndexOf('"', 1)];
            var nulled = AllMembers.Select(m => m == member ? $"\"{name}\":null" : m);
            data.Add(name, "{" + string.Join(",", nulled) + "}");
        }
        return data;
    }

    /// <summary>
    /// EVERY member of the record contract, checked ON ITS OWN. The previous
    /// round's guard was covered only collectively: neutering the single
    /// 'entry.ItemId is not null' clause left all 42 DecisionLedger tests green,
    /// which means six of the seven members had no coverage at all.
    /// <para>
    /// Each case appends a near-record missing (or nulling) exactly one member as
    /// the FINAL line, then asserts the resume did not anchor on it. Seq 99 is
    /// the tell: if the guard for that member is removed the near-record becomes
    /// the anchor, and the next append takes seq 100 and links to its hash
    /// instead of the real tail's. Both are asserted, so removing ANY single
    /// clause — or the [JsonRequired] on any single member — turns exactly the
    /// case for that member red.
    /// </para>
    /// <para>
    /// The near-record is also COMPLETE JSON, which an interrupted write cannot
    /// produce, so it is kept on disk rather than truncated and the file is
    /// refused from then on. Both are asserted: the guard tell (seq/prevHash) is
    /// what makes each member's case red on its own, the preservation and the
    /// refusal are what make the damage non-silent.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(MemberOmissions))]
    [MemberData(nameof(MemberNulls))]
    public void ALineMissingExactlyOneMember_IsNotARecord_AndNeverAnchorsTheChain(
        string member, string nearRecord)
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "F0", "F1", "F2");
            var realTail = DecisionLedger.ReadFile(path)[^1];
            File.AppendAllText(path, nearRecord + "\n");

            DecisionLedgerEntry appended;
            using (var second = new DecisionLedger(path))
                appended = second.Append("F3", DecisionLedger.DecisionQuarantine, "malware-scan");

            Assert.Equal(3, appended.Seq);
            Assert.Equal(realTail.Hash, appended.PrevHash);

            Assert.True(
                File.ReadAllText(path).Contains(nearRecord, StringComparison.Ordinal),
                $"the near-record missing/nulling '{member}' was truncated away instead of kept");
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The complete object — every member present and non-null — MUST still be
    /// accepted. Without this the theories above would pass just as well if the
    /// contract rejected everything.
    /// </summary>
    [Fact]
    public void TheCompleteObject_IsStillAcceptedAsARecord()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "G0");
            var tail = DecisionLedger.ReadFile(path)[0];
            var ts = "2026-01-01T00:00:00.0000000+00:00";
            var hash = DecisionLedger.ComputeHash(1, "G1", "exclude", "r", ts, tail.Hash);
            File.AppendAllText(
                path,
                "{" + string.Join(",",
                    "\"Seq\":1",
                    "\"ItemId\":\"G1\"",
                    "\"Decision\":\"exclude\"",
                    "\"Reason\":\"r\"",
                    $"\"Timestamp\":\"{ts}\"",
                    $"\"PrevHash\":\"{tail.Hash}\"",
                    $"\"Hash\":\"{hash}\"") + "}\n");

            using var second = new DecisionLedger(path);
            Assert.Equal(2, second.Append("G2", DecisionLedger.DecisionExclude, "r").Seq);
            Assert.True(DecisionLedger.Verify(DecisionLedger.ReadFile(path)).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The specific proof for Seq, which is not a null-checkable member at all.
    /// Observed pre-fix: a final line with no Seq became the anchor with Seq=0,
    /// NextSeq reset to 1, and the next run RE-ISSUED seqs that live records
    /// already held — "Verify: Valid=False, seq out of order at index 3 (got 0,
    /// expected 3)".
    /// </summary>
    [Fact]
    public void ALineWithNoSeq_DoesNotResetTheChainAndReIssueLiveSeqs()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "H0", "H1", "H2");
            File.AppendAllText(
                path,
                """{"ItemId":"FAKE","Decision":"exclude","Reason":"r","Timestamp":"t","PrevHash":"p","Hash":"h"}"""
                + "\n");

            using (var second = new DecisionLedger(path))
            {
                Assert.Equal(3, second.Append("H3", DecisionLedger.DecisionExclude, "r").Seq);
                Assert.Equal(4, second.Append("H4", DecisionLedger.DecisionExclude, "r").Seq);
            }

            // The FAKE line is complete JSON, so it is preserved as evidence
            // (not truncated) and the file is refused rather than read clean.
            Assert.Contains("FAKE", File.ReadAllText(path));
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── The two suspicions the verifier could not demonstrate ────────────────

    /// <summary>
    /// UTF-8 BOM. ReadFile goes through File.ReadLines, which STRIPS a BOM;
    /// ResumeTail reads raw bytes and did not, so the two disagreed about what
    /// record 1 was. The disagreement lost nothing on its own (the byte scan
    /// still reached record 2, so the resume anchor was right), which is why it
    /// could not be demonstrated as loss — but it made the ledger report a record
    /// count one short of the truth and, once the scan started resynchronising,
    /// would have flagged the BOM itself as destroyed bytes. Pinned as an
    /// agreement invariant between the two readers, which is the property that
    /// actually matters.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AUtf8Bom_DoesNotMakeTheTwoReadersDisagreeAboutRecordOne(bool terminated)
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "I0", "I1");
            var body = File.ReadAllBytes(path);
            if (!terminated)
                body = body.Take(body.Length - 1).ToArray();
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray());

            using (var second = new DecisionLedger(path))
            {
                Assert.Equal(2, second.ResumedRecordCount);
                Assert.Equal(DecisionLedger.ReadFile(path).Count, second.ResumedRecordCount);
                // And a BOM must not be MISTAKEN for damage either. Once the
                // scan resynchronises, an unrecognised BOM is stepped over like
                // any destroyed byte — the records still come back, so nothing is
                // lost, but the ledger cries corruption over a file that is
                // merely encoded the way Windows tooling encodes files. A false
                // alarm on a tamper-evidence signal is its own defect.
                Assert.True(second.ResumedDamage.IsClean,
                            $"a UTF-8 BOM was reported as damage: {second.ResumedDamage}");
                Assert.Equal(2, second.Append("I2", DecisionLedger.DecisionExclude, "r").Seq);
            }

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "I0", "I1", "I2" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
            // The BOM is left where it is — it is not damage and not ours to edit.
            Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// A BOM-only file, and a BOM followed by a torn fragment: the BOM must not
    /// be mistaken for a record, nor stop the fragment being trimmed.
    /// </summary>
    [Fact]
    public void ABomOnlyFile_StartsAFreshChain()
    {
        var path = NewLedgerPath();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF });
            using (var ledger = new DecisionLedger(path))
            {
                Assert.Equal(0, ledger.ResumedRecordCount);
                Assert.Equal(0, ledger.Append("J0", DecisionLedger.DecisionExclude, "r").Seq);
            }
            Assert.True(DecisionLedger.Verify(DecisionLedger.ReadFile(path)).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// CRLF. StreamWriter.WriteLine emits Environment.NewLine, so a ledger
    /// written on Windows is CRLF while the resume scan splits only on '\n'. The
    /// mixed-platform round trip — written on one, resumed on the other, then
    /// appended to in the local convention so the file ends up MIXED — was
    /// untested. Swept across the tear shapes, because the '\r' sits exactly
    /// where the shapes are decided.
    /// </summary>
    [Theory]
    [InlineData("crlf-clean")]
    [InlineData("crlf-no-final-terminator")]
    [InlineData("crlf-lone-cr-terminator")]
    [InlineData("crlf-torn-fragment")]
    [InlineData("crlf-mangled-separator")]
    public void ACrlfLedgerWrittenOnWindows_ResumesOnLinuxWithoutLosingRecords(string shape)
    {
        var path = NewLedgerPath();
        try
        {
            var lines = Seed(path, "K0", "K1", "K2");
            var crlf = string.Join("\r\n", lines) + "\r\n";
            switch (shape)
            {
                case "crlf-clean":
                    File.WriteAllText(path, crlf);
                    break;
                case "crlf-no-final-terminator":
                    File.WriteAllText(path, crlf[..^2]);
                    break;
                case "crlf-lone-cr-terminator":
                    File.WriteAllText(path, crlf[..^1]);      // ends "...}\r"
                    break;
                case "crlf-torn-fragment":
                    File.WriteAllText(path, crlf + """{"Seq":3,"ItemId":"K3","Deci""");
                    break;
                case "crlf-mangled-separator":
                    // The '\n' of the middle CRLF destroyed, leaving "...}\r\0{...".
                    var bytes = Encoding.UTF8.GetBytes(crlf);
                    var boundary = lines[0].Length + 2 + lines[1].Length + 1;
                    bytes[boundary] = 0x00;
                    File.WriteAllBytes(path, bytes);
                    break;
            }

            using (var second = new DecisionLedger(path))
            {
                Assert.Equal(3, second.ResumedRecordCount);
                Assert.Equal(3, second.Append("K3", DecisionLedger.DecisionExclude, "r").Seq);
            }

            var all = DecisionLedger.ReadFile(path);
            Assert.Equal(new[] { "K0", "K1", "K2", "K3" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid, shape);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── Append-smuggling: threat-model position, made observable ─────────────

    /// <summary>
    /// A forged but correctly chained record glued onto the END of an existing
    /// line — no new physical line, no existing byte modified — is still ACCEPTED
    /// as a record, and deliberately so. Rejecting glued records is precisely
    /// what destroyed acknowledged evidence in rounds 3 and 4, and the chain
    /// never defended against append access in the first place: an attacker who
    /// can append can chain a forgery onto a fresh line just as easily, which is
    /// the documented residual that off-box / WORM shipping covers.
    /// <para>
    /// What the glued form additionally bought was INVISIBILITY to a line-count
    /// or tail-based monitor, since it adds no line. That part is now closed:
    /// ReadFile reports the glued line as physical damage whether or not the
    /// chain verifies, so a monitor watching LedgerFileDamage sees it.
    /// </para>
    /// </summary>
    [Fact]
    public void AForgedRecordGluedOntoALine_VerifiesClean_ButIsReportedAsPhysicalDamage()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "L0", "L1", "L2");
            var tail = DecisionLedger.ReadFile(path, out var cleanDamage)[^1];
            Assert.True(cleanDamage.IsClean, "an undamaged ledger reported damage");

            var ts = "2026-01-01T00:00:00.0000000+00:00";
            var hash = DecisionLedger.ComputeHash(3, "FORGED", "exclude", "forged", ts, tail.Hash);
            var forged = JsonSerializer.Serialize(
                new DecisionLedgerEntry(3, "FORGED", "exclude", "forged", ts, tail.Hash, hash));

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            var lineCountBefore = lines.Count;
            lines[^1] += forged;                       // glued: no new line
            File.WriteAllText(path, string.Join('\n', lines) + "\n");
            Assert.Equal(lineCountBefore, File.ReadAllLines(path).Count(l => l.Length > 0));

            var all = DecisionLedger.ReadFile(path, out var damage);
            // Accepted and chaining — the honest position, not a claim of defence.
            Assert.Contains(all, e => e.ItemId == "FORGED");
            Assert.True(DecisionLedger.Verify(all).Valid);
            // ...but no longer invisible.
            Assert.False(damage.IsClean);
            Assert.Equal(1, damage.GluedLines);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The damage report must also flag a MANGLED boundary, which is the case a
    /// line-count monitor cannot see either — the records are all there and the
    /// chain verifies, so nothing else in the API would say the file is broken.
    /// </summary>
    [Fact]
    public void ReadFile_ReportsAResynchronisedBoundary_EvenWhenTheChainVerifies()
    {
        var path = NewLedgerPath();
        try
        {
            var lines = Seed(path, "M0", "M1", "M2");
            var buf = new MemoryStream();
            buf.Write(Encoding.UTF8.GetBytes(lines[0]));
            buf.WriteByte((byte)'\n');
            buf.Write(Encoding.UTF8.GetBytes(lines[1]));
            buf.WriteByte(0x00);                       // mangled boundary
            buf.Write(Encoding.UTF8.GetBytes(lines[2]));
            buf.WriteByte((byte)'\n');
            File.WriteAllBytes(path, buf.ToArray());

            var all = DecisionLedger.ReadFile(path, out var damage);
            Assert.Equal(new[] { "M0", "M1", "M2" }, all.Select(e => e.ItemId).ToArray());
            Assert.True(DecisionLedger.Verify(all).Valid);
            Assert.False(damage.IsClean);
            Assert.Equal(1, damage.ResyncedRegions);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
