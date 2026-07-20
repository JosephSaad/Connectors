// ContentGate/InjectionScanner.cs
// -------------------------------
// Dependency-free, config-driven prompt-injection heuristic. Runs over the
// FINAL INDEXED TEXT — the exact string that becomes Copilot grounding context
// — so it sees what an attacker actually gets in front of the model, not the
// raw source row.
//
// This is explicitly a HEURISTIC, not a security boundary. It exists to stop
// the obvious, high-volume shapes (imperative overrides, role reassignment,
// exfiltration directives, hidden text, smuggled base64) with a false-positive
// rate low enough to run against a bank's document corpus. That framing is why
// the TEXT path fails OPEN on a scanner outage while the BINARY path fails
// CLOSED (see ContentGateStage).
//
// Design rules, all load-bearing:
//   • Patterns live in config/content-gate.json, never in code — a new attack
//     shape is a config edit, not a redeploy.
//   • Every pattern is compiled ONCE at construction with a per-pattern match
//     timeout, because it runs against attacker-influenced text.
//   • A match timeout FAILS SAFE: the scan is INCOMPLETE, so the document is
//     reported suspicious rather than silently "clean". Treating a timeout as
//     "no match" would hand an attacker a trivial bypass (feed the scanner a
//     pathological input and the gate opens).
//   • Quote-awareness: a match that sits entirely inside quotation marks is
//     suppressed, so a security-awareness memo that QUOTES an attack phrase in
//     prose is not quarantined. One unquoted occurrence anywhere still fires.

using System.Text.Json;
using System.Text.RegularExpressions;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.ContentGate;

/// <summary>One compiled detection rule.</summary>
public sealed class InjectionPattern
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required Regex Regex { get; init; }

    /// <summary>When true (the default), a match fully enclosed in quotation
    /// marks does not count — prose that quotes an attack phrase is benign.</summary>
    public bool QuoteAware { get; init; } = true;
}

/// <summary>
/// Scan result. <see cref="Incomplete"/> distinguishes "we looked and found
/// something" from "we could not finish looking" — both are suspicious, but
/// only the latter is a scanner-health signal.
/// </summary>
public readonly record struct InjectionVerdict(
    bool Suspicious, string Category, string PatternName, bool Incomplete)
{
    public static InjectionVerdict Clean { get; } = new(false, string.Empty, string.Empty, false);

    public static InjectionVerdict Hit(string category, string patternName) =>
        new(true, category, patternName, false);

    /// <summary>A pattern timed out: suspicious AND incomplete (fail safe).</summary>
    public static InjectionVerdict Timeout(string patternName) =>
        new(true, GateCategories.InjectionScanTimeout, patternName, true);

    /// <summary>
    /// The text was longer than the scan cap, so only a prefix was examined and
    /// nothing matched in it. INCOMPLETE but NOT suspicious: unlike a timeout
    /// there is no attacker-triggerable component (an attacker cannot make the
    /// operator's cap smaller), so this defers to the configured text fail mode
    /// instead of blocking outright. What it must never do is report Clean.
    /// </summary>
    public static InjectionVerdict Truncated { get; } =
        new(false, GateCategories.InjectionScanTruncated, string.Empty, true);
}

public sealed class InjectionScanner
{
    /// <summary>Config file name, resolved next to the other config assets.</summary>
    public const string ConfigFileName = "content-gate.json";

    /// <summary>Bounded per-pattern match time. Config patterns run against
    /// attacker-influenced text, so catastrophic backtracking must time out
    /// rather than hang a crawl. (Same guard as the content classifier.)</summary>
    internal static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.contentgate");

    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Characters treated as quotation delimiters for quote-awareness.
    /// The apostrophe is deliberately EXCLUDED — "the user's file" would make
    /// every later match on the line look quoted.</summary>
    private const string StraightQuotes = "\"`";
    private const string QuoteOpeners = "“‘«";
    private const string QuoteClosers = "”’»";

    private readonly IReadOnlyList<InjectionPattern> _patterns;

    public InjectionScanner(IReadOnlyList<InjectionPattern> patterns) => _patterns = patterns;

    public IReadOnlyList<InjectionPattern> Patterns => _patterns;

    /// <summary>Number of usable compiled patterns. Zero means the scanner is
    /// BLIND — the stage treats that as an outage and applies the text fail
    /// mode rather than reporting everything clean.</summary>
    public int PatternCount => _patterns.Count;

    /// <summary>
    /// Load the shipped pattern set. Probes the working directory first (the
    /// deployed layout) and then the assembly directory (the test/output
    /// layout), mirroring how the other config assets resolve.
    /// <para>
    /// A missing or unreadable file yields an EMPTY scanner, NOT a crash and
    /// NOT a silent pass: PatternCount 0 is the "scanner unavailable" signal
    /// that drives the documented text fail mode and the
    /// content_gate_scan_unavailable_total metric.
    /// </para>
    /// </summary>
    public static InjectionScanner Load(string? path = null)
    {
        foreach (var candidate in CandidatePaths(path))
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;
                var scanner = FromJson(File.ReadAllText(candidate));
                if (scanner.PatternCount == 0)
                {
                    Logger.Error(
                        $"ContentGate: '{candidate}' defined no usable injection patterns. "
                        + "The text scanner is BLIND — the CONTENT_GATE_FAIL_MODE_TEXT policy applies.");
                }
                return scanner;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.Error(
                    $"ContentGate: could not read injection patterns from '{candidate}' "
                    + $"({exc.GetType().Name}: {exc.Message}). The text scanner is BLIND — "
                    + "the CONTENT_GATE_FAIL_MODE_TEXT policy applies.");
                return new InjectionScanner(Array.Empty<InjectionPattern>());
            }
        }

        Logger.Error(
            $"ContentGate: no '{ConfigFileName}' found (looked in "
            + $"{string.Join(", ", CandidatePaths(path))}). The text scanner is BLIND — "
            + "the CONTENT_GATE_FAIL_MODE_TEXT policy applies.");
        return new InjectionScanner(Array.Empty<InjectionPattern>());
    }

    private static IEnumerable<string> CandidatePaths(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath!;
            yield break;
        }
        yield return Path.Combine(Directory.GetCurrentDirectory(), "config", ConfigFileName);
        yield return Path.Combine(AppContext.BaseDirectory, "config", ConfigFileName);
    }

    /// <summary>
    /// Parse and COMPILE a pattern set. An individual invalid regex is skipped
    /// with a warning rather than taking down the whole set — but the skip is
    /// said out loud, because a silently dropped pattern means a detection
    /// category quietly stopped working.
    /// </summary>
    internal static InjectionScanner FromJson(string json, TimeSpan? matchTimeout = null)
    {
        var timeout = matchTimeout ?? DefaultMatchTimeout;
        var patterns = new List<InjectionPattern>();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("patterns", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                var category = entry.TryGetProperty("category", out var c) ? c.GetString() : null;
                var regex = entry.TryGetProperty("regex", out var r) ? r.GetString() : null;
                var quoteAware = !entry.TryGetProperty("quoteAware", out var q)
                    || q.ValueKind != JsonValueKind.False;

                if (string.IsNullOrWhiteSpace(regex) || string.IsNullOrWhiteSpace(category))
                    continue;

                try
                {
                    patterns.Add(new InjectionPattern
                    {
                        Name = name ?? "pattern",
                        Category = category!,
                        Regex = new Regex(regex!, Options, timeout),
                        QuoteAware = quoteAware,
                    });
                }
                catch (ArgumentException exc)
                {
                    Logger.Warning(
                        $"{ConfigFileName}: pattern '{name ?? "pattern"}' (category '{category}') "
                        + $"is not a valid regex and was skipped ({exc.Message}). "
                        + "Detection for this pattern is OFF until it is fixed.");
                }
            }
        }
        return new InjectionScanner(patterns);
    }

    /// <summary>
    /// Scan <paramref name="text"/> and return the first pattern hit, in file
    /// order. Text longer than <paramref name="maxScanChars"/> is truncated —
    /// the scan is bounded regardless of attachment size. Never throws.
    /// <para>
    /// When the text WAS truncated and nothing matched in the scanned prefix the
    /// result is <see cref="InjectionVerdict.Truncated"/>, NOT Clean: the tail
    /// was never looked at, so "no match" would be a claim the scanner cannot
    /// make. A hit inside the prefix is still a hit and wins — truncation only
    /// affects the absence of evidence, not its presence.
    /// </para>
    /// </summary>
    public InjectionVerdict Scan(string? text, int maxScanChars = 1 << 20)
    {
        if (string.IsNullOrWhiteSpace(text) || _patterns.Count == 0)
            return InjectionVerdict.Clean;

        var truncated = text!.Length > maxScanChars;
        var scan = truncated ? text[..maxScanChars] : text;
        // A leading byte-order mark is a file artefact, not hidden text.
        if (scan.Length > 0 && scan[0] == '﻿')
            scan = scan[1..];

        foreach (var pattern in _patterns)
        {
            try
            {
                foreach (Match match in pattern.Regex.Matches(scan))
                {
                    if (pattern.QuoteAware && IsQuoted(scan, match.Index, match.Index + match.Length))
                        continue;   // a quoted mention in prose is not an instruction
                    return InjectionVerdict.Hit(pattern.Category, pattern.Name);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // FAIL SAFE. Treating this as "no match" would let an attacker
                // open the gate by feeding it a pathological input.
                Logger.Warning(
                    $"ContentGate: injection pattern '{pattern.Name}' timed out — the scan is "
                    + "INCOMPLETE, so the content is treated as suspicious (fail safe).");
                return InjectionVerdict.Timeout(pattern.Name);
            }
        }

        // No hit in what we actually read. Only claim "clean" for text we read
        // in full — otherwise the caller must apply the text fail mode.
        return truncated ? InjectionVerdict.Truncated : InjectionVerdict.Clean;
    }

    /// <summary>
    /// True when the match at [<paramref name="matchStart"/>,
    /// <paramref name="matchEnd"/>) sits inside quotation marks on its own line
    /// — an odd number of straight quotes before it, or an unclosed typographic
    /// opener before it with a closer after it.
    /// </summary>
    internal static bool IsQuoted(string text, int matchStart, int matchEnd)
    {
        if (matchStart < 0 || matchStart > text.Length)
            return false;

        var lineStart = matchStart == 0 ? 0 : text.LastIndexOf('\n', matchStart - 1) + 1;
        var lineEnd = matchEnd >= text.Length ? text.Length : text.IndexOf('\n', matchEnd);
        if (lineEnd < 0)
            lineEnd = text.Length;

        var straight = 0;
        var depth = 0;
        for (var i = lineStart; i < matchStart; i++)
        {
            var ch = text[i];
            if (StraightQuotes.IndexOf(ch) >= 0)
                straight++;
            else if (QuoteOpeners.IndexOf(ch) >= 0)
                depth++;
            else if (QuoteClosers.IndexOf(ch) >= 0 && depth > 0)
                depth--;
        }

        if (straight % 2 == 1)
            return true;

        if (depth > 0)
        {
            for (var i = matchEnd; i < lineEnd; i++)
            {
                if (QuoteClosers.IndexOf(text[i]) >= 0)
                    return true;
            }
        }
        return false;
    }
}
