// DecisionLedgerMangledFinalRecordTests.cs
// ----------------------------------------
// Round 9 of the decision-ledger durability work. Three narrow defects, each
// reproduced with a probe before it was fixed:
//
//   BLOCKER — MANGLED FINAL RECORD, DESTROYED SILENTLY. A single overwritten
//   byte inside the LAST record's key names (observed: 'S' -> 'X' at
//   lastLineStart+2, so "Seq" became "Xeq") leaves the line COMPLETE, VALID
//   JSON that simply is not a decision record. ClassifyResidue called that
//   shape "NotARecord — junk, safely discardable", so:
//       ReadFile n=1 items=[item-A] Verify.Valid=True damage.IsClean=True
//       resume  BaseSeq=1 file 545 -> 536 bytes; damaged bytes present: False
//       final   n=2 items=[item-A,item-C] seqs=[0,1] Verify.Valid=True
//               item-B present? False
//   i.e. the record was dropped by the auditor, TRUNCATED off disk by the next
//   resume, and its seq re-issued to a different item — with every signal
//   reporting clean. Round 8's reasoning ("damage inside a record shows up as a
//   seq gap from Verify") is true for every record EXCEPT the last one: there
//   is nothing behind the last record, so no gap can ever appear.
//
//   The fix is one classification: an interrupted write can only ever leave an
//   INCOMPLETE JSON value behind (Append serializes one object and flushes it,
//   so every prefix stops mid-value). Trailing bytes that form a COMPLETE JSON
//   value the record contract rejects were therefore overwritten after being
//   flushed, or are foreign content. They are damage: kept, and loud.
//
//   MAJOR — Seq had NO range check. A line carrying a negative Seq, or
//   long.MaxValue, was accepted as a record and became the resume anchor:
//       Seq=-4            -> BaseSeq=-3,               next appended Seq=-3
//       Seq=long.MaxValue -> BaseSeq=long.MinValue,    next appended Seq=long.MinValue
//   (the second is 'last.Seq + 1' overflowing). Append only ever issues seqs
//   from 0 upward, so neither is a seq this writer produced.
//
//   MINOR — the two readers disagreed about DAMAGE on a file whose records are
//   separated by a lone CR: ReadFile (File.ReadLines, which breaks on CR)
//   reported IsClean=True while ResumeTail (raw byte scan for '\n') reported
//   GluedLines=1. The writer only ever writes '\n', so a lone CR there is an
//   overwritten separator — damage — and ReadFile now splits on the same byte.

using SeismicConnector.Infrastructure;

namespace SeismicConnector.Tests;

public class DecisionLedgerMangledFinalRecordTests
{
    private static string NewLedgerPath() =>
        Path.Combine(Path.GetTempPath(), "ledger-r9-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static void Seed(string path, params string[] itemIds)
    {
        using var ledger = new DecisionLedger(path);
        foreach (var id in itemIds)
            ledger.Append(id, DecisionLedger.DecisionExclude, "reason-" + id);
    }

    private static int LastLineStart(byte[] bytes)
    {
        for (var i = bytes.Length - 2; i >= 0; i--)
            if (bytes[i] == (byte)'\n')
                return i + 1;
        return 0;
    }

    /// <summary>
    /// Every one of the 256 byte values, as theory rows. A byte survives xUnit's
    /// theory serialisation unchanged (it is a number, not an exotic string), and
    /// the sweep below re-derives nothing from the row beyond the value itself.
    /// </summary>
    public static IEnumerable<object[]> AllByteValues() =>
        Enumerable.Range(0, 256).Select(b => new object[] { (byte)b });

    // ── BLOCKER: a mangled FINAL record is never lost silently ───────────────

    /// <summary>
    /// The exact reported repro, end to end: flip ONE byte inside the last
    /// record so "Seq" becomes "Xeq". Every stage must now be loud, and the
    /// damaged bytes must survive on disk.
    /// <para>
    /// This executes the real code path — writer, resume, auditor — over a file
    /// the real writer produced, with one byte changed. It asserts the four
    /// things that were false before the fix: the auditor REFUSES rather than
    /// returning a short clean read, the resume reports damage, the resume does
    /// NOT shrink the file, and the damaged bytes are still there afterwards.
    /// </para>
    /// </summary>
    [Fact]
    public void OneFlippedByteInTheLastRecord_IsNeverLostSilently()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "item-A", "item-B");
            var bytes = File.ReadAllBytes(path);
            bytes[LastLineStart(bytes) + 2] = (byte)'X';       // "Seq" -> "Xeq"
            File.WriteAllBytes(path, bytes);
            var sizeBefore = new FileInfo(path).Length;

            // 1. The auditor refuses the file instead of handing back a short,
            //    clean-looking chain.
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));

            // 2. The resume reports the damage...
            using (var second = new DecisionLedger(path))
            {
                Assert.False(second.ResumedDamage.IsClean);
                Assert.True(second.ResumedDamage.DamagedTail);
                second.Append("item-C", DecisionLedger.DecisionExclude, "r2");
            }

            // 3. ...and did not truncate the damaged bytes off disk.
            Assert.True(new FileInfo(path).Length > sizeBefore);
            Assert.Contains("Xeq", File.ReadAllText(path));

            // 4. The file stays refused, so the hole can never be read as clean.
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Breadth, not one lucky offset: damage EVERY byte of the final record in
    /// turn, with each of the replacement bytes from the reported sweep, and
    /// record which offsets come back SHORT WITHOUT A REFUSAL — the reported
    /// failure mode (45-46 of 275 offsets per replacement byte, scattered right
    /// across the record).
    /// <para>
    /// THE ALPHABET IS ALL 256 BYTE VALUES, AND THAT IS THE POINT. This test
    /// previously swept five replacements (X, 0xff, space, comma, NUL) and
    /// read as exhaustive while it was not: a sixth value, 0x5c — backslash, the
    /// one byte that OPENS a JSON escape — silently destroyed the final record at
    /// offset 262, which the docs then declared safe for a whole round. A sampled
    /// sweep presented as complete is a false claim, so the sample is gone.
    /// </para>
    /// <para>
    /// THE GUARANTEE THIS PINS, AND ITS HONEST LIMIT. A silent drop is now
    /// possible at exactly ONE offset — the record's closing brace, the final
    /// byte — and there only for the four JSON WHITESPACE bytes (0x09, 0x0a,
    /// 0x0d, 0x20). Replacing the brace with whitespace leaves bytes that the
    /// format's own trailing-whitespace trimming reduces to a byte-for-byte
    /// prefix of a write that stopped one byte short of the brace, so no reader
    /// can tell the two apart. Every other byte value at that offset, and every
    /// byte value at all 264 other offsets, is now either recovered intact or
    /// refused. Those numbers are what this test measures, not what it assumes:
    /// the assertion below names the offending offset and byte if the residue
    /// ever grows.
    /// </para>
    /// <para>
    /// Behavioural: it runs the real writer, damages real bytes, and asserts on
    /// what the real reader returns. Reformatting the source cannot satisfy it.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllByteValues))]
    public void DamagingAnyByteOfTheFinalRecord_OnlyEverDropsItAtTheTerminator(byte replacement)
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "s0", "s1", "s2", "s3", "s4", "s5");
            var pristine = File.ReadAllBytes(path);
            Assert.Equal(6, DecisionLedger.ReadFile(path).Count);   // the harness itself is sound

            var start = LastLineStart(pristine);
            var end = pristine.Length - 1;                          // exclude the final '\n'
            var recordLength = end - start;
            var refused = 0;
            var keptAll = 0;
            var droppedAt = new List<int>();

            for (var i = start; i < end; i++)
            {
                var bytes = (byte[])pristine.Clone();
                if (bytes[i] == replacement)
                    continue;
                bytes[i] = replacement;
                File.WriteAllBytes(path, bytes);

                try
                {
                    if (DecisionLedger.ReadFile(path, out _).Count == 6)
                        keptAll++;
                    else
                        droppedAt.Add(i - start);
                }
                catch (System.Text.Json.JsonException)
                {
                    refused++;
                }
            }

            // The ONLY tolerated silent drop: the closing brace (the last byte)
            // replaced by JSON whitespace, which trims back to a genuine torn
            // prefix. Both clauses are asserted, so widening either the offset or
            // the byte alphabet fails here.
            var whitespace = replacement is 0x09 or 0x0a or 0x0d or 0x20;
            Assert.All(droppedAt, offset => Assert.True(
                offset == recordLength - 1 && whitespace,
                $"offset {offset} of {recordLength} damaged with 0x{replacement:x2}: the record was "
                + "dropped and the file was NOT refused, at a position/byte that is not the closing "
                + $"brace overwritten by whitespace. Silently dropped offsets: [{string.Join(",", droppedAt)}]"));

            // The refusal path must actually be exercised, or the assertion
            // above could be satisfied by a reader that refuses nothing and
            // drops nothing because it never notices anything.
            Assert.True(refused > 0, $"0x{replacement:x2}: no damaged offset was refused at all");
            Assert.Equal(recordLength, refused + keptAll + droppedAt.Count
                + (pristine.Skip(start).Take(recordLength).Count(b => b == replacement)));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The discrimination the fix rests on, asserted in both directions on the
    /// SAME file shape. A genuinely interrupted write (an INCOMPLETE JSON value
    /// as the final line) must still be tolerated and truncated — that is the
    /// crash-tail the ledger has always healed — while a COMPLETE JSON value
    /// that is not a record must be kept and refused. Getting this backwards in
    /// either direction is a defect, so both directions are pinned.
    /// </summary>
    [Fact]
    public void AnInterruptedWriteIsStillHealed_ButACompleteNonRecordIsNot()
    {
        var torn = NewLedgerPath();
        var damaged = NewLedgerPath();
        try
        {
            Seed(torn, "t0", "t1");
            var body = File.ReadAllText(torn);
            var lastLine = body[body[..^1].LastIndexOf('\n')..].Trim();

            // INCOMPLETE JSON value: closing brace missing. Torn write.
            File.AppendAllText(torn, lastLine[..^1] + "\n");
            var tornSize = new FileInfo(torn).Length;
            using (var l = new DecisionLedger(torn))
                Assert.True(l.ResumedDamage.IsClean);
            Assert.True(new FileInfo(torn).Length < tornSize, "the torn tail was not truncated");
            Assert.Equal(2, DecisionLedger.ReadFile(torn).Count);

            // COMPLETE JSON value that is not a record: same bytes, plus the
            // brace, plus one mangled key. Damage.
            Seed(damaged, "t0", "t1");
            File.AppendAllText(damaged, lastLine.Replace("\"Seq\"", "\"Xeq\"") + "\n");
            var damagedSize = new FileInfo(damaged).Length;
            using (var l = new DecisionLedger(damaged))
                Assert.False(l.ResumedDamage.IsClean);
            Assert.Equal(damagedSize, new FileInfo(damaged).Length);
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(damaged));
        }
        finally
        {
            try { File.Delete(torn); } catch { }
            try { File.Delete(damaged); } catch { }
        }
    }

    // ── BLOCKER: the ESCAPE class ────────────────────────────────────────────

    /// <summary>
    /// THE REPORTED REPRO, exactly. One 0x5c (backslash) on the LAST HEX
    /// CHARACTER of the final record's Hash value. The backslash opens a JSON
    /// escape, so the closing quote becomes an ESCAPED quote, the Hash string
    /// runs on, and the record parses as an INCOMPLETE JSON value — which is the
    /// discriminator the ledger uses for "interrupted write, safe to truncate".
    /// The damage disguises itself as a torn write.
    /// <para>
    /// Observed before the fix, on a real 3-record ledger:
    /// <code>
    ///   tail before: f8fceb9828"}      tail after: f8fceb982\"}
    ///   AUDITOR ReadFile: 2 records, seqs=[0,1], damage.IsClean=True, Verify.Valid=True
    ///   RESUME: ResumedRecordCount=2 BaseSeq=2 ResumedDamage.IsClean=True
    ///   AFTER one Append: 3 records, seqs=[0,1,2], damage.IsClean=True
    /// </code>
    /// The acknowledged record was gone, its seq reissued to a different record,
    /// and every signal read clean.
    /// </para>
    /// <para>
    /// WHAT IS AND IS NOT FIXED. The destroyed record is still unrecoverable —
    /// its bytes are gone — and the resume anchor still steps back to the last
    /// PARSEABLE record, so the next append does reuse that seq. What changed is
    /// that none of it is silent: the damaged bytes are kept as evidence rather
    /// than truncated away, the resume reports DamagedTail, and ReadFile refuses
    /// the file from then on instead of returning a short chain that verifies
    /// clean. Loud, not lossless, is the guarantee this ledger makes.
    /// </para>
    /// </summary>
    [Fact]
    public void ABackslashOnTheLastHashCharacter_IsNeverLostSilently()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "esc-A", "esc-B", "esc-C");
            var bytes = File.ReadAllBytes(path);
            var start = LastLineStart(bytes);
            var lastHexOffset = bytes.Length - 1 - 1 - 2;   // '\n', '}', '"', then the hex

            // The harness targets what it claims: the byte it is about to damage
            // is the last HEX character of Hash, immediately before the closing
            // quote and brace. (Its numeric record-offset depends on the item-id
            // and reason lengths — it was 262 of 265 in the reported ledger — so
            // the position is pinned structurally rather than by that number.)
            Assert.True(
                Uri.IsHexDigit((char)bytes[lastHexOffset]),
                "the targeted byte is not a hex character");
            Assert.Equal((byte)'"', bytes[lastHexOffset + 1]);
            Assert.Equal((byte)'}', bytes[lastHexOffset + 2]);
            Assert.True(lastHexOffset > start, "the targeted byte is not inside the final record");

            bytes[lastHexOffset] = (byte)'\\';
            File.WriteAllBytes(path, bytes);
            var sizeBefore = new FileInfo(path).Length;

            // The auditor refuses rather than returning a short, clean chain.
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));

            // The resume reports damage and does not truncate the record away.
            using (var resumed = new DecisionLedger(path))
            {
                Assert.False(resumed.ResumedDamage.IsClean);
                Assert.True(resumed.ResumedDamage.DamagedTail);
                Assert.Equal(2, resumed.ResumedRecordCount);
                resumed.Append("esc-D", DecisionLedger.DecisionExclude, "r");
            }

            Assert.True(new FileInfo(path).Length > sizeBefore, "the damaged bytes were truncated away");
            Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The CLASS, not the one byte. A backslash landing anywhere inside either
    /// hash value opens an escape; wherever that escape swallows the rest of the
    /// line the record becomes an incomplete value and used to be truncated. Both
    /// hash members are swept at every offset of their value.
    /// </summary>
    [Fact]
    public void ABackslashAnywhereInAHashValue_IsNeverLostSilently()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "h-A", "h-B");
            var pristine = File.ReadAllBytes(path);
            var text = System.Text.Encoding.UTF8.GetString(pristine);
            var start = LastLineStart(pristine);

            var offsets = new List<int>();
            foreach (var member in new[] { "\"PrevHash\":\"", "\"Hash\":\"" })
            {
                var at = text.IndexOf(member, start, StringComparison.Ordinal);
                Assert.True(at > 0, $"{member} not found in the final record");
                for (var i = 0; i < 64; i++)
                    offsets.Add(at + member.Length + i);
            }
            Assert.Equal(128, offsets.Count);

            var silent = new List<int>();
            foreach (var offset in offsets)
            {
                var bytes = (byte[])pristine.Clone();
                bytes[offset] = (byte)'\\';
                File.WriteAllBytes(path, bytes);
                try
                {
                    if (DecisionLedger.ReadFile(path, out _).Count != 2)
                        silent.Add(offset - start);
                }
                catch (System.Text.Json.JsonException)
                {
                    // Refused — loud, which is the requirement.
                }
            }

            Assert.True(
                silent.Count == 0,
                $"a backslash silently dropped the final record at record-offsets [{string.Join(",", silent)}]");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The other half of the class: DELETING or INSERTING a byte in the final
    /// record. Neither is something an interrupted write can do — it can only
    /// stop early — so neither may end in a silent drop, with the single
    /// exception of deleting the closing brace, which leaves exactly the bytes a
    /// write that stopped one byte short would have left.
    /// </summary>
    [Fact]
    public void DeletingOrInsertingAByteInTheFinalRecord_IsNeverLostSilently()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "d0", "d1", "d2", "d3", "d4", "d5");
            var pristine = File.ReadAllBytes(path);
            var start = LastLineStart(pristine);
            var end = pristine.Length - 1;
            var recordLength = end - start;

            var silentDeletes = new List<int>();
            for (var i = start; i < end; i++)
            {
                File.WriteAllBytes(path, pristine.Take(i).Concat(pristine.Skip(i + 1)).ToArray());
                try
                {
                    if (DecisionLedger.ReadFile(path, out _).Count != 6)
                        silentDeletes.Add(i - start);
                }
                catch (System.Text.Json.JsonException) { }
            }
            Assert.Equal(new[] { recordLength - 1 }, silentDeletes.ToArray());

            var silentInserts = new List<string>();
            for (var value = 0; value < 256; value++)
                for (var i = start; i <= end; i++)
                {
                    File.WriteAllBytes(
                        path,
                        pristine.Take(i).Concat(new[] { (byte)value }).Concat(pristine.Skip(i)).ToArray());
                    try
                    {
                        if (DecisionLedger.ReadFile(path, out _).Count != 6)
                            silentInserts.Add($"{i - start}/0x{value:x2}");
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            Assert.True(
                silentInserts.Count == 0,
                $"inserting a byte silently dropped the final record at [{string.Join(",", silentInserts)}]");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// A GENUINE interrupted write must still be healed at EVERY cut point — the
    /// direction the escape-class fix could most easily have broken. Cutting the
    /// final record off at each of its lengths in turn is exactly what a partial
    /// flush leaves, and every one of those must be tolerated and truncated, not
    /// refused as damage.
    /// <para>
    /// The ledger is seeded with enough records for the final seq to be
    /// MULTI-DIGIT (12 records, so seq 11). That is not cosmetic: a mutation
    /// sweep showed a template that read only the FIRST digit of Seq left the
    /// whole suite green, because every torn-write test until then had used a
    /// single-digit seq and so never tore a record whose number was longer.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryTruncationOfTheFinalRecord_IsStillHealedAsATornWrite()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, Enumerable.Range(0, 12).Select(i => "t" + i).ToArray());
            var pristine = File.ReadAllBytes(path);
            var start = LastLineStart(pristine);
            var end = pristine.Length - 1;

            // The seq of the record about to be torn really is multi-digit.
            Assert.Contains(
                "\"Seq\":11,",
                System.Text.Encoding.UTF8.GetString(pristine, start, end - start));

            var notHealed = new List<int>();
            for (var i = start; i < end; i++)
            {
                File.WriteAllBytes(path, pristine.Take(i).ToArray());
                try
                {
                    if (DecisionLedger.ReadFile(path, out var damage).Count != 11 || !damage.IsClean)
                        notHealed.Add(i - start);
                }
                catch (System.Text.Json.JsonException)
                {
                    notHealed.Add(i - start);
                }
            }

            Assert.True(
                notHealed.Count == 0,
                $"a genuine torn write was refused or mis-read at cut points [{string.Join(",", notHealed)}]");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The write-prefix check duplicates knowledge the SERIALIZER owns — the
    /// member order and the shape of each value. If DecisionLedgerEntry gains,
    /// loses or reorders a member, or a hash stops being 64 lowercase hex, the
    /// check would start calling every torn write damage and the ledger would
    /// begin refusing ordinary crash-tails. This pins the two together by taking
    /// a REAL serialized record and asserting that every one of its prefixes is
    /// accepted and the whole record is not — which is the exact contract, and is
    /// not satisfiable by reformatting either side.
    /// </summary>
    [Fact]
    public void EveryPrefixOfARealSerializedRecord_IsAcceptedAsATornWrite()
    {
        var path = NewLedgerPath();
        try
        {
            // Values that exercise the escape and non-ASCII paths of the template.
            using (var ledger = new DecisionLedger(path))
                ledger.Append(
                    "id \"quoted\" \\ back\u00e9",     // quote, backslash, non-ASCII
                    DecisionLedger.DecisionQuarantine,
                    "re\tason\u2028end");                // control + line-separator chars

            var line = File.ReadAllBytes(path).TakeWhile(b => b != (byte)'\n').ToArray();
            Assert.True(line.Length > 200, "the harness produced an implausibly short record");

            // Prefix that stops inside the record: a torn write. Tolerated.
            for (var cut = 1; cut < line.Length; cut++)
            {
                File.WriteAllBytes(path, line.Take(cut).ToArray());
                var read = DecisionLedger.ReadFile(path, out var damage);
                Assert.True(
                    read.Count == 0 && damage.IsClean,
                    $"a {cut}-byte prefix of a real record was not treated as a torn write");
            }

            // The WHOLE record is not a prefix: it must read back as a record.
            File.WriteAllBytes(path, line);
            Assert.Single(DecisionLedger.ReadFile(path, out var whole));
            Assert.True(whole.IsClean);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// PER-CLAUSE cover for the write-prefix check. The byte/delete/insert
    /// sweeps above cover it COLLECTIVELY, and a mutation sweep showed that is
    /// not enough: several clauses are individually redundant for the damage a
    /// single-byte sweep can produce, so removing one on its own left the whole
    /// suite green. Each row here is a final line crafted so that exactly ONE
    /// clause decides it — remove that clause and this row alone flips from
    /// refused to silently truncated.
    /// <para>
    /// Every row is genuinely INCOMPLETE JSON, which is what makes it a fair
    /// test: an incomplete value is the shape the ledger is entitled to truncate,
    /// so the only thing standing between these bytes and a silent drop is the
    /// clause named in the row.
    /// </para>
    /// <para>
    /// Behavioural: each row is written to a real file behind real records and
    /// read back through the real auditor.
    /// </para>
    /// </summary>
    [Theory]
    // ScanHex64, opening-quote clause: the quote before a hash value was
    // overwritten by a digit, and the write then tore.
    [InlineData("hash-open-quote", "\"Hash\":\"", "\"Hash\":0")]
    // ScanHex64, alphabet clause: a non-hex byte inside the hash value, with the
    // line ending before the count could give it away.
    [InlineData("hash-alphabet", "@HASH@", "@HASH63@z")]
    // ScanHex64, length clause: 65 hex characters — one more than any hash the
    // writer can produce.
    [InlineData("hash-length", "@HASH@", "@HASH@a")]
    // Both hash members carry the constraint, not just the last one: PrevHash is
    // swept separately or a template that only guarded Hash would look correct.
    [InlineData("prevhash-alphabet", "@PREV@", "@PREV63@z")]
    // ScanText, opening-quote clause: a string member's opening quote overwritten
    // by a digit.
    [InlineData("text-open-quote", "\"ItemId\":\"", "\"ItemId\":0")]
    // The literal clause: a key name overwritten, with the line torn afterwards
    // so the result is incomplete rather than complete-but-not-a-record.
    [InlineData("key-literal", "\"ItemId\"", "\"XtemId\"")]
    public void EachClauseOfTheWritePrefixCheck_RefusesOnItsOwn(
        string label, string find, string replace)
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "c0", "c1");
            var text = File.ReadAllText(path);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var record = lines[^1];

            // The hash value of the final record, and a 63-character prefix of
            // it, as substitution tokens — so the rows stay readable and no row
            // hard-codes a hash.
            var hashAt = record.LastIndexOf("\"Hash\":\"", StringComparison.Ordinal) + "\"Hash\":\"".Length;
            var hash = record.Substring(hashAt, 64);
            var prevAt = record.IndexOf("\"PrevHash\":\"", StringComparison.Ordinal) + "\"PrevHash\":\"".Length;
            var prev = record.Substring(prevAt, 64);
            string Expand(string s) => s
                .Replace("@HASH63@", hash[..63]).Replace("@HASH@", hash)
                .Replace("@PREV63@", prev[..63]).Replace("@PREV@", prev);

            var crafted = record.Replace(Expand(find), Expand(replace), StringComparison.Ordinal);
            Assert.NotEqual(record, crafted);          // the row actually changed something

            // Tear the line after the edit so the residue is INCOMPLETE JSON —
            // otherwise the complete-value guard decides it and this row would
            // not be testing its clause at all.
            var cut = crafted.IndexOf(Expand(replace), StringComparison.Ordinal) + Expand(replace).Length;
            crafted = crafted[..cut];
            Assert.True(
                IsIncompleteJson(crafted),
                $"{label}: the crafted line is not an incomplete JSON value, so it does not "
                + "exercise the write-prefix check at all");

            File.WriteAllText(path, lines[0] + "\n" + crafted);

            var refused = false;
            try { DecisionLedger.ReadFile(path); }
            catch (System.Text.Json.JsonException) { refused = true; }
            Assert.True(refused, $"{label}: an altered byte was silently truncated as if it were a torn write");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Is this text an INCOMPLETE JSON value — well-formed so far, then stopping?
    /// Used only to prove the rows above reach the code they claim to test.
    /// </summary>
    private static bool IsIncompleteJson(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        try
        {
            var whole = new System.Text.Json.Utf8JsonReader(bytes);
            while (whole.Read()) { }
            return false;   // complete
        }
        catch (System.Text.Json.JsonException) { }

        try
        {
            var partial = new System.Text.Json.Utf8JsonReader(bytes, isFinalBlock: false, state: default);
            while (partial.Read()) { }
            return true;    // ran out of data: incomplete
        }
        catch (System.Text.Json.JsonException)
        {
            return false;   // invalid, not incomplete
        }
    }

    // ── MAJOR: Seq range ─────────────────────────────────────────────────────

    private static string RecordLine(long seq, string itemId, string prevHash)
    {
        const string ts = "2026-01-01T00:00:00.0000000+00:00";
        var hash = DecisionLedger.ComputeHash(seq, itemId, DecisionLedger.DecisionExclude, "r", ts, prevHash);
        return "{" + string.Join(",",
            $"\"Seq\":{seq}",
            $"\"ItemId\":\"{itemId}\"",
            $"\"Decision\":\"{DecisionLedger.DecisionExclude}\"",
            "\"Reason\":\"r\"",
            $"\"Timestamp\":\"{ts}\"",
            $"\"PrevHash\":\"{prevHash}\"",
            $"\"Hash\":\"{hash}\"") + "}";
    }

    /// <summary>
    /// A line whose Seq is out of the counter's usable range is not a record and
    /// must never anchor the chain. The two ends are separate clauses of the
    /// guard and are covered SEPARATELY here — deleting either one on its own
    /// turns exactly its own case red.
    /// <para>
    /// Observed before the fix: Seq=-4 gave BaseSeq=-3 and the next appended
    /// record took Seq=-3; Seq=long.MaxValue gave BaseSeq=long.MinValue (the
    /// increment overflowing) and the next appended record took long.MinValue.
    /// Both are asserted against directly.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(-1L)]           // negative clause
    [InlineData(-4L)]           // negative clause
    [InlineData(long.MaxValue)] // overflow clause
    public void ASeqOutsideTheUsableRange_IsNotARecord_AndNeverAnchorsTheChain(long seq)
    {
        var path = NewLedgerPath();
        try
        {
            File.WriteAllText(path, RecordLine(seq, "BAD", DecisionLedger.GenesisHash) + "\n");

            DecisionLedgerEntry appended;
            long baseSeq;
            using (var ledger = new DecisionLedger(path))
            {
                baseSeq = ledger.BaseSeq;
                appended = ledger.Append("good", DecisionLedger.DecisionQuarantine, "malware-scan");
            }

            Assert.Equal(0, baseSeq);
            Assert.Equal(0, appended.Seq);
            Assert.Equal(DecisionLedger.GenesisHash, appended.PrevHash);
            // And it is not silently deleted either: complete JSON, so it stays.
            Assert.Contains("\"ItemId\":\"BAD\"", File.ReadAllText(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The boundary on the accepting side: Seq 0 and MaxSeq-1 are inside the
    /// range, MUST still be records, and MUST still hand the chain a successor.
    /// Without this the theory above would pass just as well if the guard
    /// rejected every seq.
    /// <para>
    /// MaxSeq itself is deliberately NOT here: it is a record (see the test
    /// below) but it has no issuable successor, so asserting <c>seq + 1</c> for
    /// it is asserting the very overflow this round removed.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(DecisionLedger.MaxSeq - 1)]
    public void ASeqInsideTheUsableRange_IsStillARecord(long seq)
    {
        var path = NewLedgerPath();
        try
        {
            File.WriteAllText(path, RecordLine(seq, "OK", DecisionLedger.GenesisHash) + "\n");

            using var ledger = new DecisionLedger(path);
            Assert.Equal(1, ledger.ResumedRecordCount);
            Assert.Equal(seq + 1, ledger.Append("next", DecisionLedger.DecisionExclude, "r").Seq);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// THE RANGE DEFECT THIS ROUND CLOSES. Guarding the single value
    /// <c>long.MaxValue</c> at the ANCHOR left <c>long.MaxValue - 1</c> accepted
    /// and equally overflow-inducing, because an accepted anchor has an unbounded
    /// number of appends ahead of it. Observed before the fix, on the real
    /// writer:
    /// <code>
    ///   anchor=9223372036854775806 baseSeq=9223372036854775807
    ///   issued1=9223372036854775807  issued2=-9223372036854775808  verify=True
    /// </code>
    /// i.e. the second Append wrote long.MinValue — a NEGATIVE seq, the exact
    /// state the guard exists to make impossible — and Verify() still said the
    /// chain was fine.
    /// <para>
    /// The property asserted here is the whole invariant and not one value of it:
    /// starting from EVERY anchor in the top of the range, no Append may ever
    /// return a seq outside [0, MaxSeq]. Appending is driven until it refuses, so
    /// a wrap at any depth fails this, not just a wrap on the first call.
    /// </para>
    /// <para>
    /// Behavioural: it runs the real resume and the real writer. The control row
    /// (an anchor low enough to have room) proves the guard is not simply
    /// refusing everything.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(DecisionLedger.MaxSeq)]      // one Append left; it must be the last
    [InlineData(DecisionLedger.MaxSeq - 1)]  // two left
    [InlineData(DecisionLedger.MaxSeq - 2)]  // three left
    [InlineData(100L)]                       // control: room to spare, must not refuse
    public void NoAnchorLetsAppendIssueASeqOutsideTheUsableRange(long anchor)
    {
        var path = NewLedgerPath();
        try
        {
            File.WriteAllText(path, RecordLine(anchor, "anchor", DecisionLedger.GenesisHash) + "\n");

            var issued = new List<long>();
            var refused = false;
            using (var ledger = new DecisionLedger(path))
            {
                for (var i = 0; i < 5 && !refused; i++)
                {
                    try
                    {
                        issued.Add(ledger.Append("x" + i, DecisionLedger.DecisionExclude, "r").Seq);
                    }
                    catch (InvalidOperationException)
                    {
                        refused = true;
                    }
                }
                Assert.True(ledger.Verify().Valid, "the segment this run appended must still verify");
            }

            Assert.All(issued, seq => Assert.InRange(seq, 0, DecisionLedger.MaxSeq));
            // Each issued seq is the previous plus one, so nothing was skipped or
            // reused on the way to the refusal.
            for (var i = 0; i < issued.Count; i++)
                Assert.Equal(anchor + 1 + i, issued[i]);

            // The number of appends the anchor leaves is exactly the room left in
            // the range — no more (that would need an out-of-range seq) and no
            // fewer (that would be a guard refusing usable seqs).
            var room = DecisionLedger.MaxSeq - anchor;
            if (room < 5)
            {
                Assert.True(refused, $"anchor {anchor} left {room} seq(s) but Append never refused");
                Assert.Equal(room, issued.Count);
            }
            else
            {
                Assert.False(refused, $"anchor {anchor} had room to spare but Append refused");
                Assert.Equal(5, issued.Count);
            }

            // Whatever landed on disk is a chain of in-range seqs, still readable.
            Assert.All(
                DecisionLedger.ReadFile(path),
                e => Assert.InRange(e.Seq, 0, DecisionLedger.MaxSeq));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }


    // ── MINOR: the two readers must agree about damage ───────────────────────

    /// <summary>
    /// Records separated by a lone CR instead of the '\n' the writer emits. That
    /// separator was OVERWRITTEN, so both readers must call it damage — and,
    /// per the ResumedRecordCount contract, must agree about the record count
    /// too. Before the fix ReadFile went through File.ReadLines, which breaks a
    /// line on a lone CR and so reported IsClean=True against ResumeTail's
    /// GluedLines=1.
    /// </summary>
    [Fact]
    public void ALoneCrSeparator_IsDamageToBothReaders()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "cr-A", "cr-B");
            var bytes = File.ReadAllBytes(path);
            for (var i = 0; i < bytes.Length; i++)
                if (bytes[i] == (byte)'\n') { bytes[i] = (byte)'\r'; break; }
            File.WriteAllBytes(path, bytes);

            var read = DecisionLedger.ReadFile(path, out var readDamage);
            using var ledger = new DecisionLedger(path);

            Assert.Equal(new[] { "cr-A", "cr-B" }, read.Select(e => e.ItemId).ToArray());
            Assert.Equal(read.Count, ledger.ResumedRecordCount);
            Assert.False(readDamage.IsClean);
            Assert.False(ledger.ResumedDamage.IsClean);
            Assert.Equal(ledger.ResumedDamage.GluedLines, readDamage.GluedLines);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// CRLF must NOT be mistaken for damage by the new byte-level split: the CR
    /// there is part of an intact terminator, and is trimmed as trailing
    /// whitespace exactly as before.
    /// </summary>
    [Fact]
    public void CrlfTerminators_AreStillNotDamage()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "x0", "x1");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));

            var read = DecisionLedger.ReadFile(path, out var damage);
            Assert.Equal(new[] { "x0", "x1" }, read.Select(e => e.ItemId).ToArray());
            Assert.True(damage.IsClean);
            Assert.True(DecisionLedger.Verify(read).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// The BOM invariant the previous round established must survive ReadFile
    /// no longer using File.ReadLines (which stripped the BOM for free): both
    /// readers must still see the same records, and a BOM is not damage.
    /// </summary>
    [Fact]
    public void AUtf8Bom_IsStillStrippedByBothReaders()
    {
        var path = NewLedgerPath();
        try
        {
            Seed(path, "b0", "b1");
            var body = File.ReadAllBytes(path);
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray());

            var read = DecisionLedger.ReadFile(path, out var damage);
            using var ledger = new DecisionLedger(path);

            Assert.Equal(new[] { "b0", "b1" }, read.Select(e => e.ItemId).ToArray());
            Assert.Equal(read.Count, ledger.ResumedRecordCount);
            Assert.True(damage.IsClean);
            Assert.True(DecisionLedger.Verify(read).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
