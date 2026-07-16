// Content/ContentClassifier.cs
// ----------------------------
// Dependency-free, config-driven content classifier. Scans text (ingested
// fields + attachment text) for sensitive-data categories using the regex/
// heuristic pattern set in config/classification.json. No third-party libs,
// no network — only System.Text.RegularExpressions.
//
// Categories out of the box: PII (email, phone, national-id patterns), PCI
// (card numbers validated with the Luhn checksum, so ordinary long numbers do
// NOT false-positive), and Secret (key-like tokens). Detection returns the set
// of category names that matched; the sensitivity classifier maps those to the
// unified label.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClarizenConnector.Content;

public sealed class ClassificationCategory
{
    public required string Name { get; init; }
    public bool Luhn { get; init; }
    public required IReadOnlyList<(string Name, Regex Pattern)> Patterns { get; init; }
}

public sealed class ContentClassifier
{
    /// <summary>Max characters scanned per item — keeps classification bounded
    /// regardless of attachment size (the extracted text is already capped too).</summary>
    public const int MaxScanChars = 1 * 1024 * 1024;

    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private readonly IReadOnlyList<ClassificationCategory> _categories;

    public ContentClassifier(IReadOnlyList<ClassificationCategory> categories) =>
        _categories = categories;

    public IReadOnlyList<ClassificationCategory> Categories => _categories;

    public static string DefaultPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "classification.json");

    /// <summary>Load categories from config/classification.json. Invalid patterns
    /// are skipped with the offending category still usable for its valid ones.</summary>
    public static ContentClassifier Load(string? path = null)
    {
        var json = File.ReadAllText(path ?? DefaultPath);
        return FromJson(json);
    }

    internal static ContentClassifier FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var categories = new List<ClassificationCategory>();
        if (doc.RootElement.TryGetProperty("categories", out var cats))
        {
            foreach (var cat in cats.EnumerateArray())
            {
                var name = cat.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var luhn = cat.TryGetProperty("luhn", out var l) && l.ValueKind == JsonValueKind.True;
                var patterns = new List<(string, Regex)>();
                if (cat.TryGetProperty("patterns", out var pats))
                {
                    foreach (var p in pats.EnumerateArray())
                    {
                        var pName = p.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                        var regex = p.TryGetProperty("regex", out var pr) ? pr.GetString() : null;
                        if (string.IsNullOrWhiteSpace(regex))
                            continue;
                        try
                        {
                            patterns.Add((pName ?? "pattern", new Regex(regex, Options)));
                        }
                        catch (ArgumentException)
                        {
                            // Skip an invalid regex; never crash the connector on config.
                        }
                    }
                }
                if (patterns.Count > 0)
                    categories.Add(new ClassificationCategory { Name = name!, Luhn = luhn, Patterns = patterns });
            }
        }
        return new ContentClassifier(categories);
    }

    /// <summary>Return the set of category names detected in <paramref name="text"/>.
    /// A Luhn category only matches when a candidate digit run passes the Luhn
    /// checksum. Empty/whitespace text detects nothing. Never throws.</summary>
    public IReadOnlySet<string> Detect(string? text)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return found;
        var scan = text.Length > MaxScanChars ? text[..MaxScanChars] : text;

        foreach (var category in _categories)
        {
            foreach (var (_, pattern) in category.Patterns)
            {
                if (!category.Luhn)
                {
                    if (pattern.IsMatch(scan))
                    {
                        found.Add(category.Name);
                        break;  // one hit is enough for the category
                    }
                    continue;
                }

                // Luhn category: a regex hit is only a candidate; validate it.
                var matched = false;
                foreach (Match m in pattern.Matches(scan))
                {
                    if (IsLuhnValid(m.Value))
                    {
                        matched = true;
                        break;
                    }
                }
                if (matched)
                {
                    found.Add(category.Name);
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>Luhn checksum over the digits of <paramref name="candidate"/>
    /// (spaces/dashes ignored). Requires 13–19 digits (real card lengths).</summary>
    internal static bool IsLuhnValid(string candidate)
    {
        var digits = new List<int>(candidate.Length);
        foreach (var ch in candidate)
        {
            if (char.IsDigit(ch))
                digits.Add(ch - '0');
            else if (ch is not (' ' or '-'))
                return false;  // an unexpected char breaks the candidate
        }
        if (digits.Count is < 13 or > 19)
            return false;

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Count - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                    d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }
}
