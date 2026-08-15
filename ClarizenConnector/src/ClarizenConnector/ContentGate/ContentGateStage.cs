// ContentGate/ContentGateStage.cs
// -------------------------------
// The ContentGate stage (chassis component CS-1) — one place where every piece
// of content that is about to become Copilot grounding context is inspected.
//
// WHY THIS EXISTS
// Ingested content IS the grounding context. A malicious document is therefore
// not an attack on the connector, it is an attack on every user whose query
// that document grounds. Nothing upstream of this stage validates content.
//
// TWO INDEPENDENT SCANNERS BEHIND ONE STAGE
//   InjectionScanner  — heuristics over the final indexed TEXT (all connectors).
//   IMalwareScanner   — binary scanning, wired only where this connector
//                       actually ingests bytes (the attachment download path).
//
// THE FAIL-MODE ASYMMETRY (deliberate, and the most important thing here)
//   binary/malware -> FAIL CLOSED. Never index binary content that has not been
//                     proven clean. An AV outage is not a licence to index
//                     unscanned bytes.
//   text/injection -> FAIL OPEN.  The injection scanner is a heuristic, not a
//                     security boundary. Blocking an entire crawl because a
//                     heuristic is unavailable does more damage than the risk
//                     it mitigates — but it is counted and logged loudly, never
//                     silent. Alert on content_gate_scan_unavailable_total.
//   Both are configurable (CONTENT_GATE_FAIL_MODE[_BINARY|_TEXT]); those are the
//   shipped defaults.
//
// A regex TIMEOUT is not an outage — it is an incomplete scan of one specific
// document, so it fails SAFE (blocked) regardless of the text fail mode.
// Otherwise a pathological input would be a one-line gate bypass.
//
// POSTURE: this stage only ever produces a VERDICT. Quarantining (dead-letter,
// decision ledger, alert) is the pipeline's job — see Graph/Ingest.cs.

using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.ContentGate;

public sealed class ContentGateStage
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.contentgate");

    /// <summary>Graph property recording the gate's verdict for an item.</summary>
    public const string StatusProperty = "ContentGateStatus";

    /// <summary>Prefix of a blocked status value: <c>blocked:&lt;category&gt;</c>.</summary>
    public const string BlockedPrefix = "blocked:";

    /// <summary>Prefix of an incompletely-scanned status value:
    /// <c>incomplete:&lt;category&gt;</c>. The item WAS indexed (text fail-open),
    /// but the gate did not inspect all of it, so it is deliberately
    /// distinguishable from <see cref="CleanStatus"/>.</summary>
    public const string IncompletePrefix = "incomplete:";

    /// <summary>Status value stamped on an item that was scanned in full and
    /// passed. Only ever stamped when the gate actually read all the text.</summary>
    public const string CleanStatus = "clean";

    /// <summary>Dead-letter / ledger reason prefix: <c>content-gate:&lt;category&gt;</c>.</summary>
    public const string ReasonPrefix = "content-gate:";

    private readonly AppConfig _config;
    private readonly InjectionScanner? _injection;
    private readonly IMalwareScanner? _malware;

    public ContentGateStage(
        AppConfig config, InjectionScanner? injection = null, IMalwareScanner? malware = null)
    {
        _config = config;
        _injection = injection;
        _malware = malware;
    }

    /// <summary>CONTENT_GATE. When false this stage is a strict no-op: no scan,
    /// no property, no metric, no cost.</summary>
    public bool Enabled => _config.ContentGateEnabled;

    /// <summary>True when unscannable BINARY content must not be indexed (default).</summary>
    public bool BinaryFailClosed => _config.ContentGateBinaryFailClosed;

    /// <summary>True when unscannable TEXT must not be indexed (NOT the default).</summary>
    public bool TextFailClosed => _config.ContentGateTextFailClosed;

    /// <summary>True when a binary scanner is actually wired.</summary>
    public bool HasMalwareScanner => _malware is not null;

    /// <summary>
    /// Max CHARACTERS handed to the text scanner, derived from the BYTE cap.
    /// <para>
    /// CONTENT_GATE_MAX_SCAN_MB is a byte budget, but the text scanner works on
    /// a .NET string and is capped in chars. Comparing the byte budget directly
    /// against <c>text.Length</c> conflated the two: a UTF-8 char costs 1–4
    /// bytes (and a .NET char is a UTF-16 code unit), so the effective memory
    /// cost of a scan could reach ~3x the configured MiB for multibyte text.
    /// Dividing by <see cref="Utf8WorstCaseBytesPerChar"/> makes the cap mean
    /// what it says for every input, at the price of scanning fewer chars of
    /// pure-ASCII text than before.
    /// </para>
    /// <para>
    /// Direction of the old bug matters and is worth stating: it only ever made
    /// the scan cover MORE text than configured. It could not mis-label a
    /// verdict — text past the cap is reported
    /// <c>incomplete:injection-scan-truncated</c>, never clean — so this is a
    /// resource-accounting fix, not a verdict-correctness fix.
    /// </para>
    /// </summary>
    private int MaxScanChars =>
        (int)Math.Max(1, Math.Min(
            _config.ContentGateMaxScanBytes / Utf8WorstCaseBytesPerChar, int.MaxValue));

    /// <summary>Bytes a single UTF-16 char can cost when encoded as UTF-8. A
    /// non-BMP character is a surrogate PAIR (2 chars) costing 4 bytes, so 3 is
    /// the true worst case PER CHAR, not 4.</summary>
    internal const int Utf8WorstCaseBytesPerChar = 3;

    /// <summary>The derived char cap, exposed so a test can assert the BYTE
    /// budget is honoured against the real UTF-8 encoder.</summary>
    internal int MaxScanCharsForTests => MaxScanChars;

    /// <summary>The dead-letter / ledger reason for a category.</summary>
    public static string ReasonFor(string category) => ReasonPrefix + category;

    /// <summary>
    /// Build the stage from configuration alone (the self-construct half of the
    /// pipeline's seam convention). Warns loudly about the configuration that
    /// silently blocks every attachment: gate on, binaries ingested, no scanner.
    /// </summary>
    public static ContentGateStage Create(AppConfig config)
    {
        var injection = InjectionScanner.Load();

        IMalwareScanner? malware = null;
        if (!string.IsNullOrWhiteSpace(config.ContentGateIcapUrl))
        {
            malware = new IcapMalwareScanner(config.ContentGateIcapUrl!);
        }
        else if (config.AttachmentIngestion && config.ContentGateBinaryFailClosed)
        {
            Logger.Error(
                "ContentGate: ATTACHMENT_INGESTION is on and CONTENT_GATE_FAIL_MODE_BINARY is "
                + "'closed', but CONTENT_GATE_ICAP_URL is not set — no binary scanner is wired, so "
                + "EVERY attachment will be quarantined. Set CONTENT_GATE_ICAP_URL, or accept the "
                + "risk explicitly with CONTENT_GATE_FAIL_MODE_BINARY=open.");
        }

        Logger.Info(
            $"ContentGate enabled: {injection.PatternCount} injection pattern(s), "
            + $"binary scanner {(malware is null ? "NOT configured" : "configured")}, "
            + $"fail modes binary={config.ContentGateBinaryFailMode}/text={config.ContentGateTextFailMode}, "
            + $"scan cap {config.ContentGateMaxScanMb} MiB.");
        return new ContentGateStage(config, injection, malware);
    }

    // ── Binary path (fails CLOSED by default) ───────────────────────────────

    /// <summary>
    /// Scan attachment bytes. Called between the download and the text
    /// extractor, so nothing derived from unscanned bytes can reach the index.
    /// </summary>
    public async Task<GateVerdict> ScanBinaryAsync(
        byte[] content, string fileName, CancellationToken ct = default)
    {
        if (!Enabled)
            return GateVerdict.Pass;

        if (content.LongLength > _config.ContentGateMaxScanBytes)
        {
            // Too big to prove clean. That is an unscannable binary, not a pass.
            return UnscannableBinary(
                fileName,
                $"{content.LongLength} bytes exceeds the "
                + $"{_config.ContentGateMaxScanMb} MiB CONTENT_GATE_MAX_SCAN_MB cap");
        }

        if (_malware is null)
            return UnscannableBinary(fileName, "no binary scanner configured (CONTENT_GATE_ICAP_URL)");

        var result = await _malware.ScanAsync(content, fileName, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case MalwareStatus.Infected:
                Logger.Error(
                    $"ContentGate: MALWARE in attachment '{fileName}' — {result.Detail}. "
                    + "The attachment content was not indexed and the item is quarantined.");
                return GateVerdict.Block(GateCategories.Malware, result.Detail);

            case MalwareStatus.Unavailable:
                return UnscannableBinary(fileName, result.Detail);

            default:
                return GateVerdict.Pass;
        }
    }

    /// <summary>Apply the BINARY fail mode (closed by default) to content that
    /// could not be scanned. Counted either way — an operator running fail-open
    /// must still be able to see how much went through unscanned.
    /// <para>
    /// Fail-open returns <see cref="GateVerdict.Incomplete"/>, NOT
    /// <see cref="GateVerdict.Pass"/> — exactly as the text path does. Both index
    /// the content; only Pass asserts "the gate scanned this and it was clean",
    /// and that assertion is precisely what must never be made about bytes no
    /// scanner ever saw. Returning Pass here left an item that carried unscanned
    /// binary content stamped <c>clean</c>.
    /// </para>
    /// </summary>
    private GateVerdict UnscannableBinary(string fileName, string detail)
    {
        Metrics.IncContentGateScanUnavailable("binary");

        if (BinaryFailClosed)
        {
            Logger.Error(
                $"ContentGate: could not scan attachment '{fileName}' ({detail}). "
                + "CONTENT_GATE_FAIL_MODE_BINARY=closed — the content will NOT be indexed and the "
                + "item is quarantined (re-drivable with `retry-failed` once the scanner is back).");
            return GateVerdict.Block(GateCategories.MalwareUnscannable, detail);
        }

        Logger.Warning(
            $"ContentGate: could not scan attachment '{fileName}' ({detail}), but "
            + "CONTENT_GATE_FAIL_MODE_BINARY=open — INDEXING UNSCANNED BINARY CONTENT. "
            + "This is an explicitly accepted risk; the item is stamped "
            + $"'{IncompletePrefix}{GateCategories.MalwareUnscannable}', never 'clean'. Alert on "
            + "clarizen_connector_content_gate_scan_unavailable_total{kind=\"binary\"}.");
        return GateVerdict.Incomplete(GateCategories.MalwareUnscannable, detail);
    }

    // ── Text path (fails OPEN by default) ───────────────────────────────────

    /// <summary>Scan a raw string (extracted attachment text, or any candidate
    /// grounding text) for injection heuristics.</summary>
    public GateVerdict ScanText(string? text)
    {
        if (!Enabled)
            return GateVerdict.Pass;

        // A scanner with no usable patterns is BLIND. That is an outage, and
        // the text fail mode — not a silent "clean" — decides what happens.
        if (_injection is null || _injection.PatternCount == 0)
            return UnscannableText("the injection pattern set is empty or failed to load");

        var verdict = _injection.Scan(text, MaxScanChars);

        if (verdict.Suspicious)
        {
            if (verdict.Incomplete)
            {
                // A timeout is an incomplete scan of THIS document, not a scanner
                // outage — so fail-open does not apply and it blocks. Treating it
                // as "no match" would be a trivial bypass.
                Metrics.IncContentGateScanUnavailable("text");
                Logger.Warning(
                    $"ContentGate: injection scan did not complete (pattern '{verdict.PatternName}' "
                    + "timed out). Failing SAFE — the content is treated as suspicious.");
                return GateVerdict.Block(
                    GateCategories.InjectionScanTimeout, $"pattern '{verdict.PatternName}' timed out");
            }

            Logger.Warning(
                $"ContentGate: prompt-injection heuristic '{verdict.PatternName}' "
                + $"({verdict.Category}) matched indexed text.");
            return GateVerdict.Block(verdict.Category, $"pattern '{verdict.PatternName}'");
        }

        if (verdict.Incomplete)
        {
            // Truncation: the scanner read only the first MaxScanChars and found
            // nothing THERE. The tail was never inspected, so this is not a clean
            // bill of health — it is unscanned text, and the documented text fail
            // mode governs it exactly as it governs a blind scanner.
            return UnscannableText(
                $"{text!.Length} chars exceeds the {MaxScanChars}-char "
                + $"({_config.ContentGateMaxScanMb} MiB) CONTENT_GATE_MAX_SCAN_MB scan cap — only "
                + $"the first {MaxScanChars} chars were scanned",
                GateCategories.InjectionScanTruncated);
        }

        return GateVerdict.Pass;
    }

    /// <summary>
    /// Apply the TEXT fail mode (open by default) to text the gate could not
    /// fully inspect — either because the heuristic itself is unusable, or
    /// because the content ran past the scan cap.
    /// <para>
    /// Fail-open returns <see cref="GateVerdict.Incomplete"/>, NOT
    /// <see cref="GateVerdict.Pass"/>. Both index the content, but only Pass
    /// means "we looked at all of it and it was fine" — and that claim is
    /// precisely what the caller must not stamp on unscanned content.
    /// </para>
    /// </summary>
    private GateVerdict UnscannableText(
        string detail, string category = GateCategories.InjectionUnscannable)
    {
        Metrics.IncContentGateScanUnavailable("text");

        if (TextFailClosed)
        {
            Logger.Error(
                $"ContentGate: cannot fully scan text ({detail}) and "
                + "CONTENT_GATE_FAIL_MODE_TEXT=closed — the item is quarantined "
                + "(re-drivable with `retry-failed`).");
            return GateVerdict.Block(category, detail);
        }

        Logger.Warning(
            $"ContentGate: cannot fully scan text ({detail}). CONTENT_GATE_FAIL_MODE_TEXT=open — "
            + "INDEXING TEXT THAT WAS NOT FULLY SCANNED. This is the shipped default because the "
            + "scanner is a heuristic, not a security boundary, but it means the gate is not "
            + $"protecting this content: it is stamped '{IncompletePrefix}{category}', never "
            + "'clean'. Alert on "
            + "clarizen_connector_content_gate_scan_unavailable_total{kind=\"text\"}.");
        return GateVerdict.Incomplete(category, detail);
    }

    /// <summary>
    /// Scan a fully assembled item — content body plus EVERY string / string[]
    /// property, with no exceptions.
    /// <para>
    /// It shares <see cref="Item.SensitivityClassifier.ScanText"/> so the two
    /// scanners cannot drift, but passes
    /// <c>includeTaxonomyProperties: true</c>. The classifier skips its own two
    /// output properties to avoid re-detecting its own labels; the gate must not,
    /// because a schema mapping like <c>"Description": "advisorySensitivity"</c>
    /// puts source-controlled text under that reserved name and it is indexed as
    /// grounding context all the same. Skipping it hid that text from the gate
    /// entirely — a silent bypass. Such a mapping is now ALSO rejected at config
    /// load (see <see cref="Config.SchemaConfig"/>); this is the second line of
    /// defence, so an item that reaches the gate by any route is fully scanned.
    /// </para>
    /// </summary>
    public GateVerdict ScanItem(ExternalItem item) =>
        !Enabled
            ? GateVerdict.Pass
            : ScanText(Item.SensitivityClassifier.ScanText(item, includeTaxonomyProperties: true));

    /// <summary>Stamp the gate's verdict on the item as a Graph property.
    /// "clean" is reserved for content the gate actually read in full — an
    /// incomplete scan is stamped <c>incomplete:&lt;category&gt;</c> so the
    /// property never overstates what was checked.
    /// <para>
    /// <paramref name="enabled"/> is REQUIRED and has no default on purpose. A
    /// disabled gate scans nothing and its <see cref="ScanBinaryAsync"/> /
    /// <see cref="ScanText"/> / <see cref="ScanItem"/> all return
    /// <see cref="GateVerdict.Pass"/> as a no-op — so stamping that Pass would
    /// write <c>clean</c> onto an item NOTHING inspected, which is the worst
    /// possible value for this property: false assurance that reads as a scan
    /// result. Production never reaches this state today (every caller checks
    /// the gate first), but a future caller widening the seam must fail loudly
    /// rather than silently manufacture a clean bill of health.
    /// </para></summary>
    /// <exception cref="InvalidOperationException">
    /// The gate is disabled. There is no verdict to record.
    /// </exception>
    public static void Stamp(ExternalItem item, GateVerdict verdict, bool enabled)
    {
        if (!enabled)
        {
            throw new InvalidOperationException(
                $"ContentGateStage.Stamp called with the gate DISABLED (verdict {verdict.Outcome}, "
                + $"category '{verdict.Category}'). A disabled gate inspects nothing, so it has no "
                + $"verdict to record — stamping would put '{StatusProperty}' on an item that was "
                + "never scanned. Guard the call with ContentGateStage.Enabled.");
        }

        item.Properties[StatusProperty] = verdict.Outcome switch
        {
            GateOutcome.Blocked => BlockedPrefix + verdict.Category,
            GateOutcome.Incomplete => IncompletePrefix + verdict.Category,
            _ => CleanStatus,
        };
    }

    /// <summary>Instance-scoped stamp: takes the enabled state from THIS stage,
    /// so a caller holding a gate cannot get the flag wrong.</summary>
    public void StampVerdict(ExternalItem item, GateVerdict verdict) =>
        Stamp(item, verdict, Enabled);

    /// <summary>Separator between the categories of a MERGED incomplete status,
    /// e.g. <c>incomplete:malware-unscannable+injection-unscannable</c>.</summary>
    public const char CategorySeparator = '+';

    /// <summary>
    /// Union two incomplete categories, preserving both reasons.
    /// <para>
    /// An item can be incompletely scanned for more than one reason at once —
    /// unscannable attachment BYTES at the enricher seam, then a truncated or
    /// unscannable item-level TEXT scan later. Re-stamping used to overwrite the
    /// first category with the second. The value stayed <c>incomplete:</c>, so
    /// there was never false assurance, but the operator lost WHICH reason
    /// applied and therefore what to do about it. Both are kept, ordered by
    /// first appearance and de-duplicated so the value is stable.
    /// </para>
    /// </summary>
    public static string MergeCategories(string? carried, string? fresh)
    {
        var merged = new List<string>();
        foreach (var source in new[] { carried, fresh })
        {
            if (string.IsNullOrEmpty(source))
                continue;
            foreach (var part in source.Split(CategorySeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!merged.Contains(part, StringComparer.Ordinal))
                    merged.Add(part);
            }
        }
        return string.Join(CategorySeparator, merged);
    }

    /// <summary>Read a blocked verdict previously stamped by an earlier seam
    /// (the attachment enricher scans bytes long before the pipeline sees the
    /// assembled item). Returns the category, or null when not blocked.</summary>
    public static string? BlockedCategoryOf(ExternalItem item) =>
        item.Properties.TryGetValue(StatusProperty, out var value)
        && value is string status
        && status.StartsWith(BlockedPrefix, StringComparison.Ordinal)
            ? status[BlockedPrefix.Length..]
            : null;

    /// <summary>Read an INCOMPLETE verdict previously stamped by an earlier seam
    /// (an unscannable attachment binary under a fail-open binary mode). Returns
    /// the category, or null when the item carries no incomplete stamp.
    /// <para>
    /// The item-level scan that runs later in the pipeline only sees the assembled
    /// TEXT, so it cannot know the bytes went unscanned. Without this, a clean
    /// text verdict would overwrite <c>incomplete:malware-unscannable</c> with
    /// <c>clean</c> and re-open the very gap the incomplete outcome exists to
    /// close.
    /// </para></summary>
    public static string? IncompleteCategoryOf(ExternalItem item) =>
        item.Properties.TryGetValue(StatusProperty, out var value)
        && value is string status
        && status.StartsWith(IncompletePrefix, StringComparison.Ordinal)
            ? status[IncompletePrefix.Length..]
            : null;
}
