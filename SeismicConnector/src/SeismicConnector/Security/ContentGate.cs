// Security/ContentGate.cs
// -----------------------
// CS-1 ContentGate: the single stage that decides whether ingested content is
// safe to become Copilot grounding context. It fronts TWO independent scanners:
//
//   BINARY channel  -> IMalwareScanner over the downloaded payload
//   TEXT channel    -> InjectionScanner over the FINAL indexed text
//
// POSTURE: QUARANTINE, NOT DROP. A positive verdict never silently discards an
// item. The pipeline still indexes the item's METADATA (the malicious bytes /
// text are removed), writes the item to the EXISTING dead-letter queue with
// reason "content-gate:<category>", appends a "quarantine" decision-ledger
// entry, stamps a scan-status property, increments content_gate_blocked_total
// and raises the existing alert path. retry-failed re-drives it unchanged.
//
// FAIL MODE — the deliberate asymmetry:
//
//   BINARY  -> FAIL CLOSED by default. Malware detection is a real security
//              control and the payload is genuinely dangerous; indexing bytes
//              that were never scanned defeats the control entirely. If the
//              gateway is down, the content does not get indexed.
//
//   TEXT    -> FAIL OPEN by default. Injection detection is a HEURISTIC, not a
//              security boundary — it is regexes over prose. Halting an entire
//              bank-wide crawl because a heuristic could not run trades a
//              certain, total availability loss against a partial, probabilistic
//              risk reduction. So the crawl proceeds, loudly: a WARNING per item
//              plus content_gate_scanner_unavailable_total{channel="text"}, and
//              the item is stamped warn:scan-unavailable so the gap is visible
//              in the index itself rather than only in a log nobody reads.
//
// Both defaults are overridable (CONTENT_GATE_FAIL_MODE, or the per-channel
// CONTENT_GATE_FAIL_MODE_BINARY / _TEXT) because the right answer is a
// deployment's risk appetite, not ours. The DEFAULTS are ours.
//
// MASTER SWITCH: CONTENT_GATE, default OFF. With it unset nothing here is
// constructed, no rules file is read, no property is stamped and no scan runs —
// behaviour is byte-identical to the pre-gate connector.

using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Security;

/// <summary>Dead-letter / metric category names for a gate verdict.</summary>
public static class ContentGateCategories
{
    public const string Malware = "malware";
    public const string Injection = "injection";
    public const string ScanUnavailable = "scan-unavailable";

    /// <summary>The dead-letter reason string for <paramref name="category"/>.</summary>
    public static string Reason(string category) => "content-gate:" + category;
}

/// <summary>The <c>contentGateStatus</c> property stamped on every gated item.</summary>
public static class ContentGateStatusValues
{
    public const string PropertyName = "contentGateStatus";

    public const string Clean = "clean";
    public const string WarnScanUnavailable = "warn:scan-unavailable";
    public const string QuarantinedInjection = "quarantined:injection";
    public const string QuarantinedScanUnavailable = "quarantined:scan-unavailable";
    public const string QuarantinedMalware = "quarantined:malware";

    /// <summary>Ascending severity, so the worst channel result wins the stamp.</summary>
    private static readonly string[] Severity =
    {
        Clean, WarnScanUnavailable, QuarantinedInjection, QuarantinedScanUnavailable, QuarantinedMalware,
    };

    /// <summary>Return whichever of the two statuses is more severe.</summary>
    public static string Escalate(string? current, string candidate)
    {
        if (current is null)
            return candidate;
        return Array.IndexOf(Severity, candidate) > Array.IndexOf(Severity, current) ? candidate : current;
    }
}

/// <summary>Environment-driven ContentGate configuration.</summary>
public sealed class ContentGateSettings
{
    public const string EnabledEnvVar = "CONTENT_GATE";
    public const string IcapUrlEnvVar = "CONTENT_GATE_ICAP_URL";
    public const string FailModeEnvVar = "CONTENT_GATE_FAIL_MODE";
    public const string FailModeBinaryEnvVar = "CONTENT_GATE_FAIL_MODE_BINARY";
    public const string FailModeTextEnvVar = "CONTENT_GATE_FAIL_MODE_TEXT";
    public const string MaxScanMbEnvVar = "CONTENT_GATE_MAX_SCAN_MB";
    public const string ScanTimeoutEnvVar = "CONTENT_GATE_SCAN_TIMEOUT_SECONDS";

    public const string FailClosed = "closed";
    public const string FailOpen = "open";

    /// <summary>CONTENT_GATE — master switch. Default OFF.</summary>
    public bool Enabled { get; init; }

    /// <summary>CONTENT_GATE_ICAP_URL — unset means the binary channel is not wired at all.</summary>
    public string? IcapUrl { get; init; }

    /// <summary>Binary/malware fail mode. Default <c>closed</c>.</summary>
    public string BinaryFailMode { get; init; } = FailClosed;

    /// <summary>Text/injection fail mode. Default <c>open</c>.</summary>
    public string TextFailMode { get; init; } = FailOpen;

    /// <summary>CONTENT_GATE_MAX_SCAN_MB, in bytes. Default 25 MB.</summary>
    public long MaxScanBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Per-scan gateway timeout. Default 30s.</summary>
    public int ScanTimeoutSeconds { get; init; } = 30;

    /// <summary>Read the gate configuration from the process environment.</summary>
    public static ContentGateSettings FromEnvironment()
    {
        // Per-channel knob wins over the master knob, which wins over the
        // channel's own (asymmetric) default.
        string FailMode(string specific, string channelDefault) =>
            Settings.EnvOr(specific, Settings.EnvOr(FailModeEnvVar, channelDefault)).ToLowerInvariant();

        return new ContentGateSettings
        {
            Enabled = Settings.BoolEnv(EnabledEnvVar),
            IcapUrl = Environment.GetEnvironmentVariable(IcapUrlEnvVar)?.Trim() is { Length: > 0 } url
                ? url
                : null,
            BinaryFailMode = FailMode(FailModeBinaryEnvVar, FailClosed),
            TextFailMode = FailMode(FailModeTextEnvVar, FailOpen),
            MaxScanBytes = Math.Max(1, Settings.IntEnv(MaxScanMbEnvVar, 25)) * 1024L * 1024L,
            ScanTimeoutSeconds = Math.Max(1, Settings.IntEnv(ScanTimeoutEnvVar, 30)),
        };
    }

    /// <summary>
    /// Fail fast on a mistyped knob — but ONLY when the gate is on, so an unset
    /// CONTENT_GATE can never introduce a new startup failure.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
            return;

        foreach (var (name, value) in new[]
        {
            (FailModeBinaryEnvVar, BinaryFailMode),
            (FailModeTextEnvVar, TextFailMode),
        })
        {
            if (value is not (FailClosed or FailOpen))
            {
                throw new ConfigException(
                    $"Invalid configuration: {name} (or {FailModeEnvVar}) is '{value}': "
                    + $"expected '{FailClosed}' or '{FailOpen}'.");
            }
        }

        if (IcapUrl is not null
            && (!Uri.TryCreate(IcapUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")))
        {
            throw new ConfigException(
                $"Invalid configuration: {IcapUrlEnvVar} '{IcapUrl}' is not an absolute http(s) URL.");
        }
    }

    /// <summary>True when <paramref name="channel"/> should QUARANTINE on an incomplete scan.</summary>
    public bool FailsClosed(bool binary) =>
        (binary ? BinaryFailMode : TextFailMode) == FailClosed;
}

/// <summary>One gate decision for one channel.</summary>
/// <param name="Blocked">The item must be quarantined.</param>
/// <param name="Category">One of <see cref="ContentGateCategories"/>; empty when nothing fired.</param>
/// <param name="ScannerUnavailable">The scan could not complete (regardless of the fail-mode outcome).</param>
public readonly record struct ContentGateVerdict(
    bool Blocked,
    string Category,
    string Detail,
    IReadOnlyList<string> Signals,
    bool ScannerUnavailable)
{
    public static ContentGateVerdict Pass() =>
        new(false, "", "clean", Array.Empty<string>(), false);
}

/// <summary>
/// The ContentGate stage. Constructed only when CONTENT_GATE is on, and
/// INDEPENDENTLY of CLASSIFICATION — the injection gate must not be silently
/// disabled just because advisory classification happens to be off.
/// </summary>
public sealed class ContentGate
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.contentgate");

    private readonly ContentGateSettings _settings;
    private readonly InjectionScanner _injection;
    private readonly IMalwareScanner? _malware;

    public ContentGate(
        ContentGateSettings settings,
        ClassificationRules injectionRules,
        IMalwareScanner? malwareScanner)
    {
        _settings = settings;
        _injection = new InjectionScanner(injectionRules);
        _malware = malwareScanner;

        if (_malware is null)
        {
            Logger.Warning(
                $"{ContentGateSettings.EnabledEnvVar} is ON but no malware scanner is configured "
                + $"({ContentGateSettings.IcapUrlEnvVar} is unset) — downloaded BINARY payloads are NOT "
                + "scanned for malware; only the text injection heuristic is active. Configure the "
                + "gateway to close this gap.");
        }
        if (!_injection.IsOperational)
        {
            Logger.Warning(
                $"{ContentGateSettings.EnabledEnvVar} is ON but the injection ruleset has ZERO usable "
                + $"patterns (none compiled, or none carries a '{InjectionRules.CategoryPrefix}' category, "
                + "which is what routes a hit to quarantine) — the TEXT channel is UNAVAILABLE and fail "
                + $"mode '{_settings.TextFailMode}' applies to every item. Check config/content-gate.json.");
        }
    }

    /// <summary>True when a malware scanner is actually wired up.</summary>
    public bool BinaryScanningConfigured => _malware is not null;

    /// <summary>
    /// BINARY channel. Returns <see cref="ContentGateVerdict.Pass"/> when no
    /// scanner is configured (the channel is off, not "clean by assumption" —
    /// the constructor already warned about it loudly).
    /// </summary>
    public async Task<ContentGateVerdict> ScanBinaryAsync(
        byte[] payload, string fileName, CancellationToken ct)
    {
        if (_malware is null)
            return ContentGateVerdict.Pass();

        // Oversize is an UNSCANNED payload, not a clean one, so it takes the
        // same fail-mode path as an unreachable gateway.
        if (payload.LongLength > _settings.MaxScanBytes)
        {
            return Unavailable(
                binary: true,
                $"payload is {payload.LongLength} bytes, over the "
                + $"{ContentGateSettings.MaxScanMbEnvVar} limit of {_settings.MaxScanBytes} bytes");
        }

        MalwareVerdict verdict;
        try
        {
            verdict = await _malware.ScanAsync(payload, fileName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // graceful stop, not a scanner failure
        }
        catch (Exception ex)
        {
            // A scanner that BLOWS UP must not read as "clean" — that would be
            // the single most dangerous failure this stage could have.
            verdict = MalwareVerdict.Unavailable($"scanner threw {ex.GetType().Name}: {ex.Message}");
        }

        return verdict.Status switch
        {
            MalwareScanStatus.Infected => new ContentGateVerdict(
                Blocked: true,
                ContentGateCategories.Malware,
                verdict.Detail,
                new[] { verdict.Signature ?? "unknown" },
                ScannerUnavailable: false),
            MalwareScanStatus.Unavailable => Unavailable(binary: true, verdict.Detail),
            _ => ContentGateVerdict.Pass(),
        };
    }

    /// <summary>
    /// TEXT channel over the FINAL indexed text. A regex TIMEOUT is not a
    /// scanner outage — it is one pathological document that could not be
    /// examined — so it fails SAFE for that item (quarantine) rather than
    /// taking the fail-open path that a whole-scanner outage takes.
    /// </summary>
    public ContentGateVerdict ScanText(string? text)
    {
        if (!_injection.IsOperational)
        {
            return Unavailable(
                binary: false,
                "the injection ruleset has no usable patterns (none compiled, or no category carries the "
                + $"'{InjectionRules.CategoryPrefix}' prefix, so no match could ever signal)");
        }

        var result = _injection.Scan(text);
        if (result.Incomplete)
        {
            return new ContentGateVerdict(
                Blocked: true,
                ContentGateCategories.Injection,
                "the injection scan did not complete (regex match timeout) — failing safe",
                result.Signals,
                ScannerUnavailable: false);
        }
        if (result.Suspicious)
        {
            return new ContentGateVerdict(
                Blocked: true,
                ContentGateCategories.Injection,
                "injection signals: " + string.Join(", ", result.Signals),
                result.Signals,
                ScannerUnavailable: false);
        }
        return ContentGateVerdict.Pass();
    }

    /// <summary>Apply the channel's fail mode to a scan that could not complete.</summary>
    private ContentGateVerdict Unavailable(bool binary, string detail)
    {
        var channel = binary ? "binary" : "text";
        Metrics.IncContentGateScannerUnavailable(channel);
        var failClosed = _settings.FailsClosed(binary);

        if (failClosed)
        {
            Logger.Error(
                $"ContentGate {channel} scan could not complete ({detail}) — fail mode CLOSED: "
                + "the item is QUARANTINED (unscanned content is never indexed).");
        }
        else
        {
            Logger.Warning(
                $"ContentGate {channel} scan could not complete ({detail}) — fail mode OPEN: "
                + "the item PROCEEDS UNSCANNED. This is a deliberate availability trade for a "
                + "heuristic control; the item is stamped so the gap is visible in the index.");
        }

        return new ContentGateVerdict(
            Blocked: failClosed,
            ContentGateCategories.ScanUnavailable,
            detail,
            Array.Empty<string>(),
            ScannerUnavailable: true);
    }

    /// <summary>
    /// Build the production malware scanner for <paramref name="settings"/>, or
    /// null when no gateway URL is configured.
    /// </summary>
    public static IMalwareScanner? CreateScanner(ContentGateSettings settings) =>
        string.IsNullOrWhiteSpace(settings.IcapUrl)
            ? null
            : new IcapMalwareScanner(
                settings.IcapUrl!,
                timeout: TimeSpan.FromSeconds(settings.ScanTimeoutSeconds));
}
