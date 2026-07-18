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
// File-backed JSONL (like the reconciliation / classification manifests) when a
// path is given; in-memory (counts + chain only, no file) otherwise, so callers
// can attach a no-op ledger unconditionally.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SeismicConnector.Infrastructure;

/// <summary>One immutable, hash-chained decision record.</summary>
public sealed record DecisionLedgerEntry(
    long Seq,
    string ItemId,
    string Decision,
    string Reason,
    string Timestamp,
    string PrevHash,
    string Hash);

/// <summary>Result of verifying a ledger chain.</summary>
public readonly record struct LedgerVerification(bool Valid, long? FirstBrokenSeq, string? Detail)
{
    public static readonly LedgerVerification Ok = new(true, null, null);

    public static LedgerVerification Broken(long seq, string detail) => new(false, seq, detail);
}

public sealed class DecisionLedger : IDisposable
{
    /// <summary>Opt-in gate: DECISION_LEDGER=true writes a file-backed ledger per run.</summary>
    public const string EnvVar = "DECISION_LEDGER";

    /// <summary>Well-known decision kinds (free-form is allowed, these are the ones the pipeline emits).</summary>
    public const string DecisionExclude = "exclude";
    public const string DecisionAclRestrict = "acl-restrict";

    /// <summary>Genesis link for the first entry (64 hex zeros).</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.ledger");

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

    public string? FilePath { get; }

    /// <summary>Open a file-backed ledger at <paramref name="filePath"/> (parent dirs hardened owner-only).</summary>
    public DecisionLedger(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            SecureDirectory.EnsureHardened(dir);
        _writer = new StreamWriter(filePath, append: false, Utf8NoBom);
    }

    /// <summary>In-memory ledger (chain only, no file) — the default no-op attachment and test seam.</summary>
    public DecisionLedger()
    {
        FilePath = null;
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
    /// </summary>
    public DecisionLedgerEntry Append(string itemId, string decision, string reason)
    {
        lock (_lock)
        {
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
    /// Verify a chain: seqs are 0..n-1 contiguous, each entry's hash recomputes
    /// from its fields, and each links to the previous entry's hash (the first
    /// to <see cref="GenesisHash"/>). Returns the first break found.
    /// </summary>
    public static LedgerVerification Verify(IReadOnlyList<DecisionLedgerEntry> entries)
    {
        var prevHash = GenesisHash;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Seq != i)
                return LedgerVerification.Broken(e.Seq, $"seq out of order at index {i} (got {e.Seq})");
            if (!string.Equals(e.PrevHash, prevHash, StringComparison.Ordinal))
                return LedgerVerification.Broken(e.Seq, "prevHash does not match the prior entry's hash (chain broken)");
            var recomputed = ComputeHash(e.Seq, e.ItemId, e.Decision, e.Reason, e.Timestamp, e.PrevHash);
            if (!string.Equals(e.Hash, recomputed, StringComparison.Ordinal))
                return LedgerVerification.Broken(e.Seq, "entry hash does not match its contents (tampered)");
            prevHash = e.Hash;
        }
        return LedgerVerification.Ok;
    }

    /// <summary>Verify this ledger's in-memory chain.</summary>
    public LedgerVerification Verify()
    {
        lock (_lock)
            return Verify(_entries);
    }

    /// <summary>
    /// Re-read a persisted ledger file into entries (for offline verification).
    /// Tolerant of a TORN FINAL LINE — the normal crash-tail of an append-only
    /// ledger flushed per line: a partial/unterminated last record is skipped
    /// (with a warning) so an auditor can still read the intact prefix, and its
    /// chain still verifies. An unparseable INTERIOR line is genuine corruption,
    /// not a clean crash-tail, so it is surfaced (thrown) rather than silently
    /// dropped — dropping it would also punch a hole Verify could not see.
    /// </summary>
    public static List<DecisionLedgerEntry> ReadFile(string path)
    {
        var entries = new List<DecisionLedgerEntry>();
        // A non-blank line that failed to parse, held pending the question
        // "was it the last content line?" — only then is it a tolerable crash-tail.
        JsonException? deferred = null;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
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
            DecisionLedgerEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<DecisionLedgerEntry>(line, JsonOptions);
            }
            catch (JsonException exc)
            {
                deferred = exc;   // tolerate only if nothing non-blank follows
                continue;
            }
            if (entry is not null)
                entries.Add(entry);
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
