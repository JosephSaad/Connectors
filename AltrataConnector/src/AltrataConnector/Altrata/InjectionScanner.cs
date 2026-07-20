// Altrata/InjectionScanner.cs
// ---------------------------
// Heuristic prompt-injection detection over the FINAL INDEXED TEXT (the body
// ItemTransformer.BuildContent assembles, plus the item's string properties).
//
// WHY: ingested content becomes Copilot grounding context, so a poisoned record
// is an attack on every user whose query it grounds — not just on the person the
// record describes. This scanner is the text half of the ContentGate stage
// (see ContentGate.cs).
//
// WHAT IT IS NOT: a security boundary. It is a bounded regex heuristic. It can
// be evaded (see the mention guard caveat below) and it can be wrong. That is
// exactly why the text fail mode ships FAIL-OPEN — blocking a whole crawl on a
// heuristic outage is worse than the residual risk. Treat a hit as "quarantine
// for human review", never as proof of malice.
//
// PII CONTRACT (hard requirement for this connector): a ContentScanResult
// carries a CATEGORY and nothing else. Never the matched text, never a snippet,
// never an offset into the value, never the field name. The connector holds
// names, employers and net-worth figures; none of them may reach a log line, a
// metric label, a dead-letter reason or a ledger entry.
//
// DESIGN NOTES
//   * Patterns are DATA (InjectionPattern records), compiled ONCE into a
//     Regex table with a per-pattern match timeout, and fully replaceable from
//     a JSON file (CONTENT_GATE_PATTERNS_PATH). config/content-gate-patterns.example.json
//     is the shipped copy of the built-in table (a test pins them equal).
//   * A match TIMEOUT fails SAFE: the scan is reported INCOMPLETE, never as
//     "no match". ContentGate then applies the configured fail mode.
//   * Every pattern runs twice when needed: once over the raw text, once over a
//     NORMALIZED copy with zero-width / bidi / soft-hyphen characters stripped,
//     so "i<ZWSP>gnore all previous instructions" cannot slip past a naive regex.
//   * MENTION GUARD (false-positive control): ordinary business text discusses
//     these phrases — a compliance memo quoting "ignore previous instructions"
//     must not be quarantined. A match is treated as a MENTION (not a directive)
//     when it is wrapped in quotation marks, or when a citation cue ("says",
//     "the phrase", "for example", "warns about"...) appears in the preceding
//     window. CAVEAT, stated plainly: this is an intentional, documented
//     evasion — an attacker can prefix their payload with `The memo says`. That
//     trade is acceptable precisely because this is a heuristic and not a
//     boundary; the alternative (quarantining every training deck) destroys the
//     signal for operators.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AltrataConnector.Config;

namespace AltrataConnector.Altrata;

/// <summary>PII-safe verdict vocabulary. These strings reach logs, metrics,
/// dead-letter reasons and the decision ledger — so they are the ONLY thing a
/// scan is ever allowed to say about the content it looked at.</summary>
public static class ContentGateCategories
{
    public const string ImperativeOverride = "injection.imperative-override";
    public const string RoleReassignment = "injection.role-reassignment";
    public const string Exfiltration = "injection.exfiltration";
    public const string HiddenText = "injection.hidden-text";
    public const string EncodedBlob = "injection.encoded-blob";

    /// <summary>The scan did not complete (scanner unavailable, match timeout,
    /// or content larger than CONTENT_GATE_MAX_SCAN_MB). NOT a clean result.</summary>
    public const string ScanIncomplete = "scan-incomplete";
}

/// <summary>PII-safe detail codes explaining an incomplete scan (fixed
/// vocabulary — never derived from content).</summary>
public static class ContentGateDetails
{
    public const string ScannerUnavailable = "scanner-unavailable";
    public const string ScanTimeout = "scan-timeout";
    public const string ScanTruncated = "scan-truncated";
}

public enum ContentScanOutcome
{
    /// <summary>Fully scanned, nothing found.</summary>
    Clean = 0,
    /// <summary>A pattern matched — quarantine.</summary>
    Blocked = 1,
    /// <summary>The scan did not complete. NEVER equivalent to Clean; the
    /// configured fail mode decides what happens next.</summary>
    Incomplete = 2,
}

/// <summary>A scan verdict. Category + detail only — see the PII contract.</summary>
public sealed record ContentScanResult
{
    public required ContentScanOutcome Outcome { get; init; }

    /// <summary>One of <see cref="ContentGateCategories"/>; null when clean.</summary>
    public string? Category { get; init; }

    /// <summary>One of <see cref="ContentGateDetails"/>; null unless incomplete.</summary>
    public string? Detail { get; init; }

    public static readonly ContentScanResult Ok = new() { Outcome = ContentScanOutcome.Clean };

    public static ContentScanResult Blocked(string category) =>
        new() { Outcome = ContentScanOutcome.Blocked, Category = category };

    public static ContentScanResult Incomplete(string detail) =>
        new()
        {
            Outcome = ContentScanOutcome.Incomplete,
            Category = ContentGateCategories.ScanIncomplete,
            Detail = detail,
        };
}

/// <summary>
/// A text content scanner. Implementations MUST NOT return matched text.
/// The production implementation is <see cref="InjectionScanner"/>; tests
/// substitute fakes (including one that always throws, standing in for an
/// unavailable scanner) so no live scanner is ever needed to build or test.
/// </summary>
public interface IContentScanner
{
    ContentScanResult Scan(string text);
}

/// <summary>One configurable detection pattern (the JSON file's element shape).</summary>
public sealed record InjectionPattern
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>PII-safe category reported on a hit (see ContentGateCategories).</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("regex")]
    public required string Regex { get; init; }

    /// <summary>Apply the quoted/cited MENTION guard to this pattern's hits.
    /// Off for structural signals (hidden characters, encoded blobs) where a
    /// "quotation" is meaningless.</summary>
    [JsonPropertyName("mentionGuard")]
    public bool MentionGuard { get; init; } = true;
}

/// <summary>JSON document shape of CONTENT_GATE_PATTERNS_PATH.</summary>
public sealed record InjectionPatternFile
{
    [JsonPropertyName("patterns")]
    public IReadOnlyList<InjectionPattern> Patterns { get; init; } = Array.Empty<InjectionPattern>();
}

public sealed class InjectionScanner : IContentScanner
{
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Characters stripped before the second (normalized) pass:
    /// zero-width space/non-joiner/joiner, word joiner, BOM, soft hyphen, and
    /// the bidi embedding/override/isolate controls.</summary>
    private const string InvisibleChars =
        "\u200B\u200C\u200D\u2060\uFEFF\u00AD" +          // zero-width + soft hyphen
        "\u202A\u202B\u202C\u202D\u202E" +                 // bidi embedding / override
        "\u2066\u2067\u2068\u2069";                        // bidi isolates

    private const int MentionWindow = 90;

    /// <summary>Citation cues that turn a match into a MENTION rather than a
    /// directive. Deliberately conservative — see the evasion caveat up top.</summary>
    private static readonly Regex MentionCue = new(
        @"\b(?:phrase|phrases|wording|quote|quotes|quoted|quoting|says|say|said|saying|" +
        @"warns|warn|warned|warning|example|examples|e\.g\.|such\s+as|mention|mentions|" +
        @"mentioned|reads|titled|labelled|labeled|flagged|reported|so-called)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The built-in pattern table — the DEFAULT data, mirrored byte-for-byte in
    /// config/content-gate-patterns.example.json (pinned by a test). Order
    /// matters only for which category is reported when several would match;
    /// directive patterns come before structural ones so a poisoned instruction
    /// is reported as an instruction, not as "some odd characters".
    /// </summary>
    public static IReadOnlyList<InjectionPattern> DefaultPatterns { get; } = new[]
    {
        // ---- imperative overrides -------------------------------------------
        new InjectionPattern
        {
            Id = "override-instructions",
            Category = ContentGateCategories.ImperativeOverride,
            Regex = @"\b(?:ignore|disregard|override|bypass|forget)\b[^\n]{0,40}?" +
                    @"\b(?:previous|prior|preceding|earlier|above|foregoing|all|any|your|the\s+system)\b" +
                    @"[^\n]{0,20}?\b(?:instructions?|prompts?|directions?|rules?|guardrails?|guidelines?|messages?|context)\b",
        },
        new InjectionPattern
        {
            Id = "disregard-the-above",
            Category = ContentGateCategories.ImperativeOverride,
            Regex = @"\b(?:ignore|disregard)\s+(?:everything\s+)?(?:the\s+)?(?:above|foregoing|preceding)\b",
        },
        new InjectionPattern
        {
            Id = "forget-everything",
            Category = ContentGateCategories.ImperativeOverride,
            Regex = @"\bforget\s+(?:everything|all)\s+(?:above|before|you)\b",
        },
        // ---- role reassignment ------------------------------------------------
        new InjectionPattern
        {
            Id = "role-you-are-now",
            Category = ContentGateCategories.RoleReassignment,
            // Deliberately NOT a bare "you are now": Altrata prose says things
            // like "you are now able to view the profile". A reassignment names
            // the role it is reassigning TO.
            Regex = @"\byou\s+are\s+(?:now\s+)?(?:no\s+longer\s+)?(?:a\s+|an\s+|the\s+)?(?:helpful\s+)?" +
                    @"(?:AI|A\.I\.|assistant|chatbot|language\s+model|LLM|DAN|unrestricted|unfiltered|" +
                    @"jailbroken|developer\s+mode|in\s+developer\s+mode)\b",
        },
        new InjectionPattern
        {
            Id = "role-act-as",
            Category = ContentGateCategories.RoleReassignment,
            // NOT a bare "act as": board/career datasets are full of "acts as a
            // director", "will act as interim chair". Only AI-role targets count.
            Regex = @"\bact(?:s|ing)?\s+as\s+(?:a\s+|an\s+|the\s+)?(?:AI|A\.I\.|assistant|chatbot|" +
                    @"language\s+model|LLM|DAN|system\s+prompt|unrestricted|unfiltered|different\s+AI)\b",
        },
        new InjectionPattern
        {
            Id = "role-new-system-prompt",
            Category = ContentGateCategories.RoleReassignment,
            Regex = @"\b(?:new|updated|revised|replacement)\s+system\s+(?:prompt|message|instructions?)\b",
        },
        new InjectionPattern
        {
            Id = "role-pretend",
            Category = ContentGateCategories.RoleReassignment,
            Regex = @"\bpretend\s+(?:to\s+be|that\s+you|you(?:'re|\s+are))\b",
        },
        new InjectionPattern
        {
            Id = "role-chat-template-marker",
            Category = ContentGateCategories.RoleReassignment,
            // Chat-template control markers have no business in a data feed.
            Regex = @"<\|(?:im_start|im_end|system|endoftext)\|>|\[/?INST\]|<<SYS>>|###\s*(?:system|instruction)\b",
            MentionGuard = false,
        },
        // ---- exfiltration directives -------------------------------------------
        new InjectionPattern
        {
            Id = "exfil-to-url",
            Category = ContentGateCategories.Exfiltration,
            // Requires a SENSITIVE OBJECT between the verb and the URL, so
            // "post the quarterly update to https://wiki..." stays clean.
            Regex = @"\b(?:send|post|upload|transmit|forward|leak|publish)\b[^\n]{0,100}?" +
                    @"\b(?:conversation|chat\s+history|context|system\s+prompt|prompts?|instructions?|" +
                    @"credentials?|api[\s_-]*keys?|tokens?|secrets?|passwords?|private\s+key|" +
                    @"user\s+data|seat\s+list|the\s+above|everything\s+above)\b[^\n]{0,100}?https?://",
        },
        new InjectionPattern
        {
            Id = "exfil-url-carrying-secrets",
            Category = ContentGateCategories.Exfiltration,
            Regex = @"https?://[^\n]{0,120}?\b(?:with|containing|including)\b[^\n]{0,60}?" +
                    @"\b(?:conversation|context|credentials?|api[\s_-]*keys?|tokens?|secrets?|passwords?)\b",
        },
        new InjectionPattern
        {
            Id = "exfil-verb",
            Category = ContentGateCategories.Exfiltration,
            Regex = @"\bexfiltrat(?:e|es|ed|ing|ion)\b",
        },
        // ---- hidden text / structural signals -----------------------------------
        new InjectionPattern
        {
            Id = "hidden-zero-width-run",
            Category = ContentGateCategories.HiddenText,
            // Three or more in a row. A lone ZWJ is legitimate (emoji sequences).
            Regex = @"[\u200B-\u200D\u2060\uFEFF]{3,}",
            MentionGuard = false,
        },
        new InjectionPattern
        {
            Id = "hidden-bidi-control",
            Category = ContentGateCategories.HiddenText,
            Regex = @"[\u202A-\u202E\u2066-\u2069]",
            MentionGuard = false,
        },
        new InjectionPattern
        {
            Id = "encoded-long-base64-run",
            Category = ContentGateCategories.EncodedBlob,
            // A very long base64-DENSE run: mixed case AND digits, 180+ chars.
            // A hex digest (single case, no '+/') and ordinary prose never hit it.
            Regex = @"(?<![A-Za-z0-9+/])(?=[A-Za-z0-9+/]{0,400}[a-z])(?=[A-Za-z0-9+/]{0,400}[A-Z])" +
                    @"(?=[A-Za-z0-9+/]{0,400}[0-9])[A-Za-z0-9+/]{180,}={0,2}",
            MentionGuard = false,
        },
    };

    /// <summary>The shared, compiled default table. Compiled ONCE for the
    /// process (regex construction with RegexOptions.Compiled is expensive).</summary>
    public static InjectionScanner Default { get; } = new(DefaultPatterns, DefaultMatchTimeout);

    private readonly (InjectionPattern Pattern, Regex Compiled)[] _patterns;

    public InjectionScanner(IReadOnlyList<InjectionPattern> patterns, TimeSpan matchTimeout)
    {
        if (matchTimeout <= TimeSpan.Zero)
            throw new ConfigurationError("content-gate match timeout must be positive");

        var compiled = new (InjectionPattern, Regex)[patterns.Count];
        for (var i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            if (string.IsNullOrWhiteSpace(pattern.Id))
                throw new ConfigurationError("content-gate pattern is missing an 'id'");
            if (string.IsNullOrWhiteSpace(pattern.Category))
                throw new ConfigurationError($"content-gate pattern '{pattern.Id}' is missing a 'category'");
            try
            {
                compiled[i] = (pattern, new Regex(pattern.Regex,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                    matchTimeout));
            }
            catch (ArgumentException exc)
            {
                throw new ConfigurationError(
                    $"content-gate pattern '{pattern.Id}' has an invalid regex: {exc.Message}");
            }
        }
        _patterns = compiled;
        PatternIds = compiled.Select(c => c.Item1.Id).ToArray();
    }

    /// <summary>Ids of the compiled patterns, in evaluation order.</summary>
    public IReadOnlyList<string> PatternIds { get; }

    /// <summary>Load a REPLACEMENT pattern table from JSON. Any failure is a
    /// ConfigurationError naming CONTENT_GATE_PATTERNS_PATH, so a typo fails
    /// validate-config / startup rather than silently disabling detection.</summary>
    public static InjectionScanner FromFile(string path, TimeSpan matchTimeout)
    {
        const string setting = ContentGateOptions.PatternsPathEnvVar;
        if (!File.Exists(path))
            throw new ConfigurationError($"{setting} '{path}' does not exist");

        InjectionPatternFile? document;
        try
        {
            document = JsonSerializer.Deserialize<InjectionPatternFile>(File.ReadAllText(path));
        }
        catch (Exception exc) when (exc is JsonException or IOException)
        {
            throw new ConfigurationError($"{setting} '{path}' is unreadable: {exc.Message}");
        }
        if (document == null || document.Patterns.Count == 0)
            throw new ConfigurationError($"{setting} '{path}' defines no patterns");

        try
        {
            return new InjectionScanner(document.Patterns, matchTimeout);
        }
        catch (ConfigurationError exc)
        {
            throw new ConfigurationError($"{setting} '{path}': {exc.Message}");
        }
    }

    /// <summary>
    /// Scan text. Returns a category-only verdict; a match timeout FAILS SAFE as
    /// <see cref="ContentScanOutcome.Incomplete"/>, never as clean.
    /// </summary>
    public ContentScanResult Scan(string text)
    {
        if (string.IsNullOrEmpty(text))
            return ContentScanResult.Ok;

        var normalized = StripInvisible(text);
        foreach (var (pattern, regex) in _patterns)
        {
            try
            {
                if (Hits(regex, pattern, text))
                    return ContentScanResult.Blocked(pattern.Category);
                if (!ReferenceEquals(normalized, text) && Hits(regex, pattern, normalized))
                    return ContentScanResult.Blocked(pattern.Category);
            }
            catch (RegexMatchTimeoutException)
            {
                // FAIL SAFE: an un-finished pattern is an INCOMPLETE scan, never
                // a clean bill of health. ContentGate applies the fail mode.
                return ContentScanResult.Incomplete(ContentGateDetails.ScanTimeout);
            }
        }
        return ContentScanResult.Ok;
    }

    /// <summary>True when the pattern matches somewhere that is not a mention.</summary>
    private static bool Hits(Regex regex, InjectionPattern pattern, string text)
    {
        for (var match = regex.Match(text); match.Success; match = match.NextMatch())
        {
            if (!pattern.MentionGuard || !IsMention(text, match.Index, match.Length))
                return true;
            if (match.Length == 0)
                break;   // defensive: a zero-width pattern would loop forever
        }
        return false;
    }

    /// <summary>
    /// Mention heuristic: the match is being QUOTED or CITED rather than issued.
    /// Rule 1 (strong) — the match is wrapped in quotation marks.
    /// Rule 2 (weak)   — a citation cue appears in the preceding window.
    /// Documented evasion: an attacker may prepend a cue. Accepted trade-off; see
    /// the file header.
    /// </summary>
    private static bool IsMention(string text, int start, int length)
    {
        var before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before]))
            before--;
        var after = start + length;
        while (after < text.Length && (char.IsWhiteSpace(text[after]) || text[after] is '.' or ',' or '!' or '?' or ';' or ':'))
            after++;

        var opened = before >= 0 && IsOpenQuote(text[before]);
        var closed = after < text.Length && IsCloseQuote(text[after]);
        if (opened && closed)
            return true;

        var windowStart = Math.Max(0, start - MentionWindow);
        if (windowStart == start)
            return false;
        try
        {
            return MentionCue.IsMatch(text.AsSpan(windowStart, start - windowStart));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;   // fail safe: cannot prove it is a mention ⇒ treat as a hit
        }
    }

    private static bool IsOpenQuote(char c) => c is '"' or '\'' or '“' or '‘' or '«' or '`';
    private static bool IsCloseQuote(char c) => c is '"' or '\'' or '”' or '’' or '»' or '`';

    /// <summary>Remove zero-width / bidi / soft-hyphen characters. Returns the
    /// SAME instance when nothing was stripped, so the caller can skip the
    /// second pass with a reference comparison.</summary>
    internal static string StripInvisible(string text)
    {
        var found = false;
        foreach (var c in text)
        {
            if (InvisibleChars.IndexOf(c) >= 0)
            {
                found = true;
                break;
            }
        }
        if (!found)
            return text;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (InvisibleChars.IndexOf(c) < 0)
                sb.Append(c);
        }
        return sb.ToString();
    }
}
