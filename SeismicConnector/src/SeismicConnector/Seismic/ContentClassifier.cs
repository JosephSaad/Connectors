// Seismic/ContentClassifier.cs
// ----------------------------
// Unified data classification & sensitivity labeling (Data Governance &
// Compliance — Purview-aligned classification/labeling, PII controls).
//
// A DEPENDENCY-FREE, regex/heuristic classifier over the extracted content
// text. It detects sensitive-data categories (PII: email/phone/national-id;
// PCI: Luhn-valid card numbers; secrets: key-like tokens) and derives a single
// unified sensitivity label for every ingested externalItem:
//
//     Public < Internal < Confidential < Restricted
//
// The label is derived with a fixed precedence (see DeriveLabel):
//   1. any detected category (PII/PCI/secret) OR MNE-adjacency  → Restricted
//   2. distribution "internal-only"                             → Confidential
//   3. distribution "client-approved"                           → Internal
//   4. otherwise                                                → the configured
//                                                                 default label
//
// The existing `distribution` property is one INPUT to this scheme (and is kept
// on the item unchanged). The No-MNE exclusion is untouched: excluded content
// is still never ingested — classification only ever runs over what IS
// ingested, and "MNE-adjacency" here is a BODY-TEXT heuristic (keyword hits in
// the indexed text), distinct from the property-flag-based exclusion filter.
//
// Patterns are config-driven (config/classification.json) with a sensible
// built-in default when the file is absent. Everything runs offline; there is
// no live Purview API call.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SeismicConnector.Seismic;

/// <summary>The unified sensitivity taxonomy (ascending sensitivity).</summary>
public enum SensitivityLabel
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Restricted = 3,
}

/// <summary>A regex detection rule: a match anywhere in the text adds <see cref="Category"/>.</summary>
public sealed class ClassificationPattern
{
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("regex")]
    public required string Regex { get; init; }
}

/// <summary>Luhn (card-number) detection: candidate digit runs, then a checksum test.</summary>
public sealed class LuhnRule
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = "PCI.CardNumber";

    [JsonPropertyName("candidateRegex")]
    public string CandidateRegex { get; init; } = "\\d(?:[ \\-]?\\d){12,18}";
}

/// <summary>Deserialized config/classification.json.</summary>
public sealed class ClassificationRules
{
    /// <summary>Label applied when nothing else escalates (parsed to <see cref="SensitivityLabel"/>).</summary>
    [JsonPropertyName("defaultLabel")]
    public string DefaultLabel { get; init; } = "Internal";

    [JsonPropertyName("patterns")]
    public List<ClassificationPattern> Patterns { get; init; } = new();

    [JsonPropertyName("luhn")]
    public LuhnRule? Luhn { get; init; }

    /// <summary>Body-text keywords that mark content as MNE-adjacent (escalates to Restricted).</summary>
    [JsonPropertyName("mneAdjacentKeywords")]
    public List<string> MneAdjacentKeywords { get; init; } = new();

    /// <summary>Category name attached to MNE-adjacent hits.</summary>
    public const string MneAdjacentCategory = "MNE.Adjacent";

    /// <summary>Load rules from <paramref name="path"/>, falling back to the built-in default set.</summary>
    public static ClassificationRules LoadFile(string path)
    {
        if (!File.Exists(path))
            return Default();
        return JsonSerializer.Deserialize<ClassificationRules>(File.ReadAllText(path)) ?? Default();
    }

    /// <summary>Sensible built-in ruleset (mirrors the shipped config/classification.json).</summary>
    public static ClassificationRules Default() => new()
    {
        DefaultLabel = "Internal",
        Patterns = new List<ClassificationPattern>
        {
            new() { Category = "PII.Email", Regex = @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b" },
            new() { Category = "PII.Phone", Regex = @"\b(?:\+?\d{1,3}[\s.\-]?)?\(?\d{3}\)?[\s.\-]?\d{3}[\s.\-]?\d{4}\b" },
            new() { Category = "PII.NationalId", Regex = @"\b\d{3}-\d{2}-\d{4}\b" },
            new() { Category = "Secret.ApiKey", Regex = @"\b(?:AKIA[0-9A-Z]{16}|(?:sk|pk|api|key|token|secret)[_\-][A-Za-z0-9]{16,})\b" },
        },
        Luhn = new LuhnRule(),
        MneAdjacentKeywords = new List<string>
        {
            "material non-public", "material nonpublic", "MNPI",
            "insider information", "embargoed", "under embargo",
        },
    };
}

/// <summary>The outcome of classifying one item: its label + detected categories.</summary>
public readonly record struct ClassificationResult(
    SensitivityLabel Label,
    IReadOnlyList<string> Categories);

/// <summary>
/// Regex/heuristic content classifier. Stateless after construction and safe to
/// share across the concurrent teamsite/content workers. Every regex is compiled
/// with a bounded match timeout so a pathological input cannot hang a crawl.
/// </summary>
public sealed class ContentClassifier
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private readonly List<(string Category, Regex Regex)> _patterns = new();
    private readonly (string Category, Regex Regex)? _luhn;
    private readonly List<string> _mneKeywords;
    private readonly SensitivityLabel _defaultLabel;

    public ContentClassifier(ClassificationRules rules)
    {
        foreach (var pattern in rules.Patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.Category) || string.IsNullOrWhiteSpace(pattern.Regex))
                continue;
            if (TryCompile(pattern.Regex, out var regex))
                _patterns.Add((pattern.Category.Trim(), regex!));
        }

        if (rules.Luhn is { } luhn
            && !string.IsNullOrWhiteSpace(luhn.CandidateRegex)
            && TryCompile(luhn.CandidateRegex, out var luhnRegex))
        {
            _luhn = (string.IsNullOrWhiteSpace(luhn.Category) ? "PCI.CardNumber" : luhn.Category.Trim(), luhnRegex!);
        }

        _mneKeywords = rules.MneAdjacentKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToList();

        _defaultLabel = ParseLabel(rules.DefaultLabel, SensitivityLabel.Internal);
    }

    private static bool TryCompile(string pattern, out Regex? regex)
    {
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;  // invalid config pattern — skip it rather than crash a crawl
            return false;
        }
    }

    /// <summary>Parse a label name (case-insensitive); returns <paramref name="fallback"/> when unrecognised.</summary>
    public static SensitivityLabel ParseLabel(string? name, SensitivityLabel fallback) =>
        Enum.TryParse<SensitivityLabel>((name ?? "").Trim(), ignoreCase: true, out var label)
            ? label
            : fallback;

    /// <summary>
    /// Detect sensitive categories in <paramref name="text"/>. Returns the
    /// distinct, stably ordered category list (including <c>MNE.Adjacent</c>
    /// when a keyword hits). Never throws — a regex timeout is treated as "no
    /// match" for that pattern.
    /// </summary>
    public IReadOnlyList<string> Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        var categories = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (category, regex) in _patterns)
        {
            if (SafeIsMatch(regex, text))
                categories.Add(category);
        }

        if (_luhn is { } luhn && HasLuhnValidNumber(luhn.Regex, text))
            categories.Add(luhn.Category);

        if (IsMneAdjacent(text))
            categories.Add(ClassificationRules.MneAdjacentCategory);

        return categories.ToList();
    }

    /// <summary>True when the body text contains any configured MNE-adjacency keyword.</summary>
    public bool IsMneAdjacent(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var keyword in _mneKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool HasLuhnValidNumber(Regex candidateRegex, string text)
    {
        try
        {
            foreach (Match match in candidateRegex.Matches(text))
            {
                var digits = new string(match.Value.Where(char.IsDigit).ToArray());
                if (digits.Length is >= 13 and <= 19 && IsLuhnValid(digits))
                    return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return false;
    }

    /// <summary>Luhn checksum over a pure-digit string (used for card-number validation).</summary>
    public static bool IsLuhnValid(string digits)
    {
        if (string.IsNullOrEmpty(digits) || !digits.All(char.IsDigit))
            return false;
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9)
                    n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    /// <summary>
    /// Classify the item: detect categories in <paramref name="text"/> and
    /// derive the unified label from the categories and the item's
    /// <paramref name="distribution"/>.
    /// </summary>
    public ClassificationResult Classify(string? text, string? distribution)
    {
        var categories = Detect(text);
        var label = DeriveLabel(distribution, categories, _defaultLabel);
        return new ClassificationResult(label, categories);
    }

    /// <summary>
    /// Fixed-precedence label derivation:
    /// any detected category (sensitive data or MNE-adjacent) ⇒ Restricted;
    /// else distribution internal-only ⇒ Confidential, client-approved ⇒ Internal;
    /// else the configured default.
    /// </summary>
    public static SensitivityLabel DeriveLabel(
        string? distribution, IReadOnlyCollection<string> detectedCategories, SensitivityLabel defaultLabel)
    {
        if (detectedCategories.Count > 0)
            return SensitivityLabel.Restricted;

        return (distribution ?? "").Trim().ToLowerInvariant() switch
        {
            "internal-only" => SensitivityLabel.Confidential,
            "client-approved" => SensitivityLabel.Internal,
            _ => defaultLabel,
        };
    }
}
