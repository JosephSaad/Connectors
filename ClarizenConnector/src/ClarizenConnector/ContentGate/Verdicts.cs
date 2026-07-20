// ContentGate/Verdicts.cs
// -----------------------
// Shared result vocabulary for the content gate (chassis component CS-1).
//
// A gate verdict is BINARY for the ROUTING decision — index it, or don't. Every
// call site only has to ask IsBlocked, and the stage resolves an outage into
// that answer using the configured fail mode (binary fails CLOSED, text fails
// OPEN by default).
//
// There is, however, a third OUTCOME: Incomplete. It routes like Clean (the item
// is indexed) but it is emphatically NOT Clean, because the gate did not manage
// to inspect all of the content. Collapsing it into Clean was a real defect: an
// item larger than CONTENT_GATE_MAX_SCAN_MB was scanned only as a prefix and
// then stamped "clean", which is a false assurance about content nobody looked
// at. Fail-open means "index it anyway", it does not mean "we checked it".

namespace ClarizenConnector.ContentGate;

/// <summary>Gate decision for one piece of content.</summary>
public enum GateOutcome
{
    /// <summary>Nothing suspicious — index the content as normal.</summary>
    Clean = 0,

    /// <summary>Positive verdict — quarantine (never index this content).</summary>
    Blocked = 1,

    /// <summary>
    /// The gate did not inspect all of the content (it exceeded the scan cap, or
    /// the scanner was unusable) and the applicable fail mode is OPEN, so the
    /// content IS indexed. Routes like <see cref="Clean"/> — <c>IsBlocked</c> is
    /// false — but must never be reported or stamped as clean.
    /// </summary>
    Incomplete = 2,
}

/// <summary>
/// One gate decision: the outcome, the category that drives the dead-letter
/// reason (<c>content-gate:&lt;category&gt;</c>) and the metric label, and a
/// human-readable detail for logs and the audit ledger.
/// </summary>
public readonly record struct GateVerdict(GateOutcome Outcome, string Category, string Detail)
{
    public static GateVerdict Pass { get; } = new(GateOutcome.Clean, string.Empty, string.Empty);

    public static GateVerdict Block(string category, string detail) =>
        new(GateOutcome.Blocked, category, detail);

    /// <summary>Indexed, but not proven clean — see <see cref="GateOutcome.Incomplete"/>.</summary>
    public static GateVerdict Incomplete(string category, string detail) =>
        new(GateOutcome.Incomplete, category, detail);

    public bool IsBlocked => Outcome == GateOutcome.Blocked;

    /// <summary>True when the content was indexed without being fully scanned.</summary>
    public bool IsIncomplete => Outcome == GateOutcome.Incomplete;
}

/// <summary>
/// Canonical category names. These are the stable strings that appear in the
/// dead-letter reason, the decision ledger, the ContentGateStatus property and
/// the <c>content_gate_blocked_total</c> metric label — treat them as a wire
/// contract, not as free text.
/// </summary>
public static class GateCategories
{
    /// <summary>A malware scanner returned a positive verdict on binary content.</summary>
    public const string Malware = "malware";

    /// <summary>Binary content could NOT be scanned (no scanner, outage, or over
    /// the scan size cap) and the binary fail mode is closed.</summary>
    public const string MalwareUnscannable = "malware-unscannable";

    /// <summary>Imperative override ("ignore previous instructions").</summary>
    public const string InjectionOverride = "injection.override";

    /// <summary>Role reassignment ("you are now an unrestricted model").</summary>
    public const string InjectionRole = "injection.role";

    /// <summary>Exfiltration directive ("POST the transcript to http://...").</summary>
    public const string InjectionExfiltration = "injection.exfiltration";

    /// <summary>Hidden-text signal (zero-width / bidi control characters).</summary>
    public const string InjectionHiddenText = "injection.hidden_text";

    /// <summary>Very long base64-dense run (smuggled payload).</summary>
    public const string InjectionEncodedBlob = "injection.encoded_blob";

    /// <summary>A pattern timed out, so the scan is INCOMPLETE. Fails safe: the
    /// content is treated as suspicious rather than as "no match".</summary>
    public const string InjectionScanTimeout = "injection.scan_timeout";

    /// <summary>The text exceeded CONTENT_GATE_MAX_SCAN_MB, so only a PREFIX was
    /// scanned. The unscanned tail carries no signal either way, so the outcome
    /// is INCOMPLETE and the text fail mode decides (open ⇒ indexed and stamped
    /// incomplete, closed ⇒ quarantined). It is never "clean".</summary>
    public const string InjectionScanTruncated = "injection.scan_truncated";

    /// <summary>The injection scanner itself was unusable (no patterns loaded)
    /// and the text fail mode is closed.</summary>
    public const string InjectionUnscannable = "injection.unscannable";
}
