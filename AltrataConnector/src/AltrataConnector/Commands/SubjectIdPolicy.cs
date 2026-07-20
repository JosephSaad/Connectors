// Commands/SubjectIdPolicy.cs
// ---------------------------
// Operator-input validation for subject ids — COMMAND LAYER ONLY.
//
// This closes the cross-backend subject-id divergence (unpaired UTF-16
// surrogates rewritten to U+FFFD by the file backend; NVARCHAR(256) overflow
// raising un-retried SQL error 8152 on the SQL backend) at the ONE point where
// a new subject id enters the system from an operator: the `forget-subject
// --id` argument, validated in CommandRegistry.ForgetSubjectAsync before any
// state is touched.
//
// It is deliberately NOT wired into IStateStore.AddSuppressedSubject or any
// other state write. A previous round validated every write; a dead-letter
// queue holding one legacy out-of-domain record then wedged every
// read-modify-write over it (the forget-subject scrub, retry-failed's finalize
// and attempt bumps) and left a DSAR erasure HALF-APPLIED. See the history in
// State/StateContract.cs. The rule that survived: VALIDATE where new operator
// input arrives, TOLERATE everywhere state is read, replayed or deleted —
// including ids resolved from the crosswalk by `forget-subject --email`
// (already-stored state) and the id given to `unsuppress-subject` (an operator
// must be able to remove a legacy bad entry).
//
// WHITESPACE is deliberately not a clause here: both commands .Trim() the
// operator-supplied id before use, so padded operator input is normalised
// rather than refused, and an all-whitespace id is already rejected by the
// IsNullOrWhiteSpace guard. Blank-padding of LEGACY state (SQL_CONTRACT.md
// divergence (c)) remains open and is out of this policy's reach by design.

using System.Text;
using System.Text.RegularExpressions;
using AltrataConnector.State;

namespace AltrataConnector.Commands;

internal static class SubjectIdPolicy
{
    /// <summary>The declared width of dbo.altrata_suppressed.subject_id, read
    /// off the SHIPPED DDL (SqlStateStore.SchemaScript) rather than hardcoded a
    /// second time — a duplicated constant drifts. Every subject_id NVARCHAR
    /// declaration in the script (the CREATE and the collation-migration ALTER)
    /// must agree, or this throws instead of picking one.</summary>
    internal static int MaxLength { get; } = ReadDeclaredWidth();

    private static int ReadDeclaredWidth()
    {
        // \s after subject_id keeps this from matching the subject_ids
        // (NVARCHAR(MAX)) dead-letter column; \d+ cannot match MAX anyway.
        var widths = Regex.Matches(SqlStateStore.SchemaScript,
                @"\bsubject_id\s+NVARCHAR\s*\(\s*(\d+)\s*\)", RegexOptions.IgnoreCase)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .ToList();
        return widths switch
        {
            [var width] => width,
            [] => throw new InvalidOperationException(
                "SubjectIdPolicy: no bounded subject_id NVARCHAR declaration found in " +
                "SqlStateStore.SchemaScript — the DDL changed shape; update the policy's parser."),
            _ => throw new InvalidOperationException(
                "SubjectIdPolicy: SqlStateStore.SchemaScript declares subject_id NVARCHAR at " +
                $"conflicting widths ({string.Join(", ", widths)}); the CREATE and its migration " +
                "ALTER must agree before the policy can enforce a bound."),
        };
    }

    /// <summary>Null when <paramref name="subjectId"/> is storable identically
    /// by both backends; otherwise a complete, actionable refusal message —
    /// what was wrong, the offending id rendered safely (no raw unpaired
    /// surrogates reach a console), and what the operator should do next.
    /// Never throws and never mutates anything; the caller decides that a
    /// non-null result refuses the command.</summary>
    internal static string? Explain(string subjectId)
    {
        if (!StateContract.IsWellFormedUtf16(subjectId))
        {
            var (index, unit) = FirstIllFormedUnit(subjectId);
            return
                $"the subject id is not well-formed UTF-16 — it contains an unpaired surrogate " +
                $"code unit (0x{unit:X4}) at index {index}. The file backend cannot store this id " +
                "faithfully (the JSON writer rewrites the code unit to U+FFFD), so the erasure would " +
                "be filed under a DIFFERENT id and the subject would stay ingestible, while the SQL " +
                "backend would store it verbatim — the two backends would then disagree about who is " +
                $"erased. Offending id (escaped, truncated): \"{Render(subjectId, 64)}\". " +
                "An unpaired surrogate almost always means the id was corrupted in transit — a " +
                "copy/paste or encoding conversion split a two-unit character in half. Re-copy the " +
                "exact id from the feed export or the upstream system and re-run forget-subject; if " +
                "the upstream id genuinely contains this sequence, repair it upstream first. " +
                "Nothing has been changed by this refusal.";
        }
        if (subjectId.Length > MaxLength)
        {
            return
                $"the subject id is {subjectId.Length} UTF-16 code units long; the SQL backend's " +
                $"subject_id column is NVARCHAR({MaxLength}) (read from the shipped DDL), so on SQL " +
                "this erasure would fail with error 8152 (not transient, not retried) while the file " +
                "backend would accept it — the two backends would then disagree about who is erased. " +
                $"Offending id (escaped, truncated): \"{Render(subjectId, 64)}\". " +
                $"Altrata person ids are far shorter than {MaxLength} units, so an over-long value is " +
                "almost always a paste error — two ids concatenated, or surrounding text included. " +
                "Check the id against the feed export and re-run forget-subject with the exact id. " +
                "Nothing has been changed by this refusal.";
        }
        return null;
    }

    private static (int Index, int Unit) FirstIllFormedUnit(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    return (i, c);
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return (i, c);
            }
        }
        throw new InvalidOperationException("FirstIllFormedUnit called on a well-formed string.");
    }

    /// <summary>Render an id for an error message without ever emitting a raw
    /// unpaired surrogate or control character to a console/log: those become
    /// \uXXXX escapes; everything else (including well-formed non-BMP pairs)
    /// passes through. <paramref name="maxUnits"/> caps the rendered length for
    /// pathological inputs.</summary>
    internal static string Render(string subjectId, int maxUnits = int.MaxValue)
    {
        var sb = new StringBuilder(Math.Min(subjectId.Length, maxUnits) + 16);
        var rendered = 0;
        for (var i = 0; i < subjectId.Length && rendered < maxUnits; i++, rendered++)
        {
            var c = subjectId[i];
            if (char.IsHighSurrogate(c) && i + 1 < subjectId.Length && char.IsLowSurrogate(subjectId[i + 1]))
            {
                sb.Append(c).Append(subjectId[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c) || char.IsControl(c))
            {
                sb.Append("\\u").Append(((int)c).ToString("X4"));
            }
            else
            {
                sb.Append(c);
            }
        }
        if (rendered < subjectId.Length)
            sb.Append("…(").Append(subjectId.Length - rendered).Append(" more units)");
        return sb.ToString();
    }
}
