// State/StateContract.cs
// ----------------------
// Shared, NON-REJECTING normalisation for connector state, plus the column
// widths of the SQL schema as constants.
//
// HISTORY, and why this file no longer validates anything.
//
// A previous round added write-side validation here: every identifier and key
// heading for either backend was checked against the SQL column's accepted
// domain (bounded length, no unpaired UTF-16 surrogates, no blank padding) and
// a value outside it was REJECTED with a thrown StateContractViolation, on both
// backends, before any I/O.
//
// That was a regression, and it was withdrawn. The reason is the asymmetry it
// created. Reads are deliberately unfiltered — state written before the
// validation existed must still read back, because refusing to read a legacy
// dead-letter line turns it into a LOST failure record. But every WRITE
// canonicalised the whole batch all-or-nothing. So a queue holding a single
// legacy out-of-domain record — which the file backend, having no length bound,
// had happily accepted — could be READ but not WRITTEN BACK UNCHANGED, and
// every read-modify-write over that queue was wedged:
//
//     CommandRegistry's forget-subject scrub threw StateContractViolation
//     before writing anything, leaving the erased subject's payload on disk.
//     And because AddSuppressedSubject had already run, the erasure was left
//     HALF-APPLIED: subject marked suppressed, payload still at rest. Nothing
//     under src/ ever caught StateContractViolation, so this surfaced as an
//     unhandled throw. retry-failed's finalize and attempt-bump wedged the same
//     way.
//
// A DSAR erasure that cannot complete is worse than the silent-divergence
// defects the validation was meant to close, so the validation is gone rather
// than replaced.
//
// WHAT REMAINS HERE is only what cannot throw:
//
//   * The column-width constants, kept because the SQL schema tests pair each
//     bounded column in the DDL against a constant here, so widening one
//     without the other fails the suite.
//   * Text() — free-text normalisation for the NVARCHAR(MAX) columns (error,
//     payload JSON, KV values). Unpaired surrogates become U+FFFD, which is
//     exactly what System.Text.Json already does on the file backend, so the
//     SQL backend persists the identical replacement. This never rejected
//     anything and is not part of the withdrawn validation.
//   * Utc() — DateTimeKind normalisation. The file backend round-trips Kind
//     through ISO-8601 'o'; DATETIME2 carries no Kind and SqlDataReader hands
//     back Unspecified. Applied on READ as well as write, and it never rejects.
//
// KNOWN, ACCEPTED, OPEN DIVERGENCES — reintroduced by withdrawing the
// validation. These are NOT closed. See docs/SQL_CONTRACT.md.
//
//   (a) UNPAIRED SURROGATE IN A SUBJECT ID. System.Text.Json cannot encode one
//       and substitutes U+FFFD, so the FILE backend silently rewrites the id it
//       was given. AddSuppressedSubject("ALT-\uD83D-9001") then
//       IsSubjectSuppressed(THE SAME STRING) returns FALSE on the same store
//       instance — the DSAR erasure is filed under a different id and THE
//       SUBJECT STAYS INGESTIBLE. NVARCHAR is UCS-2 and stores the unpaired
//       code unit verbatim, so SQL answers TRUE. OPEN.
//
//   (b) LENGTH. subject_id / item_id / delivery_id are NVARCHAR(256), [key] is
//       NVARCHAR(128), dataset is NVARCHAR(64); the file backend has no bound.
//       An over-long id erases SUCCESSFULLY on file and raises SQL error 8152
//       ("String or binary data would be truncated") on SQL. 8152 is not in
//       TransientErrorNumbers, so it is rethrown without retry and the erasure
//       FAILS. OPEN.
//
//   (c) BLANK PADDING. SQL Server's `=` on (n)varchar pads the shorter operand
//       with trailing spaces, so 'ALT-1' and 'ALT-1 ' may be ONE subject on SQL
//       and are always TWO on file; '' and ' ' likewise. (Whether binary
//       collations are exempt is disputed in the wild and this file no longer
//       takes a position.) OPEN.

using System.Text;

namespace AltrataConnector.State;

public static class StateContract
{
    // ---- column bounds, mirrored from SqlStateStore.SchemaScript -------------
    //
    // NVARCHAR(n) counts UCS-2 code units, which is exactly what string.Length
    // counts — a surrogate PAIR costs 2 against both. The pairing is
    // test-enforced against the parsed DDL (StateContractTests), so a column
    // widened in the schema without widening the constant here fails the
    // build's test run.
    //
    // NOTE: these are DOCUMENTATION of the SQL schema, not enforcement. Nothing
    // checks a value against them any more — see (b) above.

    public const int ConnectorIdMax = 64;
    public const int SubjectIdMax = 256;
    public const int ItemIdMax = 256;
    public const int DeliveryIdMax = 256;
    public const int DatasetMax = 64;
    public const int FileNameMax = 512;
    public const int KeyMax = 128;
    public const int OpMax = 16;
    public const int CorrelationIdMax = 128;
    public const int SubjectHashMax = 64;
    public const int LeaseNameMax = 128;
    public const int LeaseOwnerMax = 128;

    // ---- non-rejecting normalisation ----------------------------------------

    /// <summary>True when every surrogate code unit in <paramref name="value"/>
    /// is part of a well-formed pair — i.e. when the string survives a UTF-8 /
    /// JSON round trip unchanged.</summary>
    public static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    return false;
                i++;               // consume the pair
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;      // a low surrogate with no high before it
            }
        }
        return true;
    }

    /// <summary>Free text destined for an NVARCHAR(MAX) column. NORMALISED,
    /// never rejected: unpaired surrogates become U+FFFD — precisely the
    /// substitution System.Text.Json already performs on the file backend — so
    /// the SQL backend stores the identical replacement and the two agree. Null
    /// becomes "" so a nullable-vs-empty distinction cannot diverge either.
    ///
    /// Applies to error strings, captured payload JSON and KV values only.
    /// These are not identity: a dead-letter write must not be able to fail over
    /// an error MESSAGE, because that turns a recoverable item failure into a
    /// lost failure record.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (IsWellFormedUtf16(value))
            return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c).Append(value[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append('�');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Normalize a timestamp to Kind=Utc. The file backend round-trips
    /// Kind through ISO-8601 'o'; DATETIME2 carries no Kind at all and
    /// SqlDataReader.GetDateTime hands back Unspecified. Everything the
    /// connector stores here is already a UTC instant, so stamping the Kind
    /// makes the two backends return equal DateTime values rather than values
    /// that merely happen to have the same ticks. Applied on read and write
    /// alike; never rejects.</summary>
    public static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    // ---- composed keys ------------------------------------------------------

    /// <summary>Prefix SqlStateStore composes onto the sync-timestamp kind to
    /// make a KV key. The file backend keys its own dictionary by the BARE kind
    /// instead, so the two live in different observable namespaces — see the
    /// last-sync note in docs/SQL_CONTRACT.md.</summary>
    public const string LastSyncKeyPrefix = "last_sync_";

    /// <summary>The KV key SqlStateStore uses for a sync-timestamp kind.</summary>
    public static string LastSyncKey(string kind) => LastSyncKeyPrefix + kind;

    // ---- whole-record normalisation -----------------------------------------

    /// <summary>The stored form of a dead-letter record: free text normalised,
    /// timestamp Kind stamped. NOTHING IS REJECTED and nothing is bounded — a
    /// record read from a legacy queue file can always be written back
    /// unchanged, which is what read-modify-write (the forget-subject scrub,
    /// retry-failed's finalize and attempt bump) depends on.</summary>
    public static DeadLetterRecord Storable(DeadLetterRecord record) => record with
    {
        Error = Text(record.Error),
        PayloadJson = Text(record.PayloadJson),
        FailedUtc = Utc(record.FailedUtc),
    };

    /// <summary>The stored form of a checkpoint: timestamp Kind stamped.
    /// Nothing is rejected.</summary>
    public static CrawlCheckpoint Storable(CrawlCheckpoint checkpoint) => checkpoint with
    {
        UpdatedUtc = Utc(checkpoint.UpdatedUtc),
    };
}
