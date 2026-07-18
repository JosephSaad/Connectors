// Altrata/HashChainedLedger.cs
// ----------------------------
// Append-only, SHA-256 hash-chained ledger — the tamper-evident substrate
// shared by the erasure ledger (per-subject DSAR removals, ErasureLedger.cs)
// and the decision ledger (exclusion / ACL-restriction decisions,
// DecisionLedger.cs). Generalized from the original ErasureLedger so both
// compliance records share ONE audited chain implementation.
//
// Tamper evidence: entries form a SHA-256 hash chain. Each entry's Hash covers
// its own fields plus the previous entry's Hash, so editing, reordering or
// deleting any entry breaks every subsequent link — Verify() detects it without
// a trusted external store. The file is only ever opened in append mode; no code
// path rewrites earlier lines, and a chain that cannot be parsed is NEVER
// appended to (that would bury the tamper under fresh entries).
//
// Subclasses supply the concrete entry type, the canonical hash, the
// chain-field accessors and the ledger-specific naming/metrics; the append/read/
// verify mechanics (tail cache, corrupt-chain refusal, tolerant line reader)
// live here once.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AltrataConnector.Infrastructure;

namespace AltrataConnector.Altrata;

public abstract class HashChainedLedger<TEntry> where TEntry : class
{
    /// <summary>Genesis PrevHash — 64 hex zeros.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    protected static readonly JsonSerializerOptions JsonlOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();

    /// <summary>
    /// Chain-tail cache: (entry count, last hash, file length at cache time).
    /// Without it Append re-read and re-parsed the WHOLE file per call, making
    /// an N-entry ledger O(N²); with the cache each append is O(1). The cache is
    /// validated against the current file length before every use, so another
    /// instance appending to the same file (sequential cross-instance use, as
    /// commands in separate processes do) or an external truncation triggers a
    /// full strict reload — byte-identical chaining semantics to the uncached
    /// path. Guarded by _sync.
    /// </summary>
    private (int Count, string LastHash, long FileLength)? _tail;

    public string Path { get; }

    protected HashChainedLedger(string path) => Path = path;

    // ---- subclass hooks -------------------------------------------------------

    protected abstract IAppLogger Logger { get; }

    /// <summary>Human name used in messages, e.g. "Erasure ledger".</summary>
    protected abstract string LedgerName { get; }

    /// <summary>Prometheus gauge set to 1 when the last verify failed, else 0.</summary>
    protected abstract string BrokenMetricName { get; }

    protected abstract long SeqOf(TEntry entry);
    protected abstract string PrevHashOf(TEntry entry);
    protected abstract string HashOf(TEntry entry);

    /// <summary>Recompute the canonical hash of an entry from its stored fields
    /// plus its PrevHash — MUST be byte-identical to what Append computed.</summary>
    protected abstract string RecomputeHash(TEntry entry);

    protected abstract TEntry? Deserialize(string line);

    /// <summary>SHA-256 hex (lowercase) of a canonical string — the chain primitive.</summary>
    protected static string Sha256Hex(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    // ---- append ---------------------------------------------------------------

    /// <summary>
    /// Append one entry. <paramref name="buildEntry"/> constructs the fully
    /// formed entry (computing its own Hash) from the freshly determined
    /// seq / timestamp / correlation id / prevHash. Refuses to append to a
    /// corrupt chain (fail-closed); <paramref name="refusalDescriptor"/> names
    /// what was being appended in that (loud) refusal message.
    /// </summary>
    protected TEntry AppendCore(string refusalDescriptor,
        Func<long, DateTime, string?, string, TEntry> buildEntry)
    {
        lock (_sync)
        {
            var currentLength = File.Exists(Path) ? new FileInfo(Path).Length : 0L;
            if (_tail == null || _tail.Value.FileLength != currentLength)
            {
                // Cold cache, or the file changed under us (another instance
                // appended / external truncation): strict full reload. A chain
                // that cannot be parsed must never be appended to.
                var existing = ReadEntries(out var firstBadLine);
                if (firstBadLine != 0)
                {
                    Metrics.Set(BrokenMetricName, 1);
                    Logger.Error(
                        $"{LedgerName} '{Path}' line {firstBadLine} is unreadable — REFUSING to append " +
                        $"{refusalDescriptor} to a corrupt chain. Restore the ledger, then re-run.");
                    throw new InvalidDataException(
                        $"{LedgerName} '{Path}' line {firstBadLine} is unreadable — refusing to " +
                        "append to a corrupt chain. Run verify and restore the ledger first.");
                }
                _tail = (existing.Count, existing.Count > 0 ? HashOf(existing[^1]) : GenesisHash, currentLength);
            }

            var seq = _tail.Value.Count + 1;
            var prevHash = _tail.Value.LastHash;
            var timestamp = DateTime.UtcNow;
            var correlationId = CorrelationContext.Current;
            var entry = buildEntry(seq, timestamp, correlationId, prevHash);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using (var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(JsonSerializer.Serialize(entry, JsonlOptions));
            }
            _tail = (seq, HashOf(entry), new FileInfo(Path).Length);
            return entry;
        }
    }

    // ---- read / verify --------------------------------------------------------

    public IReadOnlyList<TEntry> ReadAll()
    {
        lock (_sync)
        {
            var entries = ReadEntries(out var firstBadLine);
            if (firstBadLine != 0)
            {
                Logger.Error(
                    $"{LedgerName} '{Path}' line {firstBadLine} is unreadable/tampered — ReadAll refused.");
                throw new InvalidDataException(
                    $"{LedgerName} '{Path}' line {firstBadLine} is unreadable/tampered — " +
                    "use Verify() to check integrity without throwing.");
            }
            return entries;
        }
    }

    /// <summary>
    /// Tolerant line reader: parses entries up to the first unreadable line;
    /// firstBadLine is that line's 1-based number (0 = whole file parsed).
    /// Blank lines are skipped; an unparseable line stops the scan so Verify
    /// can pinpoint the break instead of throwing on tampered bytes.
    /// </summary>
    protected List<TEntry> ReadEntries(out int firstBadLine)
    {
        firstBadLine = 0;
        var entries = new List<TEntry>();
        if (!File.Exists(Path))
            return entries;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(Path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            TEntry? entry;
            try
            {
                entry = Deserialize(line);
            }
            catch (JsonException)
            {
                entry = null;
            }
            if (entry == null)
            {
                firstBadLine = lineNumber;
                break;
            }
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Verify the whole chain. Returns true when every entry's recomputed hash
    /// matches and links to its predecessor; on failure, brokenAtSeq is the
    /// first entry whose integrity check failed (0 when the file is empty/absent).
    /// NEVER throws on tampered content — a line that no longer parses as an
    /// entry (structural corruption) reports the chain as broken at that line,
    /// exactly like a hash mismatch does.
    /// </summary>
    public bool Verify(out int brokenAtSeq)
    {
        lock (_sync)
        {
            var entries = ReadEntries(out var firstBadLine);
            var prevHash = GenesisHash;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var expectedSeq = i + 1;
                var recomputed = RecomputeHash(entry);
                if (SeqOf(entry) != expectedSeq || PrevHashOf(entry) != prevHash || HashOf(entry) != recomputed)
                {
                    brokenAtSeq = (int)SeqOf(entry);
                    Metrics.Set(BrokenMetricName, 1);
                    Logger.Error(
                        $"{LedgerName} '{Path}' FAILED verification: chain broken at seq {brokenAtSeq} " +
                        "(sequence/link/hash mismatch — the entry or an earlier one was edited, reordered or deleted).");
                    return false;
                }
                prevHash = HashOf(entry);
            }
            if (firstBadLine != 0)
            {
                brokenAtSeq = firstBadLine;   // unreadable line = broken chain link
                Metrics.Set(BrokenMetricName, 1);
                Logger.Error(
                    $"{LedgerName} '{Path}' FAILED verification: chain broken at seq {brokenAtSeq} " +
                    "(line does not parse as a ledger entry).");
                return false;
            }
            brokenAtSeq = 0;
            Metrics.Set(BrokenMetricName, 0);
            return true;
        }
    }
}
