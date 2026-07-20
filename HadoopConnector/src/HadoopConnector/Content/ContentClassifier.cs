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
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Content;

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

    /// <summary>Bounded regex match time — config patterns run against
    /// attacker-influenced text (fields + attachment content), so a
    /// catastrophic-backtracking pattern must time out, not hang the crawl.
    /// (Same guard as the Seismic connector's classifier.)</summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.classifier");

    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private readonly IReadOnlyList<ClassificationCategory> _categories;

    public ContentClassifier(IReadOnlyList<ClassificationCategory> categories) =>
        _categories = categories;

    public IReadOnlyList<ClassificationCategory> Categories => _categories;

    public static string DefaultPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "classification.json");

    /// <summary>Load categories from config/classification.json. Invalid patterns
    /// are skipped with the offending category still usable for its valid ones.
    /// <para>
    /// Throws <see cref="InvalidDataException"/> — never a raw
    /// <c>InvalidOperationException</c>/<c>JsonException</c> — for a file this
    /// loader cannot honour, naming the file and the offending JSON path, so
    /// <c>validate-config</c> can report it in preflight instead of the crawl
    /// dying on a stack that names neither.
    /// </para></summary>
    public static ContentClassifier Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Classification config '{file}' could not be read: {exc.Message}. This file is "
                + "required whenever CLASSIFICATION=true.");
        }
        return FromJson(json, file);
    }

    internal static ContentClassifier FromJson(string json, string path = "classification.json")
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException exc)
        {
            throw new InvalidDataException(
                $"Classification config '{path}' could not be read"
                + (string.IsNullOrEmpty(exc.Path) ? string.Empty : $" at {exc.Path}")
                + $": {exc.Message}",
                exc);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw WrongType(path, "$", "an object with a \"categories\" array",
                    doc.RootElement.ValueKind);
            }

            var categories = new List<ClassificationCategory>();
            // Absent, or an explicit null: the house null-is-empty semantics
            // (docs/CONFIG_NULL_SEMANTICS.md) — no categories. Whether an EMPTY
            // category set is acceptable is then judged exactly where schema.json's
            // empty objectList is judged: by the validator, not by a crash here.
            if (!doc.RootElement.TryGetProperty("categories", out var cats)
                || cats.ValueKind == JsonValueKind.Null)
            {
                return new ContentClassifier(categories);
            }
            if (cats.ValueKind != JsonValueKind.Array)
                throw WrongType(path, "$.categories", "an array of category objects", cats.ValueKind);

            var index = -1;
            foreach (var cat in cats.EnumerateArray())
            {
                index++;
                var catPath = $"$.categories[{index}]";
                // A null ENTRY of a list of objects has no meaningful empty form —
                // same rule, and the same reasoning, as ConfigNullNormalizer's.
                if (cat.ValueKind == JsonValueKind.Null)
                    throw NullElement(path, catPath);
                if (cat.ValueKind != JsonValueKind.Object)
                    throw WrongType(path, catPath, "a category object", cat.ValueKind);

                var name = StringOrNull(path, catPath, "name", cat);
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var luhn = cat.TryGetProperty("luhn", out var l) && l.ValueKind == JsonValueKind.True;
                var patterns = new List<(string, Regex)>();
                if (cat.TryGetProperty("patterns", out var pats)
                    && pats.ValueKind != JsonValueKind.Null)
                {
                    if (pats.ValueKind != JsonValueKind.Array)
                    {
                        throw WrongType(path, $"{catPath}.patterns",
                            "an array of pattern objects", pats.ValueKind);
                    }
                    var pIndex = -1;
                    foreach (var p in pats.EnumerateArray())
                    {
                        pIndex++;
                        var pPath = $"{catPath}.patterns[{pIndex}]";
                        if (p.ValueKind == JsonValueKind.Null)
                            throw NullElement(path, pPath);
                        if (p.ValueKind != JsonValueKind.Object)
                            throw WrongType(path, pPath, "a pattern object", p.ValueKind);

                        var pName = StringOrNull(path, pPath, "name", p);
                        var regex = StringOrNull(path, pPath, "regex", p);
                        if (string.IsNullOrWhiteSpace(regex))
                            continue;
                        try
                        {
                            patterns.Add((pName ?? "pattern", new Regex(regex, Options, MatchTimeout)));
                        }
                        catch (ArgumentException exc)
                        {
                            // Skip an invalid regex; never crash the connector on
                            // config — but a skipped pattern is a silent DETECTION
                            // GAP (items that should classify Restricted no longer
                            // do), so name exactly which pattern was dropped.
                            Logger.Warning(
                                $"classification.json: category '{name}' pattern "
                                + $"'{pName ?? "pattern"}' is not a valid regex and was skipped "
                                + $"({exc.Message}) — detection coverage is reduced.");
                        }
                    }
                }
                if (patterns.Count > 0)
                    categories.Add(new ClassificationCategory { Name = name!, Luhn = luhn, Patterns = patterns });
            }
            return new ContentClassifier(categories);
        }
    }

    /// <summary>Read a string member. Absent or null reads as absent (null-is-empty);
    /// a member of any OTHER non-string kind is a load error naming the path,
    /// because silently ignoring it would drop a pattern the operator wrote.</summary>
    private static string? StringOrNull(string path, string jsonPath, string member, JsonElement owner)
    {
        if (!owner.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw WrongType(path, $"{jsonPath}.{member}", "a string", value.ValueKind);
        return value.GetString();
    }

    private static InvalidDataException WrongType(
        string path, string jsonPath, string expected, JsonValueKind actual) =>
        new($"Classification config '{path}': {jsonPath} must be {expected}, but it is "
            + $"{actual.ToString().ToLowerInvariant()}. Fix the file and re-run; "
            + "'validate-config --strict' reports every problem in one pass.");

    private static InvalidDataException NullElement(string path, string jsonPath) =>
        new($"Classification config '{path}': {jsonPath} is null. Elsewhere in this file a JSON "
            + "null is read as that key's EMPTY value, but a null entry in a list of objects has no "
            + "meaningful empty form. Remove the entry, or fill it in.");

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
                    if (SafeIsMatch(pattern, scan, category.Name))
                    {
                        found.Add(category.Name);
                        break;  // one hit is enough for the category
                    }
                    continue;
                }

                // Luhn category: a regex hit is only a candidate; validate it.
                if (HasLuhnValidMatch(pattern, scan, category.Name))
                {
                    found.Add(category.Name);
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>IsMatch with the timeout treated as "no match" — a pathological
    /// pattern/input pair logs a warning instead of hanging the crawl.</summary>
    private static bool SafeIsMatch(Regex pattern, string scan, string categoryName)
    {
        try
        {
            return pattern.IsMatch(scan);
        }
        catch (RegexMatchTimeoutException)
        {
            Logger.Warning(
                $"Classifier pattern for category '{categoryName}' timed out after "
                + $"{MatchTimeout.TotalSeconds:0.#}s — treated as no-match.");
            return false;
        }
    }

    /// <summary>Enumerate Luhn candidates with the same timeout guard.</summary>
    private static bool HasLuhnValidMatch(Regex pattern, string scan, string categoryName)
    {
        try
        {
            foreach (Match m in pattern.Matches(scan))
            {
                if (IsLuhnValid(m.Value))
                    return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            Logger.Warning(
                $"Classifier pattern for category '{categoryName}' timed out after "
                + $"{MatchTimeout.TotalSeconds:0.#}s — treated as no-match.");
        }
        return false;
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
