// Infrastructure/DecisionLedger.cs
// --------------------------------
// Append-only, SHA-256 HASH-CHAINED audit ledger for COMPLIANCE DECISIONS —
// specifically the two low-volume, security-relevant decisions the connector
// makes: No-MNE EXCLUSIONS and classification-driven ACL RESTRICTIONS. It is
// NOT a per-ingest log (that would be high-volume and defeat the point); only
// exclusion and restriction decisions are recorded.
//
// Each entry carries a monotonically increasing seq, the item id, the decision,
// a human reason, a timestamp, the PREVIOUS entry's hash, and its own hash:
//
//     hash = SHA256( seq | itemId | decision | reason | timestamp | prevHash )
//
// The chain makes the log tamper-EVIDENT: altering, reordering, inserting or
// deleting any entry breaks every subsequent hash link, which Verify() detects.
// (Tamper-evident, not tamper-proof: an attacker who can rewrite the whole file
// can recompute the chain — pair it with off-box shipping / WORM storage for a
// full guarantee. The chain still catches accidental corruption and partial
// edits.)
//
// ONE CHAIN PER CONNECTOR, NOT ONE PER RUN. The ledger is a single append-only
// file at the LOGS ROOT (PathFor: logs/decision_ledger_{connectorId}.jsonl) that
// every run resumes: seq keeps climbing and each run's first entry links to the
// previous run's last. This is load-bearing for the tamper-evidence claim —
// a chain that restarted from genesis each run could not prove anything ABOUT
// the gaps between runs, so deleting one run's ledger outright would leave no
// trace. It also keeps the file out of logs/{run}/, which LOG_RETENTION_DAYS
// deletes wholesale (LogPruner additionally refuses to delete any directory
// still holding a ledger, so retention can never destroy audit records).
// Single active writer per connector id is assumed, as elsewhere in logs/.
//
// File-backed JSONL (like the reconciliation / classification manifests) when a
// path is given; in-memory (counts + chain only, no file) otherwise, so callers
// can attach a no-op ledger unconditionally.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.Chassis;

/// <summary>
/// One immutable, hash-chained decision record.
/// <para>
/// EVERY member is <see cref="JsonRequiredAttribute"/>: a JSON object that omits
/// any of them is malformed input, not a record. Without this the serializer
/// silently default-fills whatever the JSON left out — <c>null</c> for the
/// string members (which then detonates in <c>ComputeHash</c>) and, worse,
/// <c>0</c> for the non-nullable <see cref="Seq"/>, which is not detectable by a
/// null check at all: a line carrying no <c>Seq</c> became the resume anchor
/// with seq 0 and reset the chain's next seq to 1, RE-ISSUING seqs that live
/// records already hold. Requiring presence at the contract closes both, and
/// closes them for any member added to this record in future — the guard cannot
/// be forgotten because it lives on the member, not in a hand-maintained list.
/// </para>
/// <para>
/// Presence is not the whole contract: JSON may name a member and set it to
/// <c>null</c>. <c>DecisionLedger.IsCompleteRecord</c> rejects that, per member.
/// </para>
/// </summary>
public sealed record DecisionLedgerEntry(
    [property: JsonRequired] long Seq,
    [property: JsonRequired] string ItemId,
    [property: JsonRequired] string Decision,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] string Timestamp,
    [property: JsonRequired] string PrevHash,
    [property: JsonRequired] string Hash);

/// <summary>Result of verifying a ledger chain.</summary>
public readonly record struct LedgerVerification(bool Valid, long? FirstBrokenSeq, string? Detail)
{
    public static readonly LedgerVerification Ok = new(true, null, null);

    public static LedgerVerification Broken(long seq, string detail) => new(false, seq, detail);
}

/// <summary>
/// PHYSICAL damage found while reading a ledger file, as distinct from a broken
/// hash chain. The two are independent, and the gap between them is a real
/// blind spot: a file can be physically mangled — a lost or destroyed line
/// terminator — and still hand back a chain that verifies perfectly, because
/// every record it holds is intact and correctly linked. Nothing is lost, so
/// refusing the file would be wrong; but an integrity monitor still wants to
/// know, which is what this reports.
/// <para>
/// It is also the answer to the APPEND-SMUGGLING shape: a forged but correctly
/// chained record glued onto the END of an existing line, adding no new physical
/// line. The hash chain never defended against append access at all (an attacker
/// who can append can chain a forgery onto a fresh line just as easily, and that
/// is the documented residual risk that off-box/WORM shipping covers), but the
/// glued form additionally hides from a line-count or tail-based monitor. It no
/// longer hides from this: <see cref="GluedLines"/> is non-zero for any line
/// carrying more than one record, however it got there.
/// </para>
/// </summary>
/// <param name="GluedLines">
/// Lines holding several records with no separator between them — a lost
/// terminator, or a smuggled append.
/// </param>
/// <param name="ResyncedRegions">
/// Regions of bytes that were OVERWRITTEN (invalid where they sit) and had to be
/// stepped over to reach the records behind them. No interrupted write can
/// produce these.
/// </param>
/// <param name="DamagedTail">
/// The file ends in overwritten bytes with no record behind them. Reading throws
/// in this case, so this is only ever observed via the thrown exception.
/// </param>
public readonly record struct LedgerFileDamage(
    int GluedLines, int ResyncedRegions, bool DamagedTail)
{
    /// <summary>True when the file is physically intact, whatever the chain says.</summary>
    public bool IsClean => GluedLines == 0 && ResyncedRegions == 0 && !DamagedTail;
}

public sealed class DecisionLedger : IDisposable
{
    /// <summary>Opt-in gate: DECISION_LEDGER=true writes a file-backed ledger per run.</summary>
    public const string EnvVar = "DECISION_LEDGER";

    /// <summary>Well-known decision kinds (free-form is allowed, these are the ones the pipeline emits).</summary>
    public const string DecisionExclude = "exclude";
    public const string DecisionAclRestrict = "acl-restrict";

    /// <summary>
    /// ContentGate (CS-1) quarantine: an item whose payload or indexed text was
    /// blocked by the malware / prompt-injection scanners. Its own kind on
    /// purpose — a quarantine is neither an exclusion (a policy decision about
    /// metadata, taken before anything is fetched) nor an ACL restriction (the
    /// item is still indexed, just narrower), so overloading either would make
    /// the ledger unauditable.
    /// </summary>
    public const string DecisionQuarantine = "quarantine";

    /// <summary>Genesis link for the first entry (64 hex zeros).</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// The largest seq <see cref="Append"/> will ever issue. <c>long.MaxValue</c>
    /// is deliberately RESERVED and never issued, which is what makes the two
    /// increments in this file total: <see cref="ResumeTail"/>'s
    /// <c>last.Seq + 1</c> tops out at exactly <c>long.MaxValue</c>, and
    /// <see cref="Append"/>'s <c>_seq++</c> tops out there too. Neither can wrap.
    /// <para>
    /// Guarding only the single value <c>long.MaxValue</c> was not enough and was
    /// a shipped defect: an anchor of <c>long.MaxValue - 1</c> was accepted, the
    /// first <see cref="Append"/> issued <c>long.MaxValue</c>, and the SECOND
    /// wrapped <c>_seq</c> to <c>long.MinValue</c> and wrote a negative seq — the
    /// exact state the range check exists to make impossible — with
    /// <see cref="Verify()"/> still returning true. The bound therefore has to be
    /// enforced at the point of ISSUE as well as at the point of acceptance; no
    /// anchor value alone can bound an unbounded number of future appends.
    /// </para>
    /// </summary>
    public const long MaxSeq = long.MaxValue - 1;

    private static readonly IAppLogger Logger = Logging.GetLogger(Chassis.Identity.LedgerLoggerName);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly object _lock = new();
    private readonly StreamWriter? _writer;
    private readonly List<DecisionLedgerEntry> _entries = new();
    private long _seq;
    private string _lastHash = GenesisHash;
    private bool _disposed;

    /// <summary>
    /// The chain anchor this instance started from: the seq its first entry
    /// takes and the hash that entry links back to. Both are the genesis values
    /// for a new chain, and the tail of the existing chain when a persisted
    /// ledger was resumed. <see cref="Verify()"/> checks this instance's own
    /// entries against the anchor, so a resumed segment verifies on its own
    /// terms without holding the whole historical chain in memory.
    /// </summary>
    private readonly long _baseSeq;

    private readonly string _baseHash;

    public string? FilePath { get; }

    /// <summary>
    /// The stable, per-connector ledger path at the LOGS ROOT —
    /// <c>logs/decision_ledger_{connectorId}.jsonl</c>, matching the rest of the
    /// connector fleet. Deliberately NOT inside <c>logs/{run}/</c>: run
    /// directories are what <see cref="LogPruner"/> deletes under
    /// LOG_RETENTION_DAYS, and a per-run path would also restart the hash chain
    /// on every run (so deleting a whole run's ledger would be undetectable).
    /// </summary>
    public static string PathFor(string logsDir, string connectorId) =>
        Path.Combine(logsDir, $"decision_ledger_{connectorId}.jsonl");

    /// <summary>The seq this instance's first entry took (0 for a fresh chain).</summary>
    public long BaseSeq => _baseSeq;

    /// <summary>True when this instance picked up an existing persisted chain rather than starting one.</summary>
    public bool ContinuedExistingChain => _baseSeq > 0;

    /// <summary>
    /// How many records <see cref="ResumeTail"/> found in the file it resumed.
    /// Exposed because the two readers of a ledger file — this one and
    /// <see cref="ReadFile"/> — must agree about what the file holds, and a
    /// disagreement is a defect: it means one of them is silently skipping a
    /// record the other can see, or calling damage clean that the other calls
    /// damage. A UTF-8 BOM used to cause the first shape (ReadLines stripped it,
    /// the byte scan did not); a lone CR between two records used to cause the
    /// second (ReadLines healed it into a line break, so ReadFile reported
    /// IsClean against this scan's GluedLines=1). Both readers now split on the
    /// newline byte alone and both strip a leading BOM.
    /// </summary>
    public long ResumedRecordCount { get; }

    /// <summary>
    /// PHYSICAL damage the resume scan found in the file it picked up, reported
    /// the same way <see cref="ReadFile(string, out LedgerFileDamage)"/> reports
    /// it. A ledger can resume perfectly and still be a damaged file — every
    /// record recovered, chain intact, bytes overwritten — and that is precisely
    /// the state an operator needs told about while it is still one incident
    /// rather than a pattern.
    /// </summary>
    public LedgerFileDamage ResumedDamage { get; }

    /// <summary>
    /// Legacy per-run ledgers: <c>decision_ledger_*.jsonl</c> files left INSIDE
    /// a <c>logs/{run}/</c> directory by releases before the ledger moved to the
    /// stable logs-root path. Each is a separate chain rooted at its own genesis
    /// and is NOT continued by the current ledger, so they must be surfaced to
    /// the operator rather than silently orphaned. Never throws.
    /// </summary>
    public static IReadOnlyList<string> FindLegacyRunLedgers(string logsDir)
    {
        try
        {
            if (!Directory.Exists(logsDir))
                return Array.Empty<string>();
            // Subdirectories only — the current ledger lives at the root and is
            // not "legacy".
            return Directory.GetDirectories(logsDir)
                .SelectMany(dir => Directory.GetFiles(dir, "decision_ledger_*.jsonl", SearchOption.AllDirectories))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exc)
        {
            Logger.Warning($"Could not scan {logsDir} for legacy per-run decision ledgers: {exc.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Open a file-backed ledger at <paramref name="filePath"/> (parent dirs
    /// hardened owner-only), CONTINUING the chain already recorded there.
    /// <para>
    /// The path is stable across runs (see <see cref="PathFor"/>), so one file
    /// accumulates ONE chain for the life of the deployment: the writer appends,
    /// and seq / prev_hash resume from the last complete record. That is what
    /// lets the chain prove continuity BETWEEN runs.
    /// </para>
    /// <para>
    /// Assumes a single active writer per connector id — the same assumption the
    /// sync cursor, checkpoints and the dead-letter queue in this directory
    /// already make.
    /// </para>
    /// </summary>
    public DecisionLedger(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            SecureDirectory.EnsureHardened(dir);

        var tail = ResumeTail(filePath);
        _seq = tail.NextSeq;
        _baseSeq = tail.NextSeq;
        _lastHash = tail.LastHash;
        _baseHash = tail.LastHash;
        ResumedRecordCount = tail.RecordCount;
        ResumedDamage = tail.Damage;

        _writer = new StreamWriter(filePath, append: true, Utf8NoBom);

        // Restore the terminator of a final record that lost only its newline,
        // so the next append starts a line of its own instead of being
        // concatenated onto a record that is already durable evidence.
        if (tail.NeedsTerminator)
        {
            _writer.Write('\n');
            _writer.Flush();
        }
    }

    /// <summary>In-memory ledger (chain only, no file) — the default no-op attachment and test seam.</summary>
    public DecisionLedger()
    {
        FilePath = null;
        _baseHash = GenesisHash;
    }

    /// <summary>
    /// Where a resumed chain picks up: the next seq, and the hash to link to.
    /// <paramref name="NeedsTerminator"/> means the last record on disk is
    /// complete but lost its trailing newline to a crash — the writer must
    /// restore it before appending.
    /// </summary>
    private readonly record struct LedgerTail(
        long NextSeq, string LastHash, long RecordCount, bool NeedsTerminator,
        LedgerFileDamage Damage);

    /// <summary>
    /// Scan an existing ledger for its resume point, and drop an UNCOMMITTED
    /// crash-tail if one is present.
    /// <para>
    /// Only bytes AFTER the last complete, parseable record are ever discarded —
    /// a torn final line is a record that was never durably written, and leaving
    /// it in place would turn it into interior corruption the moment the next
    /// record is appended (which <see cref="ReadFile"/> then refuses to read at
    /// all). A malformed line with good records after it is genuine interior
    /// corruption, is NOT a tail, and is left exactly where it is so the chain
    /// keeps the evidence and Verify keeps reporting it.
    /// </para>
    /// <para>
    /// A crash can tear the file in the six shapes below, and the difference
    /// between them decides whether a durable audit record lives or dies. They
    /// are documented individually because each one was a shipped defect, but
    /// the code no longer branches on them: shapes 1-3 are "the residue is an
    /// interrupted write, discard it", 4-5 are "resynchronise and commit
    /// everything", and 6 is "this is damage, keep it and be loud". A seventh
    /// shape nobody has named should land in one of those three by construction.
    /// </para>
    /// <list type="number">
    /// <item>
    /// FRAGMENT TAIL — a partial, unparseable last line. Never a record; it is
    /// discarded.
    /// </item>
    /// <item>
    /// NEWLINE-BOUNDARY TEAR — the last line PARSES as a complete record but has
    /// no '\n'. Its bytes are all on disk, so <see cref="Append"/> already
    /// returned it to the caller: it is durable, acknowledged audit evidence and
    /// is KEPT (the terminator is restored on open). Discarding it would be the
    /// worst possible failure for a tamper-evident log — the surviving prefix
    /// plus the next run's entries still form an internally consistent chain, so
    /// <see cref="Verify()"/> would report CLEAN over a ledger that had silently
    /// lost a record.
    /// </item>
    /// <item>
    /// BLANK-LINE TEAR — a torn line whose newline landed, followed by blank
    /// lines. The blank tail must NOT commit the torn line: doing so left it in
    /// the file, the next append put a real record after it, and from then on
    /// <see cref="ReadFile"/> refused the whole file as interior corruption —
    /// one crash bricking the audit trail for the life of the file.
    /// </item>
    /// <item>
    /// INTERIOR-NEWLINE TEAR — a '\n' BETWEEN two flushed records is LOST,
    /// gluing them into one line that no longer parses as a single value. Both
    /// records are complete, durable, acknowledged evidence, so the line is
    /// split back into them (see <see cref="ScanRecords"/>) and they are
    /// committed. This is the shape with the largest blast radius: treating the
    /// glued line as unresolved junk left the committed mark BEHIND it, so
    /// resume truncated away every record from the damaged boundary to EOF —
    /// and, exactly as in shape 2, the surviving prefix plus the next run's
    /// entries still verified CLEAN over a ledger that had lost records.
    /// A line may also hold record(s) followed by a torn fragment (a lost
    /// newline plus a crash); then only the fragment is uncommitted, and the
    /// cut is made at its exact byte offset INSIDE the line.
    /// </item>
    /// <item>
    /// MANGLED SEPARATOR — the same boundary, but the '\n' was OVERWRITTEN
    /// rather than lost: a NUL (an allocated-but-unwritten block zero-filled by
    /// the filesystem, the classic crash artifact), a stray byte, half a UTF-8
    /// sequence, a Unicode line separator, anything. Splitting on "is the next
    /// byte the start of a record" cannot see past it, so shape 4's rescue did
    /// not fire and the shape-4 disaster came straight back — every record
    /// behind the bad byte truncated, chain still verifying CLEAN. The scan now
    /// RESYNCHRONISES: on a parse failure it steps forward to the next plausible
    /// record start and keeps committing, so no amount of destroyed separator
    /// can put a later record behind the truncation cliff.
    /// </item>
    /// <item>
    /// MANGLED RECORD BODY — damage inside a record's own bytes, so that record
    /// is genuinely unrecoverable. It is NOT silently deleted: the bytes stay
    /// (see <see cref="LineResidue.Damaged"/>) as the only surviving evidence
    /// that something was there, and the hole is surfaced — as a seq gap that
    /// <see cref="Verify(IReadOnlyList{DecisionLedgerEntry}, long, string)"/>
    /// reports when a later record survives, and otherwise as a malformed line
    /// that <see cref="ReadFile"/> refuses. The seq gap alone is NOT enough for
    /// the LAST record, which is why the refusal matters: there is nothing
    /// behind the last record, so no gap can ever appear. Damage to the last
    /// record used to leave complete-but-not-a-record JSON that was classified
    /// discardable and truncated away, and every signal then read clean.
    /// </item>
    /// </list>
    /// The invariant across all six, and the one this file exists to keep:
    /// NO ACKNOWLEDGED RECORD IS EVER SILENTLY LOST — it is recovered, or the
    /// damage is loud. Bytes are discarded ONLY when they are a plausible
    /// interrupted write, i.e. an INCOMPLETE JSON value; damage to
    /// already-flushed bytes is preserved as evidence. A line that is not a
    /// record never becomes the chain anchor.
    /// <para>
    /// THE ONE PLACE THAT INVARIANT DOES NOT REACH, stated plainly: damage
    /// confined to a record's FINAL byte — the closing brace — and only when
    /// that byte is overwritten by JSON WHITESPACE or deleted outright. What is
    /// left, once the trailing whitespace this format trims anyway is gone, is
    /// byte-for-byte identical to a write that stopped one byte short. Nothing
    /// in the file distinguishes them, so those bytes are treated as the
    /// crash-tail they are indistinguishable from: the record is dropped and the
    /// tail truncated.
    /// <para>
    /// MEASURED, POST-FIX, over a real 265-byte final record — all 256 byte
    /// values at all 265 offsets, plus delete, insert and truncate at every
    /// position:
    /// </para>
    /// <list type="bullet">
    /// <item>REPLACE: 67,840 combinations → 265 no-op, 16,675 recovered,
    /// 50,896 refused, 4 dropped quietly (offset 264, the closing brace, with
    /// 0x09 / 0x0a / 0x0d / 0x20) = 1 offset of 265.</item>
    /// <item>DELETE: 265 offsets → 179 recovered, 85 refused, 1 dropped (offset
    /// 264, the same byte).</item>
    /// <item>INSERT: 68,096 combinations → 0 dropped.</item>
    /// <item>TRUNCATE: 265 cut points → all 265 healed as torn writes.</item>
    /// </list>
    /// <para>
    /// The previous release put this at "2 offsets out of 265" and named the
    /// closing quote of Hash as one of them. That number came from a five-value
    /// replacement alphabet and was wrong: the true PRE-FIX figure was 3 offsets
    /// (228 of 67,840 combinations), and the third was a 0x5c BACKSLASH inside
    /// the Hash VALUE, which opened a JSON escape, swallowed the closing quote
    /// and made altered bytes read as an interrupted write. See
    /// <see cref="IsPlausibleWritePrefix"/>, which closed that class by
    /// requiring a discardable residue to be a byte-for-byte PREFIX of what this
    /// writer could have emitted — something an altered byte is not.
    /// </para>
    /// <para>
    /// Never throws: an unreadable ledger must not take the crawl down.
    /// </para>
    /// </summary>
    private static LedgerTail ResumeTail(string path)
    {
        var fresh = new LedgerTail(0, GenesisHash, 0, false, default);
        try
        {
            if (!File.Exists(path))
                return fresh;

            DecisionLedgerEntry? last = null;
            long records = 0;
            long committedBytes = 0;   // end of the last complete record that parsed
            long totalBytes;
            // The committed region ends WITHOUT a '\n' — the writer must restore
            // one before appending, or the next record is concatenated onto a
            // record that is already durable evidence (shapes 2 and 4).
            var committedNeedsTerminator = false;
            // A non-blank line that did not parse and has not been superseded by
            // a later complete record: everything from it on is unresolved, so
            // no BLANK line after it may mark it committed (shape 3).
            var unresolvedJunk = false;
            // Lines that turned out to hold more than one record, or a record
            // plus a fragment: a lost line terminator (shape 4). Reported once.
            var gluedLines = 0;
            // Regions of DESTROYED bytes the scan had to step over to reach a
            // later record (shape 5). Nothing was lost, but the file is damaged.
            var resyncedRegions = 0;
            // The trailing bytes are damage, not an interrupted write (shape 6):
            // truncating them would delete the only evidence that a record was
            // ever there. Re-decided on every content line, so it always
            // describes the bytes that are actually at the end of the file.
            var preserveTail = false;
            var endsWithNewline = false;

            using (var stream = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var buffered = new BufferedStream(stream))
            {
                var line = new List<byte>(256);
                long position = 0;
                int b;

                void EndOfLine(bool terminated)
                {
                    var raw = line.ToArray();
                    line.Clear();
                    // Where this line began, so a fragment glued INSIDE it can be
                    // cut at an exact byte offset without touching the records
                    // that share the line with it.
                    var lineStart = position - raw.Length - (terminated ? 1 : 0);

                    var content = TrimTrailingWhitespace(raw);
                    if (content.IsEmpty)
                    {
                        // A blank separator is harmless in ITSELF, but it commits
                        // only what precedes it that already parsed.
                        if (terminated && !unresolvedJunk)
                        {
                            committedBytes = position;
                            committedNeedsTerminator = false;
                            preserveTail = false;
                        }
                        return;
                    }

                    var recovered = new List<DecisionLedgerEntry>(1);
                    ScanRecords(content, recovered, out var consumed, out var gaps, out var residue);
                    resyncedRegions += gaps;
                    // Trailing bytes are only ever discarded when they are a
                    // plausible interrupted write (TornPrefix). Bytes that are
                    // invalid where they sit, OR that form a complete JSON value
                    // the record contract rejects, were overwritten after being
                    // flushed — that is evidence, and evidence is kept.
                    preserveTail = residue == LineResidue.Damaged;

                    if (recovered.Count == 0)
                    {
                        // A torn write, interior corruption, or a JSON value that
                        // is not a record at all. Do NOT advance the committed
                        // mark past it — but keep scanning, because records after
                        // it are still the physical tail of the chain.
                        unresolvedJunk = true;
                        return;
                    }

                    if (recovered.Count > 1 || consumed != content.Length)
                        gluedLines++;
                    last = recovered[^1];
                    records += recovered.Count;

                    if (residue == LineResidue.None)
                    {
                        // The whole line is records — one, or several glued
                        // together by a lost or destroyed terminator (shapes 4/5).
                        unresolvedJunk = false;
                        committedBytes = position;
                        // Complete record, missing terminator: keep it, repair the '\n'.
                        committedNeedsTerminator = !terminated;
                    }
                    else
                    {
                        // Complete record(s) followed by a torn fragment or by
                        // damage on the SAME line. Commit exactly the records;
                        // what follows is cut at that offset (torn write) or
                        // preserved behind it (damage), which either way leaves
                        // the committed region unterminated.
                        unresolvedJunk = true;
                        committedBytes = lineStart + consumed;
                        committedNeedsTerminator = true;
                    }
                }

                // A UTF-8 BOM at offset 0 is not damage and not part of record 1:
                // File.ReadLines strips it for ReadFile, so this must strip it
                // too or the two disagree about the first record.
                var head = new byte[3];
                var headLen = buffered.ReadAtLeast(head, 3, throwOnEndOfStream: false);
                if (headLen == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                {
                    position = 3;
                    committedBytes = 3;
                }
                else
                {
                    for (var i = 0; i < headLen; i++)
                    {
                        position++;
                        endsWithNewline = head[i] == (byte)'\n';
                        if (!endsWithNewline)
                            line.Add(head[i]);
                        else
                            EndOfLine(terminated: true);
                    }
                }

                while ((b = buffered.ReadByte()) >= 0)
                {
                    position++;
                    endsWithNewline = b == '\n';
                    if (!endsWithNewline)
                    {
                        line.Add((byte)b);
                        continue;
                    }
                    EndOfLine(terminated: true);
                }
                if (line.Count > 0)
                    EndOfLine(terminated: false);
                totalBytes = stream.Length;
            }

            var damage = new LedgerFileDamage(gluedLines, resyncedRegions, preserveTail);

            if (preserveTail && totalBytes > committedBytes)
            {
                // The tail is DAMAGE, not an interrupted write. Deleting it would
                // be exactly the silent evidence loss this whole routine exists
                // to prevent: the surviving prefix plus the next run's entries
                // would verify CLEAN over a ledger that had lost records. Keep
                // the bytes, terminate them so the next append starts its own
                // line, and shout.
                Logger.Error(
                    $"Decision ledger {path}: {totalBytes - committedBytes} trailing byte(s) are DAMAGED — "
                    + "invalid where they sit, so they were overwritten after being flushed rather than "
                    + "merely half-written. They are KEPT as evidence and NOT truncated (truncating them "
                    + "would erase the only trace that a record was there, and the chain would then verify "
                    + "clean over the hole). ReadFile() will refuse this file until it is investigated.");
                committedBytes = totalBytes;
                committedNeedsTerminator = !endsWithNewline;
            }

            if (last is null)
            {
                // No complete record at all: either an empty file or nothing but
                // a torn first write. Start the chain, discarding the fragment.
                if (totalBytes > committedBytes)
                    Truncate(path, committedBytes, totalBytes);
                // ...but if those bytes were DAMAGE they were kept, and the next
                // append must still start a line of its own — otherwise it is
                // glued onto the damage and the evidence stops being visible as
                // a malformed line of its own.
                return fresh with { NeedsTerminator = committedNeedsTerminator, Damage = damage };
            }

            if (gluedLines > 0)
            {
                Logger.Warning(
                    $"Decision ledger {path}: {gluedLines} line(s) hold more than one record (or a record "
                    + "followed by a fragment) — a line terminator was lost to a crash. Every record on "
                    + "those lines was flushed and acknowledged, so all of them are KEPT and counted; only "
                    + "an unparseable trailing fragment is trimmed. Investigate: the file is damaged even "
                    + "though no evidence was lost.");
            }

            if (resyncedRegions > 0)
            {
                Logger.Error(
                    $"Decision ledger {path}: {resyncedRegions} region(s) of DESTROYED bytes were stepped "
                    + "over to resynchronise on the records behind them — a record separator (or a record) "
                    + "was OVERWRITTEN, not merely lost, which no interrupted write can do. Every record "
                    + "still parseable was recovered and KEPT; where damage landed inside a record its seq "
                    + "is now missing, and Verify() reports that gap. Investigate this file.");
            }

            // Only ever bytes AFTER the last complete record, and only ever bytes
            // that are a plausible INTERRUPTED WRITE. The committed mark advances
            // THROUGH glued records and THROUGH destroyed separators (the scan
            // resynchronises), and damaged trailing bytes already moved it to EOF
            // above — so no shape of separator damage can take the records behind
            // it over the truncation cliff.
            if (totalBytes > committedBytes)
                Truncate(path, committedBytes, totalBytes);

            if (committedNeedsTerminator)
            {
                Logger.Warning(
                    $"Decision ledger {path}: the final record (seq {last.Seq}) is complete but its newline "
                    + "terminator was lost to a crash. It was already written and acknowledged, so it is "
                    + "KEPT as evidence and the terminator is restored before appending — dropping it "
                    + "would be a silent evidence loss that Verify() could not see.");
            }

            if (last.Seq + 1 != records)
            {
                Logger.Error(
                    $"Decision ledger {path}: the persisted chain is inconsistent — {records} record(s) "
                    + $"present but the last seq is {last.Seq} (expected {records - 1}). New entries are "
                    + "appended after it so nothing is overwritten, but Verify() will report the break; "
                    + "investigate before relying on this ledger as evidence.");
            }

            // last.Hash is non-null by construction: ScanRecords only yields
            // entries whose every hashed member is present, so the anchor this
            // returns can never carry a null into ComputeHash.
            return new LedgerTail(last.Seq + 1, last.Hash, records, committedNeedsTerminator, damage);
        }
        catch (Exception exc)
        {
            // Resuming failed (locked, unreadable, ...). Appending from genesis
            // would silently fork the chain, so say so loudly — the break stays
            // visible in the file and Verify() will flag it.
            Logger.Error(
                $"Decision ledger {path}: could not read the existing chain to resume it "
                + $"({exc.GetType().Name}: {exc.Message}). Appending from genesis — the chain will show "
                + "a break at this point.");
            return fresh;
        }
    }

    /// <summary>
    /// Is this deserialized line an actual RECORD — every field the hash covers
    /// present?
    /// <para>
    /// System.Text.Json will happily materialise a <see cref="DecisionLedgerEntry"/>
    /// from a JSON object that carries none of them ("{}", or one missing
    /// members), leaving the non-nullable string members NULL at runtime with no
    /// compiler warning anywhere. Accepting such a line as a record put a null
    /// Hash into the resume anchor, and the very next <see cref="Append"/>
    /// dereferenced it in <see cref="ComputeHash"/> → <c>AppendField</c>
    /// (<c>value.Length</c>) as an unhandled NullReferenceException — on the
    /// ContentGate quarantine path, so one malformed byte in an audit file could
    /// take down the malware / prompt-injection quarantine of a whole crawl.
    /// A line like that is malformed input, never a record.
    /// </para>
    /// <para>
    /// OMISSION is handled one level up, by <see cref="JsonRequiredAttribute"/>
    /// on every member of <see cref="DecisionLedgerEntry"/> — it has to be,
    /// because a null check cannot see a missing <see cref="DecisionLedgerEntry.Seq"/>:
    /// that member is a non-nullable <c>long</c> that silently default-fills to
    /// 0, and a "record" with seq 0 became the resume anchor and reset the
    /// chain's next seq to 1, RE-ISSUING seqs live records already held. What is
    /// left for this guard is the case the attribute cannot catch: a member that
    /// is PRESENT and explicitly <c>null</c>. Each is checked separately, and
    /// each has its own test.
    /// </para>
    /// <para>
    /// <see cref="DecisionLedgerEntry.Seq"/> additionally gets a RANGE check,
    /// which no presence or null check can stand in for. <see cref="Append"/>
    /// issues seqs only in <c>[0, <see cref="MaxSeq"/>]</c> and throws rather
    /// than leave that range, so a seq outside it is not something this writer
    /// produced. Both ends were live defects: a NEGATIVE seq became the resume
    /// anchor and the chain then issued negative seqs, and <c>long.MaxValue</c>
    /// anchored a chain whose very next seq OVERFLOWED to <c>long.MinValue</c>
    /// (unchecked arithmetic in <see cref="ResumeTail"/>'s <c>last.Seq + 1</c>).
    /// Rejecting both here means such a line is never a record and never anchors
    /// anything.
    /// </para>
    /// <para>
    /// This check bounds the ANCHOR, and that is all it can do. It cannot bound
    /// what the chain issues LATER — an accepted anchor of <see cref="MaxSeq"/>
    /// still has an unbounded number of appends ahead of it — which is why
    /// <see cref="Append"/> carries the matching guard at the point of issue.
    /// The two together are what make "every seq in the file is in
    /// <c>[0, <see cref="MaxSeq"/>]</c>" true rather than merely intended.
    /// </para>
    /// </summary>
    private static bool IsCompleteRecord(DecisionLedgerEntry? entry) =>
        entry is not null
        && entry.Seq >= 0
        && entry.Seq <= MaxSeq
        && entry.ItemId is not null
        && entry.Decision is not null
        && entry.Reason is not null
        && entry.Timestamp is not null
        && entry.PrevHash is not null
        && entry.Hash is not null;

    /// <summary>Drop trailing ASCII whitespace (notably a CR from a CRLF file).</summary>
    private static ReadOnlySpan<byte> TrimTrailingWhitespace(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            end--;
        return bytes[..end];
    }

    /// <summary>
    /// What the bytes AFTER the last recovered record on a line are. This is the
    /// discriminator the whole durability story now turns on: only a residue that
    /// is a plausible INTERRUPTED WRITE may ever be truncated away — anything
    /// else is damage to bytes that were already acknowledged, and damage is
    /// evidence, so it is preserved and surfaced instead of deleted.
    /// </summary>
    private enum LineResidue
    {
        /// <summary>Nothing but JSON whitespace follows. A clean line.</summary>
        None,

        /// <summary>
        /// An INCOMPLETE JSON value: well-formed as far as it goes and then the
        /// bytes stop. The signature of a partially flushed write, i.e. a record
        /// the caller was never told had landed. Safely discardable.
        /// </summary>
        TornPrefix,

        /// <summary>
        /// NOT an interrupted write. Either INVALID JSON — a byte that cannot
        /// occur where it sits — or a structurally COMPLETE JSON value that is
        /// not a decision record ("{}", "null", an object whose members do not
        /// satisfy the contract). Neither shape is reachable by interrupting a
        /// record write: <see cref="Append"/> serializes one object and flushes
        /// it, so every prefix of a partial write stops mid-value and is
        /// <see cref="TornPrefix"/>. Something therefore OVERWROTE bytes that had
        /// already been flushed, or wrote foreign content into the ledger. Must
        /// never be truncated.
        /// <para>
        /// The complete-value case used to be its own "safely discardable" state.
        /// It is not safe: a SINGLE overwritten byte inside the LAST record's key
        /// names (e.g. <c>"Seq"</c> → <c>"Xeq"</c>) leaves the line complete,
        /// valid JSON that simply is not a record, and discarding it destroyed
        /// the record silently — no seq gap could ever appear behind it because
        /// there is nothing behind the last record.
        /// </para>
        /// </summary>
        Damaged,
    }

    private static bool IsJsonWhitespace(byte b) =>
        b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>
    /// Classify the trailing bytes of a line that are not part of any recovered
    /// record, so the caller can tell an interrupted write (discardable) from
    /// damage to already-durable bytes (never discardable). Never throws.
    /// </summary>
    private static LineResidue ClassifyResidue(ReadOnlySpan<byte> rest)
    {
        var start = 0;
        while (start < rest.Length && IsJsonWhitespace(rest[start]))
            start++;
        if (start == rest.Length)
            return LineResidue.None;
        rest = rest[start..];

        // Does the whole residue LEX as complete JSON value(s)? Then it is
        // well-formed and yet the caller could not read a record out of it. An
        // interrupted write cannot produce that — every prefix of the single
        // object Append serializes stops mid-value — so these bytes were either
        // overwritten after being flushed or are foreign content. Either way
        // they are damage, and damage is evidence.
        try
        {
            var whole = new Utf8JsonReader(rest, new JsonReaderOptions { AllowMultipleValues = true });
            while (whole.Read())
            {
            }
            return LineResidue.Damaged;
        }
        catch (JsonException)
        {
            // Fall through: either truncated, or genuinely invalid.
        }

        // isFinalBlock:false makes the reader report "ran out of data" by simply
        // returning false, and reserve exceptions for bytes that are ILLEGAL
        // where they appear.
        try
        {
            var partial = new Utf8JsonReader(rest, isFinalBlock: false, state: default);
            while (partial.Read())
            {
            }
        }
        catch (JsonException)
        {
            return LineResidue.Damaged;
        }

        // The value is INCOMPLETE. That is necessary for an interrupted write but
        // NOT sufficient, and treating it as sufficient was a shipped defect: a
        // single 0x5c (backslash) landing on the last hex character of the final
        // record's Hash turned the closing quote into an ESCAPED quote, so the
        // string ran on and the record became an incomplete value — damage
        // wearing a torn write's clothes. It was dropped, its seq re-issued to a
        // different record, and the whole file verified clean.
        //
        // "Incomplete because bytes are MISSING from the end" and "incomplete
        // because a byte was ALTERED" are distinguishable, because an interrupted
        // write cannot add or change bytes: it can only stop early. So the bytes
        // it leaves are necessarily a byte-for-byte PREFIX of what Append would
        // have written. Anything else was overwritten after being flushed.
        return IsPlausibleWritePrefix(rest) ? LineResidue.TornPrefix : LineResidue.Damaged;
    }

    /// <summary>
    /// The exact byte template <see cref="Append"/> emits, as
    /// (literal, value-kind) pairs plus <see cref="RecordTemplateEnd"/>. The
    /// property order is the declaration order of
    /// <see cref="DecisionLedgerEntry"/>, which is what System.Text.Json writes
    /// for a positional record.
    /// <para>
    /// This duplicates knowledge the serializer owns, so it is pinned by a test
    /// that serializes a REAL entry and walks it through this template: if the
    /// record gains, loses or reorders a member, that test fails rather than this
    /// silently starting to call every torn write damage.
    /// </para>
    /// </summary>
    private static readonly (string Literal, TemplateValue Value)[] RecordTemplate =
    {
        ("{\"Seq\":", TemplateValue.Number),
        (",\"ItemId\":", TemplateValue.Text),
        (",\"Decision\":", TemplateValue.Text),
        (",\"Reason\":", TemplateValue.Text),
        (",\"Timestamp\":", TemplateValue.Text),
        (",\"PrevHash\":", TemplateValue.Hex64),
        (",\"Hash\":", TemplateValue.Hex64),
    };

    private const string RecordTemplateEnd = "}";

    /// <summary>How to scan one value slot of <see cref="RecordTemplate"/>.</summary>
    private enum TemplateValue
    {
        /// <summary>A non-negative integer: <see cref="DecisionLedgerEntry.Seq"/>.</summary>
        Number,

        /// <summary>An arbitrary JSON string.</summary>
        Text,

        /// <summary>
        /// A JSON string of exactly 64 LOWERCASE HEX characters — what
        /// <see cref="ComputeHash"/> (<c>Convert.ToHexString().ToLowerInvariant()</c>)
        /// and <see cref="GenesisHash"/> both produce, and the only shape either
        /// hash member can ever take. This is the constraint that catches the
        /// backslash case: no byte a write could have left inside a hash value is
        /// outside <c>[0-9a-f]</c>, so an escape opening there is damage.
        /// </summary>
        Hex64,
    }

    /// <summary>How far a value scan got before the bytes ran out or went wrong.</summary>
    private enum Scan
    {
        /// <summary>A byte that <see cref="Append"/> could not have written here.</summary>
        Invalid,

        /// <summary>The bytes ended mid-value — consistent with an interrupted write.</summary>
        Exhausted,

        /// <summary>The value was read in full; scanning continues with the next literal.</summary>
        Complete,
    }

    /// <summary>
    /// Is <paramref name="rest"/> a byte-for-byte PREFIX of some record
    /// <see cref="Append"/> could have serialized — i.e. is it what an
    /// interrupted write would leave?
    /// <para>
    /// True ONLY when the bytes run out partway through the template. A residue
    /// that completes the template is a whole value, not a prefix (and is already
    /// classified <see cref="LineResidue.Damaged"/> upstream, since a complete
    /// value that is not a record is damage); a residue that CONTRADICTS the
    /// template at any byte cannot have come from this writer at all.
    /// </para>
    /// <para>
    /// SOME CLAUSES BELOW ARE DEFENSIVE, NOT LOAD-BEARING, and are marked as
    /// such. This runs only on a residue the JSON reader has already called
    /// INCOMPLETE, so shapes that reader rejects outright — a raw control
    /// character in a string, a malformed <c>\u</c> escape, an empty number —
    /// never arrive here and are classified <see cref="LineResidue.Damaged"/>
    /// upstream. A mutation sweep confirmed those clauses can be removed without
    /// changing any observable behaviour. They are kept because they are cheap
    /// and because "reject what Append could not have written" is easier to
    /// reason about when it has no holes, but no test pins them and none can.
    /// </para>
    /// <para>Never throws.</para>
    /// </summary>
    private static bool IsPlausibleWritePrefix(ReadOnlySpan<byte> rest)
    {
        var pos = 0;
        foreach (var (literal, value) in RecordTemplate)
        {
            foreach (var expected in literal)
            {
                if (pos == rest.Length)
                    return true;
                if (rest[pos] != (byte)expected)
                    return false;
                pos++;
            }

            var scan = value switch
            {
                TemplateValue.Number => ScanNumber(rest, ref pos),
                TemplateValue.Hex64 => ScanHex64(rest, ref pos),
                _ => ScanText(rest, ref pos),
            };
            if (scan == Scan.Invalid)
                return false;
            if (scan == Scan.Exhausted)
                return true;
        }

        foreach (var expected in RecordTemplateEnd)
        {
            if (pos == rest.Length)
                return true;
            if (rest[pos] != (byte)expected)
                return false;
            pos++;
        }

        // The template was satisfied in full: a complete value, not a prefix.
        // DEFENSIVE: a residue that gets here is complete JSON, which the
        // complete-value branch of ClassifyResidue already called Damaged.
        return false;
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool IsLowerHex(byte b) =>
        IsDigit(b) || b is >= (byte)'a' and <= (byte)'f';

    /// <summary>Seq: one or more decimal digits. Append never issues a negative seq, so no sign.</summary>
    private static Scan ScanNumber(ReadOnlySpan<byte> rest, ref int pos)
    {
        if (pos == rest.Length)
            return Scan.Exhausted;
        // DEFENSIVE: an empty or non-numeric Seq is invalid JSON here, so the
        // reader has already rejected it upstream.
        if (!IsDigit(rest[pos]))
            return Scan.Invalid;
        while (pos < rest.Length && IsDigit(rest[pos]))
            pos++;
        return pos == rest.Length ? Scan.Exhausted : Scan.Complete;
    }

    /// <summary>A JSON string, tracking escapes so an escaped quote does not read as the end.</summary>
    private static Scan ScanText(ReadOnlySpan<byte> rest, ref int pos)
    {
        if (pos == rest.Length)
            return Scan.Exhausted;
        if (rest[pos] != (byte)'"')
            return Scan.Invalid;
        pos++;

        while (true)
        {
            if (pos == rest.Length)
                return Scan.Exhausted;
            var b = rest[pos];
            if (b == (byte)'"')
            {
                pos++;
                return Scan.Complete;
            }
            if (b == (byte)'\\')
            {
                pos++;
                if (pos == rest.Length)
                    return Scan.Exhausted;
                var esc = rest[pos];
                if (esc == (byte)'u')
                {
                    pos++;
                    for (var i = 0; i < 4; i++)
                    {
                        if (pos == rest.Length)
                            return Scan.Exhausted;
                        var h = rest[pos];
                        var hex = IsDigit(h)
                            || h is >= (byte)'a' and <= (byte)'f'
                            || h is >= (byte)'A' and <= (byte)'F';
                        // DEFENSIVE: a malformed \u escape is invalid JSON, so
                        // the reader has already rejected it upstream.
                        if (!hex)
                            return Scan.Invalid;
                        pos++;
                    }
                    continue;
                }
                if (esc is not ((byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b'
                    or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t'))
                {
                    return Scan.Invalid;
                }
                pos++;
                continue;
            }
            // DEFENSIVE (see the summary): the serializer escapes every control
            // character, and the JSON reader rejects a raw one before this runs.
            if (b < 0x20)
                return Scan.Invalid;
            pos++;
        }
    }

    /// <summary>
    /// A hash member: a quoted run of EXACTLY 64 lowercase hex characters.
    /// Length and alphabet are both enforced — an escape, an uppercase letter or
    /// a 65th character are all bytes this writer could not have produced here.
    /// </summary>
    private static Scan ScanHex64(ReadOnlySpan<byte> rest, ref int pos)
    {
        if (pos == rest.Length)
            return Scan.Exhausted;
        if (rest[pos] != (byte)'"')
            return Scan.Invalid;
        pos++;

        for (var i = 0; i < 64; i++)
        {
            if (pos == rest.Length)
                return Scan.Exhausted;
            if (!IsLowerHex(rest[pos]))
                return Scan.Invalid;
            pos++;
        }

        if (pos == rest.Length)
            return Scan.Exhausted;
        if (rest[pos] != (byte)'"')
            return Scan.Invalid;
        pos++;
        return Scan.Complete;
    }

    /// <summary>Index of the next plausible record start (a '{') at or after <paramref name="from"/>, or -1.</summary>
    private static int NextRecordStart(ReadOnlySpan<byte> line, int from)
    {
        for (var i = from; i < line.Length; i++)
            if (line[i] == (byte)'{')
                return i;
        return -1;
    }

    /// <summary>Try to read exactly one complete record at the start of <paramref name="span"/>.</summary>
    private static bool TryReadRecord(
        ReadOnlySpan<byte> span, out DecisionLedgerEntry? entry, out long consumed)
    {
        entry = null;
        consumed = 0;
        try
        {
            var reader = new Utf8JsonReader(span, new JsonReaderOptions { AllowMultipleValues = true });
            if (!reader.Read())
                return false;
            var candidate = JsonSerializer.Deserialize<DecisionLedgerEntry>(ref reader, JsonOptions);
            if (!IsCompleteRecord(candidate))
                return false;
            entry = candidate;
            consumed = reader.BytesConsumed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Read every COMPLETE record one physical line holds into
    /// <paramref name="into"/>, RESYNCHRONISING across damage rather than
    /// stopping at it, and report what the leftover bytes are.
    /// <para>
    /// Normally a line is exactly one record and <paramref name="bytesConsumed"/>
    /// is its whole length. A crash can lose the '\n' BETWEEN two flushed records
    /// and glue them into <c>{...}{...}</c>, or MANGLE it — replace it with a NUL
    /// (the classic allocated-but-unwritten block zero-fill), a stray byte, half
    /// a UTF-8 sequence, anything. Both were written, flushed and returned to the
    /// caller, so both are durable audit evidence and both must be seen.
    /// </para>
    /// <para>
    /// Stopping at the first byte that is not the start of a record is what made
    /// the mangled case catastrophic: everything from the bad byte to EOF fell
    /// outside the caller's committed mark and was truncated away, and the
    /// surviving prefix plus the next run's entries still verified CLEAN. So on a
    /// failure this scans FORWARD to the next plausible record start and keeps
    /// going. Every record it can still parse is recovered no matter how many
    /// bytes in front of it were destroyed; a record the damage landed INSIDE is
    /// genuinely unrecoverable, and skipping it leaves a seq GAP that
    /// <see cref="Verify(IReadOnlyList{DecisionLedgerEntry}, long, string)"/>
    /// reports — loud, which is the whole point, rather than a silent deletion.
    /// </para>
    /// <para>
    /// <paramref name="resyncGaps"/> counts damaged regions the scan stepped over
    /// to reach a later record (0 for a merely LOST terminator, which needs no
    /// resync). <paramref name="residue"/> classifies the bytes after the last
    /// record so the caller can tell a torn write from damage.
    /// </para>
    /// <para>
    /// Nothing here throws.
    /// </para>
    /// </summary>
    private static void ScanRecords(
        ReadOnlySpan<byte> line,
        List<DecisionLedgerEntry> into,
        out long bytesConsumed,
        out int resyncGaps,
        out LineResidue residue)
    {
        bytesConsumed = 0;
        resyncGaps = 0;
        var pos = 0;
        var pendingGap = false;

        while (true)
        {
            while (pos < line.Length && IsJsonWhitespace(line[pos]))
                pos++;
            if (pos >= line.Length)
                break;

            if (TryReadRecord(line[pos..], out var entry, out var consumed) && consumed > 0)
            {
                into.Add(entry!);
                pos += (int)consumed;
                bytesConsumed = pos;
                if (pendingGap)
                {
                    resyncGaps++;
                    pendingGap = false;
                }
                continue;
            }

            // Not a record here. Look for the next one; pos strictly increases,
            // so this terminates however pathological the bytes are. Cost is one
            // parse ATTEMPT per candidate '{', and a candidate that is not a
            // record fails within a few tokens, so this is linear in practice —
            // measured at ~1s for a line carrying 200k record-shaped prefixes.
            // Deliberately NOT capped: a large zero-filled block can legitimately
            // mangle thousands of separators onto one line, and a cap there would
            // stop recovering real records partway through.
            var next = NextRecordStart(line, pos + 1);
            if (next < 0)
                break;
            pendingGap = true;
            pos = next;
        }

        residue = ClassifyResidue(line[(int)bytesConsumed..]);
    }

    /// <summary>Convenience overload for callers that only want the records.</summary>
    private static void ScanRecords(
        ReadOnlySpan<byte> line, List<DecisionLedgerEntry> into, out long bytesConsumed) =>
        ScanRecords(line, into, out bytesConsumed, out _, out _);

    private static void Truncate(string path, long keepBytes, long totalBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(keepBytes);
            Logger.Warning(
                $"Decision ledger {path}: discarded {totalBytes - keepBytes} byte(s) of torn, uncommitted "
                + "trailing data (crash-tail) before resuming. No complete record was affected.");
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"Decision ledger {path}: could not trim the torn trailing fragment ({exc.Message}); "
                + "appending after it — the fragment will read as interior corruption.");
        }
    }

    /// <summary>Entries appended so far (snapshot copy).</summary>
    public IReadOnlyList<DecisionLedgerEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>
    /// Append one decision, linking it to the chain and (when file-backed)
    /// flushing it to disk. Thread-safe: appends are serialized so the chain
    /// order is well-defined even under concurrent crawl workers. Returns the
    /// entry that was written.
    /// <para>
    /// SEQ EXHAUSTION throws. Every seq this method issues is in
    /// <c>[0, <see cref="MaxSeq"/>]</c>; once the counter reaches
    /// <c>long.MaxValue</c> there is no next seq, and the only alternatives to
    /// throwing are to wrap (which writes a NEGATIVE seq into a tamper-evident
    /// chain that then still verifies — the shipped defect) or to reuse a seq
    /// (which re-issues an identifier a live record already holds). Both are
    /// silent corruption of the audit trail, so this fails loudly instead.
    /// Unreachable in practice: it takes 2^63 recorded decisions to get here.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The seq space is exhausted.</exception>
    public DecisionLedgerEntry Append(string itemId, string decision, string reason)
    {
        lock (_lock)
        {
            if (_seq > MaxSeq)
            {
                throw new InvalidOperationException(
                    $"Decision ledger {FilePath ?? "(in-memory)"}: the seq space is exhausted (the next seq "
                    + $"would be past {MaxSeq}). Refusing to append: wrapping would write a negative seq "
                    + "into the chain and Verify() could not see it.");
            }
            var seq = _seq++;
            var timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var prevHash = _lastHash;
            var hash = ComputeHash(seq, itemId ?? "", decision ?? "", reason ?? "", timestamp, prevHash);
            var entry = new DecisionLedgerEntry(
                seq, itemId ?? "", decision ?? "", reason ?? "", timestamp, prevHash, hash);
            _entries.Add(entry);
            _lastHash = hash;

            if (_writer is not null)
            {
                _writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                _writer.Flush();
            }
            return entry;
        }
    }

    /// <summary>The canonical hash over one entry's fields plus the previous hash.</summary>
    public static string ComputeHash(
        long seq, string itemId, string decision, string reason, string timestamp, string prevHash)
    {
        // Length-prefixed field join so no combination of field values can be
        // rearranged to collide (e.g. itemId="a|b" vs itemId="a", decision="b").
        var sb = new StringBuilder();
        AppendField(sb, seq.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, itemId);
        AppendField(sb, decision);
        AppendField(sb, reason);
        AppendField(sb, timestamp);
        AppendField(sb, prevHash);
        var hash = SHA256.HashData(Utf8NoBom.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder sb, string value)
    {
        sb.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        sb.Append(':');
        sb.Append(value);
        sb.Append('|');
    }

    /// <summary>
    /// Verify a chain: seqs are contiguous from <paramref name="expectedFirstSeq"/>,
    /// each entry's hash recomputes from its fields, and each links to the
    /// previous entry's hash (the first to <paramref name="expectedPrevHash"/>).
    /// Returns the first break found.
    /// <para>
    /// The defaults verify a WHOLE chain read off disk — seqs from 0, first link
    /// to <see cref="GenesisHash"/> — which is what an auditor does with
    /// <see cref="ReadFile"/>. The parameters exist for verifying a SEGMENT of a
    /// longer chain, e.g. the entries one resumed run appended.
    /// </para>
    /// </summary>
    public static LedgerVerification Verify(
        IReadOnlyList<DecisionLedgerEntry> entries,
        long expectedFirstSeq = 0,
        string? expectedPrevHash = null)
    {
        var prevHash = expectedPrevHash ?? GenesisHash;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var expectedSeq = expectedFirstSeq + i;
            if (e.Seq != expectedSeq)
                return LedgerVerification.Broken(e.Seq, $"seq out of order at index {i} (got {e.Seq}, expected {expectedSeq})");
            if (!string.Equals(e.PrevHash, prevHash, StringComparison.Ordinal))
                return LedgerVerification.Broken(e.Seq, "prevHash does not match the prior entry's hash (chain broken)");
            var recomputed = ComputeHash(e.Seq, e.ItemId, e.Decision, e.Reason, e.Timestamp, e.PrevHash);
            if (!string.Equals(e.Hash, recomputed, StringComparison.Ordinal))
                return LedgerVerification.Broken(e.Seq, "entry hash does not match its contents (tampered)");
            prevHash = e.Hash;
        }
        return LedgerVerification.Ok;
    }

    /// <summary>
    /// Verify the entries THIS instance appended, against the chain anchor it
    /// started from. For a fresh chain that is the whole chain; for a resumed
    /// file-backed ledger it is this run's segment, checked to link correctly
    /// onto the persisted history. Use <see cref="ReadFile"/> +
    /// <see cref="Verify(IReadOnlyList{DecisionLedgerEntry}, long, string)"/> to
    /// verify the full on-disk chain.
    /// </summary>
    public LedgerVerification Verify()
    {
        lock (_lock)
            return Verify(_entries, _baseSeq, _baseHash);
    }

    /// <summary>
    /// Re-read a persisted ledger file into entries (for offline verification).
    /// Tolerant of a TORN FINAL LINE — the normal crash-tail of an append-only
    /// ledger flushed per line: a final line that is an INCOMPLETE JSON value is
    /// skipped (with a warning) so an auditor can still read the intact prefix,
    /// and its chain still verifies. "Torn" means incomplete and nothing else: a
    /// final line that is COMPLETE JSON the record contract rejects is damage,
    /// not a crash-tail, and is refused. An unparseable INTERIOR line is genuine corruption,
    /// not a clean crash-tail, so it is surfaced (thrown) rather than silently
    /// dropped — dropping it would also punch a hole Verify could not see.
    /// <para>
    /// A line that holds SEVERAL records glued together by a lost '\n' (shape 4
    /// in <see cref="ResumeTail"/>) is split back into them rather than refused:
    /// every one of those records was flushed and acknowledged, and they hash
    /// and chain exactly as they always did, so a lost terminator hides nothing
    /// — refusing the file would instead make the records <c>ResumeTail</c>
    /// preserved unreachable to the auditor for the life of the ledger.
    /// </para>
    /// <para>
    /// A line that deserializes to an entry with NULL members ("{}", or an
    /// object missing fields) is malformed input, NOT a record: returning one
    /// would detonate inside <see cref="Verify(IReadOnlyList{DecisionLedgerEntry}, long, string)"/>'s
    /// <see cref="ComputeHash"/> as a NullReferenceException.
    /// </para>
    /// <para>
    /// THE RULE THAT DECIDES WHAT IS THROWN: this refuses the file exactly when
    /// the damage would otherwise be INVISIBLE TO <c>Verify</c>. Damage the scan
    /// can resynchronise over is not refused — every record on both sides is
    /// returned, and if a record was destroyed outright the seq gap it leaves is
    /// something <c>Verify</c> reports, so the auditor is told either way and the
    /// file stays readable. Damage with NO record behind it is refused, because
    /// nothing downstream could ever see it — and that is what covers the LAST
    /// record, for which no seq gap can ever exist. A merely INTERRUPTED write —
    /// an INCOMPLETE JSON value, the only residue a partial flush can leave — as
    /// the final line is still tolerated: it was never acknowledged to anyone.
    /// </para>
    /// <para>
    /// <paramref name="damage"/> reports what was found even when nothing is
    /// thrown, so a WORM / integrity monitor can act on a physically damaged
    /// ledger whose chain still verifies.
    /// </para>
    /// </summary>
    public static List<DecisionLedgerEntry> ReadFile(string path) => ReadFile(path, out _);

    /// <summary>
    /// The file's PHYSICAL lines, split on the NEWLINE BYTE and nothing else.
    /// <para>
    /// Deliberately not <see cref="File.ReadLines(string)"/>, which also breaks a
    /// line on a lone CR. <see cref="Append"/> only ever terminates a record with
    /// '\n', so a lone CR sitting between two records means that '\n' was
    /// OVERWRITTEN — the mangled-separator shape, i.e. damage. ReadLines healed
    /// it into an ordinary line break, so this reader reported the file CLEAN
    /// while <see cref="ResumeTail"/> (which scans raw bytes) reported a glued
    /// line: a disagreement between the two readers of a ledger, which
    /// <see cref="ResumedRecordCount"/> documents as a defect. Splitting on the
    /// same byte the writer writes removes the disagreement at its source.
    /// </para>
    /// <para>
    /// A UTF-8 BOM at offset 0 is stripped, exactly as ReadLines and
    /// <see cref="ResumeTail"/> both do, so all three agree about record 1. A CR
    /// that is part of a CRLF is still handled downstream by
    /// <see cref="TrimTrailingWhitespace"/>, so CRLF files read as before.
    /// </para>
    /// </summary>
    private static IEnumerable<byte[]> PhysicalLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffered = new BufferedStream(stream);
        var line = new List<byte>(256);
        var atFileStart = true;
        int b;
        while ((b = buffered.ReadByte()) >= 0)
        {
            if (b == '\n')
            {
                atFileStart = false;
                yield return line.ToArray();
                line.Clear();
                continue;
            }
            line.Add((byte)b);
            if (atFileStart && line.Count == 3)
            {
                atFileStart = false;
                if (line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
                    line.Clear();
            }
        }
        if (line.Count > 0)
            yield return line.ToArray();
    }

    /// <inheritdoc cref="ReadFile(string)"/>
    public static List<DecisionLedgerEntry> ReadFile(string path, out LedgerFileDamage damage)
    {
        var entries = new List<DecisionLedgerEntry>();
        // A non-blank line whose (remaining) bytes are not a record, held pending
        // the question "was it the last content line?" — only then is it a
        // tolerable crash-tail.
        JsonException? deferred = null;
        var glued = 0;
        var resynced = 0;
        var lineNumber = 0;
        var damagedTail = false;
        foreach (var line in PhysicalLines(path))
        {
            lineNumber++;
            var content = TrimTrailingWhitespace(line);
            if (content.IsEmpty)
                continue;
            if (deferred is not null)
            {
                // A malformed line was followed by MORE content → it is interior
                // corruption, not a crash-tail. Surface it.
                throw new JsonException(
                    $"Decision ledger {path}: malformed interior record (not the final line) — "
                    + $"the ledger is corrupt, not merely crash-truncated ({deferred.Message}).",
                    deferred);
            }

            var recovered = new List<DecisionLedgerEntry>(1);
            ScanRecords(content, recovered, out var consumed, out var gaps, out var residue);
            if (recovered.Count > 1)
                glued++;
            resynced += gaps;
            entries.AddRange(recovered);
            damagedTail = residue == LineResidue.Damaged;
            if (consumed != content.Length)
                deferred = new JsonException(
                    recovered.Count == 0
                        ? $"line {lineNumber} is not a decision-ledger record"
                        : $"line {lineNumber} holds {recovered.Count} record(s) followed by "
                          + "bytes that are not a record");
        }

        damage = new LedgerFileDamage(glued, resynced, damagedTail);

        if (glued > 0)
            Logger.Warning(
                $"Decision ledger {path}: {glued} line(s) held several records glued together by a lost "
                + "line terminator. Every record was recovered and the chain is returned intact, but the "
                + "file itself is damaged — investigate.");
        if (resynced > 0)
            Logger.Error(
                $"Decision ledger {path}: {resynced} region(s) of DESTROYED bytes were stepped over to "
                + "resynchronise on the records behind them — bytes that had been flushed were OVERWRITTEN. "
                + "Every record still parseable is returned; a record the damage landed inside leaves a seq "
                + "gap that Verify() reports. Investigate this file.");

        if (damagedTail)
        {
            // Damage with nothing behind it: no seq gap, no later line, nothing
            // Verify could ever notice. Refusing the file is the ONLY way the
            // auditor learns of it, so refuse.
            throw new JsonException(
                $"Decision ledger {path}: the final line ends in DAMAGED bytes — invalid where they sit, so "
                + "they were overwritten after being flushed rather than merely half-written. This is not a "
                + "crash-tail and it is not something Verify() can see, so the file is refused rather than "
                + $"read as clean ({deferred?.Message ?? $"line {lineNumber}"}).",
                deferred);
        }

        if (deferred is not null)
            Logger.Warning(
                $"Decision ledger {path}: torn/partial final line skipped (crash-tail: {deferred.Message}) — "
                + "the intact prefix is returned and its chain still verifies.");
        return entries;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer?.Dispose();
        }
    }
}
