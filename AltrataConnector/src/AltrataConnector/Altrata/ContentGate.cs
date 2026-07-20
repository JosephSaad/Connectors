// Altrata/ContentGate.cs
// ----------------------
// The ContentGate stage (chassis component CS-1): screen the FINAL indexed text
// of every external item before it becomes Copilot grounding context.
//
// ── SCOPE FOR THIS CONNECTOR ────────────────────────────────────────────────
// Altrata ingests NO binary content, so there is deliberately NO malware
// scanner here:
//   * FeedReader.ReadRecords accepts .json / .jsonl / .csv ONLY and throws
//     NotSupportedException on any other extension (Altrata/FeedReader.cs).
//   * ItemTransformer emits content type "text" unconditionally
//     (Altrata/ItemTransformer.cs, ExternalItemContent.Type).
//   * There is no attachment / blob / stream path anywhere in the connector,
//     and the enrichment API parses JSON only.
// FILE INTEGRITY is already covered by the existing SHA-256 manifest gate
// (FeedReader.ValidateChecksums + the TOCTOU re-verify on the same open handle).
// The binary fail-mode knob still exists for fleet parity and is documented as
// INERT here; its default is FAIL-CLOSED so that if a binary path is ever added
// it starts safe.
//
// ── POSTURE: QUARANTINE, NOT DROP ───────────────────────────────────────────
// A positive verdict routes the item to the EXISTING dead-letter queue with
// reason "content-gate:<category>", appends a decision-ledger entry of the new
// 'quarantine' kind, stamps a scan-status property, increments a metric and
// raises the existing alert path. The record stays REPLAYABLE, so retry-failed
// re-drives it once a human has reviewed it. retry-failed re-runs the gate, so
// draining the queue with CONTENT_GATE still on cannot silently bypass a
// quarantine — the operator clears the gate (or fixes the source) deliberately.
//
// ── FAIL MODE (deliberate asymmetry) ────────────────────────────────────────
//   text / injection -> FAIL OPEN  (default): a heuristic outage must not stop a
//        crawl. The item proceeds, loudly: warning + metric + scan status
//        "incomplete" stamped on the item so the gap is visible in the index.
//   binary / malware -> FAIL CLOSED (default): never index unscanned binary
//        content. Inert here (no binary path) but shipped safe by default.
// Both are configurable; the shipped defaults are the ones above.
//
// ── PII CONTRACT ────────────────────────────────────────────────────────────
// A verdict carries ONLY the item id and a fixed-vocabulary category. Never the
// matched text, never a snippet, never the field value. Names, employers and
// net-worth figures must not reach a log, a metric, a dead-letter reason or a
// ledger entry. Enforced by tests.

using System.Text;
using AltrataConnector.Config;
using AltrataConnector.Graph;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.Altrata;

/// <summary>What to do when a scan cannot be completed.</summary>
public enum ContentGateFailMode
{
    /// <summary>Proceed unscanned, loudly (warning + metric + scan status).</summary>
    Open = 0,
    /// <summary>Quarantine — never index unscanned content.</summary>
    Closed = 1,
}

/// <summary>
/// ContentGate configuration. Read LIVE from the environment (same idiom as
/// DeadLetterPolicy.Mode and Alerting.WebhookUrl) so a command, a crawl and a
/// retry-failed run all see the same switch without threading it through
/// AppConfig. AppConfig.Load ALSO parses it, so a typo fails validate-config /
/// startup instead of mid-crawl.
/// </summary>
public sealed record ContentGateOptions
{
    public const string EnabledEnvVar = "CONTENT_GATE";
    public const string IcapUrlEnvVar = "CONTENT_GATE_ICAP_URL";
    public const string FailModeEnvVar = "CONTENT_GATE_FAIL_MODE";
    public const string TextFailModeEnvVar = "CONTENT_GATE_TEXT_FAIL_MODE";
    public const string BinaryFailModeEnvVar = "CONTENT_GATE_BINARY_FAIL_MODE";
    public const string MaxScanMbEnvVar = "CONTENT_GATE_MAX_SCAN_MB";
    public const string PatternTimeoutMsEnvVar = "CONTENT_GATE_PATTERN_TIMEOUT_MS";
    public const string PatternsPathEnvVar = "CONTENT_GATE_PATTERNS_PATH";

    /// <summary>CONTENT_GATE — master switch. DEFAULT OFF: the bank's scanner
    /// contract is not yet agreed, and with this unset the connector's wire
    /// output is byte-identical to before the gate existed.</summary>
    public bool Enabled { get; init; }

    /// <summary>Fail mode for TEXT (injection heuristics). Ships OPEN.</summary>
    public ContentGateFailMode TextFailMode { get; init; } = ContentGateFailMode.Open;

    /// <summary>Fail mode for BINARY (malware). Ships CLOSED. Inert in this
    /// connector — see the file header — but kept honest for fleet parity.</summary>
    public ContentGateFailMode BinaryFailMode { get; init; } = ContentGateFailMode.Closed;

    /// <summary>CONTENT_GATE_MAX_SCAN_MB — bound on how much of an item's text
    /// is scanned. Beyond it the scan is reported INCOMPLETE (never clean).</summary>
    public int MaxScanMb { get; init; } = 4;

    /// <summary>Per-pattern regex match budget. A timeout fails SAFE.</summary>
    public TimeSpan PatternTimeout { get; init; } = InjectionScanner.DefaultMatchTimeout;

    /// <summary>CONTENT_GATE_PATTERNS_PATH — replacement pattern table.</summary>
    public string? PatternsPath { get; init; }

    /// <summary>CONTENT_GATE_ICAP_URL — the fleet's ICAP/HTTP malware gateway.
    /// Read and validated for parity; INERT here (no binary is ever ingested).</summary>
    public string? IcapUrl { get; init; }

    public static ContentGateOptions FromEnv()
    {
        var text = ParseFailMode(TextFailModeEnvVar)
                   ?? ParseFailMode(FailModeEnvVar)
                   ?? ContentGateFailMode.Open;
        var binary = ParseFailMode(BinaryFailModeEnvVar)
                     ?? ParseFailMode(FailModeEnvVar)
                     ?? ContentGateFailMode.Closed;

        return new ContentGateOptions
        {
            Enabled = EnvFlags.IsTrue(EnabledEnvVar),
            TextFailMode = text,
            BinaryFailMode = binary,
            MaxScanMb = ParsePositiveInt(MaxScanMbEnvVar, 4),
            PatternTimeout = TimeSpan.FromMilliseconds(
                ParsePositiveInt(PatternTimeoutMsEnvVar,
                    (int)InjectionScanner.DefaultMatchTimeout.TotalMilliseconds)),
            PatternsPath = Optional(PatternsPathEnvVar),
            IcapUrl = Optional(IcapUrlEnvVar),
        };
    }

    private static string? Optional(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static ContentGateFailMode? ParseFailMode(string name)
    {
        var raw = Optional(name);
        if (raw == null)
            return null;
        return raw.ToLowerInvariant() switch
        {
            "open" => ContentGateFailMode.Open,
            "closed" => ContentGateFailMode.Closed,
            _ => throw new ConfigurationError(
                $"{name} must be 'open' or 'closed', got '{raw}'"),
        };
    }

    private static int ParsePositiveInt(string name, int fallback)
    {
        var raw = Optional(name);
        if (raw == null)
            return fallback;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new ConfigurationError($"{name} must be a positive integer, got '{raw}'");
        }
        return value;
    }
}

/// <summary>The stage's decision about one item. ITEM ID + CATEGORY ONLY.</summary>
public sealed record ContentScanVerdict
{
    public required string ItemId { get; init; }
    public required ContentScanOutcome Outcome { get; init; }

    /// <summary>Fixed-vocabulary category (ContentGateCategories); null if clean.</summary>
    public string? Category { get; init; }

    /// <summary>Fixed-vocabulary detail (ContentGateDetails); null unless incomplete.</summary>
    public string? Detail { get; init; }

    /// <summary>The policy outcome after the fail mode was applied.</summary>
    public bool Quarantine { get; init; }

    /// <summary>Value stamped into the scan-status property.</summary>
    public required string Status { get; init; }

    /// <summary>The PII-safe dead-letter / ledger reason.</summary>
    public string Reason => $"content-gate:{Category}";
}

/// <summary>An inspected item plus its verdict. The item is a COPY carrying the
/// scan-status property; the original is returned untouched when the gate is
/// disabled, so a disabled gate is provably a no-op.</summary>
public sealed record ContentGateResult(ExternalItem Item, ContentScanVerdict Verdict);

/// <summary>Raised when retry-failed tries to replay an item the gate still
/// blocks. Its message IS the PII-safe reason, so the dead-letter record keeps
/// its original content-gate reason across the replay attempt.</summary>
public sealed class ContentGateBlockedException : Exception
{
    public ContentGateBlockedException(string reason) : base(reason) { }
}

public sealed class ContentGate
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.content_gate");

    /// <summary>Property stamped on an inspected item. Added ONLY when the gate
    /// is enabled — with CONTENT_GATE unset the wire output is unchanged.</summary>
    public const string ScanStatusProp = "contentScanStatus";

    public const string StatusClean = "clean";
    public const string StatusQuarantined = "quarantined";
    public const string StatusIncomplete = "incomplete";

    /// <summary>Fleet-canonical name: content_gate_blocked_total. This connector
    /// prefixes every metric with 'altrata_' (see Infrastructure/Metrics.cs and
    /// ops/grafana-dashboard.json), so the local name carries that prefix.</summary>
    public const string BlockedMetric = "altrata_content_gate_blocked_total";
    public const string ScannedMetric = "altrata_content_gate_scanned_total";
    public const string IncompleteMetric = "altrata_content_gate_incomplete_total";

    private readonly Lazy<IContentScanner> _scanner;

    public ContentGateOptions Options { get; }

    public ContentGate(ContentGateOptions options, IContentScanner? scanner = null)
    {
        Options = options;
        // Lazy: a disabled gate never constructs (or compiles) a scanner.
        _scanner = new Lazy<IContentScanner>(() => scanner ?? BuildScanner(options));
    }

    private static IContentScanner BuildScanner(ContentGateOptions options)
    {
        if (options.PatternsPath != null)
            return InjectionScanner.FromFile(options.PatternsPath, options.PatternTimeout);
        return options.PatternTimeout == InjectionScanner.DefaultMatchTimeout
            ? InjectionScanner.Default                      // shared, compiled once
            : new InjectionScanner(InjectionScanner.DefaultPatterns, options.PatternTimeout);
    }

    /// <summary>The gate for the current environment, or NULL when CONTENT_GATE
    /// is off — callers branch on null so a disabled gate costs nothing.</summary>
    public static ContentGate? FromEnv()
    {
        var options = ContentGateOptions.FromEnv();
        return options.Enabled ? new ContentGate(options) : null;
    }

    /// <summary>
    /// Scan the item's final indexed text and apply the fail-mode policy.
    /// Returns the (possibly status-stamped) item plus a PII-safe verdict.
    /// </summary>
    public ContentGateResult Inspect(ExternalItem item)
    {
        if (!Options.Enabled)
        {
            return new ContentGateResult(item, new ContentScanVerdict
            {
                ItemId = item.Id,
                Outcome = ContentScanOutcome.Clean,
                Status = StatusClean,
                Quarantine = false,
            });
        }

        Metrics.Increment(ScannedMetric);
        var (text, truncated) = AssembleScannableText(item);

        ContentScanResult result;
        try
        {
            result = _scanner.Value.Scan(text);
        }
        catch (Exception exc) when (exc is not ConfigurationError)
        {
            // Scanner unavailable (gateway down, transient IO, ...). A
            // ConfigurationError is NOT swallowed — a bad pattern table is an
            // operator error and fails fast like every other setting here.
            // Log the exception TYPE only: a message could in principle echo
            // content, and content is PII in this connector.
            Logger.Warning($"CONTENT_GATE: content scanner UNAVAILABLE ({exc.GetType().Name}) " +
                           $"while scanning item '{item.Id}'.");
            result = ContentScanResult.Incomplete(ContentGateDetails.ScannerUnavailable);
        }

        // Truncation is never a clean bill of health: what we did not read, we
        // did not clear. A hit inside the scanned prefix still wins.
        if (result.Outcome == ContentScanOutcome.Clean && truncated)
            result = ContentScanResult.Incomplete(ContentGateDetails.ScanTruncated);

        var verdict = Decide(item.Id, result);
        return new ContentGateResult(Stamp(item, verdict.Status), verdict);
    }

    private ContentScanVerdict Decide(string itemId, ContentScanResult result)
    {
        switch (result.Outcome)
        {
            case ContentScanOutcome.Blocked:
                Metrics.Increment(BlockedMetric);
                Logger.Warning($"CONTENT_GATE: item '{itemId}' QUARANTINED " +
                               $"(content-gate:{result.Category}) — routed to the dead-letter queue " +
                               "for review; re-drivable with retry-failed.");
                return new ContentScanVerdict
                {
                    ItemId = itemId,
                    Outcome = ContentScanOutcome.Blocked,
                    Category = result.Category,
                    Quarantine = true,
                    Status = StatusQuarantined,
                };

            case ContentScanOutcome.Incomplete when Options.TextFailMode == ContentGateFailMode.Closed:
                Metrics.Increment(IncompleteMetric);
                Metrics.Increment(BlockedMetric);
                Logger.Warning($"CONTENT_GATE: item '{itemId}' scan INCOMPLETE " +
                               $"(detail: {result.Detail}) and {ContentGateOptions.FailModeEnvVar}=closed " +
                               "— QUARANTINED (fail-closed).");
                return new ContentScanVerdict
                {
                    ItemId = itemId,
                    Outcome = ContentScanOutcome.Incomplete,
                    Category = ContentGateCategories.ScanIncomplete,
                    Detail = result.Detail,
                    Quarantine = true,
                    Status = StatusQuarantined,
                };

            case ContentScanOutcome.Incomplete:
                Metrics.Increment(IncompleteMetric);
                Logger.Warning($"CONTENT_GATE: item '{itemId}' scan INCOMPLETE " +
                               $"(detail: {result.Detail}) — {ContentGateOptions.FailModeEnvVar}=open, so the " +
                               "item proceeds UNSCANNED (fail-open). Injection screening is a heuristic, not a " +
                               "security boundary; set " + ContentGateOptions.FailModeEnvVar +
                               "=closed to quarantine instead.");
                return new ContentScanVerdict
                {
                    ItemId = itemId,
                    Outcome = ContentScanOutcome.Incomplete,
                    Category = ContentGateCategories.ScanIncomplete,
                    Detail = result.Detail,
                    Quarantine = false,
                    Status = StatusIncomplete,
                };

            default:
                return new ContentScanVerdict
                {
                    ItemId = itemId,
                    Outcome = ContentScanOutcome.Clean,
                    Quarantine = false,
                    Status = StatusClean,
                };
        }
    }

    private static ExternalItem Stamp(ExternalItem item, string status)
    {
        var properties = new Dictionary<string, object?>(item.Properties, StringComparer.Ordinal)
        {
            [ScanStatusProp] = status,
        };
        return item with { Properties = properties };
    }

    /// <summary>
    /// The FINAL indexed text: the assembled body plus every string (and string
    /// collection) property — an injection hidden in a role title grounds a
    /// Copilot answer exactly like one in the body. Bounded by
    /// CONTENT_GATE_MAX_SCAN_MB; the bool says whether we had to stop early.
    /// </summary>
    private (string Text, bool Truncated) AssembleScannableText(ExternalItem item)
    {
        var budget = (long)Options.MaxScanMb * 1024 * 1024;
        var sb = new StringBuilder();
        var truncated = false;

        void Append(string? value)
        {
            if (truncated || string.IsNullOrEmpty(value))
                return;
            var remaining = budget - sb.Length;
            if (remaining <= 0)
            {
                truncated = true;
                return;
            }
            if (value.Length > remaining)
            {
                sb.Append(value, 0, (int)remaining);
                truncated = true;
                return;
            }
            sb.Append(value).Append('\n');
        }

        Append(item.Content?.Value);
        foreach (var (key, value) in item.Properties)
        {
            if (key == ScanStatusProp)
                continue;
            switch (value)
            {
                case string s:
                    Append(s);
                    break;
                case IEnumerable<string> many:
                    foreach (var one in many)
                        Append(one);
                    break;
            }
        }
        return (sb.ToString(), truncated);
    }
}
