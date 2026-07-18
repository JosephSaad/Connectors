// Altrata/ItemTransformer.cs
// --------------------------
// FeedRecord → Microsoft Graph externalItem.
//
//   * item id       : {dataset}-{recordId} (Graph-safe characters only)
//   * ACL           : built exclusively from the licensed seats (SeatAclBuilder)
//   * properties    : mapped from well-known fields + dataset + PII label +
//                     the linked CRM contact id from entity resolution
//   * content       : compact human-readable text for semantic indexing

using System.Security.Cryptography;
using System.Text;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;

namespace AltrataConnector.Altrata;

public sealed class TransformException : Exception
{
    public TransformException(string message) : base(message) { }
}

/// <summary>Unified data-classification options (default off = no extra props).</summary>
public sealed record ClassificationOptions
{
    /// <summary>CLASSIFICATION — stamp sensitivityLabel/detectedCategories/dataResidency.</summary>
    public bool Enabled { get; init; }

    /// <summary>DATA_RESIDENCY — governance residency tag, e.g. "US" / "EU".</summary>
    public string? Residency { get; init; }
}

public sealed class ItemTransformer
{
    private readonly EntityResolver? _resolver;
    private readonly RelationshipPathIndex? _pathIndex;
    private readonly ClassificationOptions _classification;

    public ItemTransformer(EntityResolver? resolver = null, RelationshipPathIndex? pathIndex = null,
        ClassificationOptions? classification = null)
    {
        _resolver = resolver;
        _pathIndex = pathIndex;
        _classification = classification ?? new ClassificationOptions();
    }

    /// <summary>Property keys the classification feature adds (used by the manifest reader).</summary>
    public const string SensitivityLabelProp = "sensitivityLabel";
    public const string DetectedCategoriesProp = "detectedCategories";
    public const string DataResidencyProp = "dataResidency";

    /// <summary>
    /// The subject person id(s) a record concerns — the reverse-index key for
    /// per-subject erasure (DSAR). PersonProfile is its own subject; the derived
    /// person datasets carry the person via person_id; a relationship path
    /// concerns BOTH endpoints. Organization has no personal subject.
    /// </summary>
    public static IReadOnlyList<string> SubjectIds(FeedRecord record)
    {
        switch (record.Dataset)
        {
            case Datasets.PersonProfile:
                return record.Id != null ? new[] { record.Id } : Array.Empty<string>();
            case Datasets.WealthIndicator:
            case Datasets.BoardMembership:
            case Datasets.CareerHistory:
                var person = record.Get("person_id");
                return person != null ? new[] { person } : Array.Empty<string>();
            case Datasets.RelationshipPath:
                var subjects = new List<string>(2);
                var from = record.Get("from_person_id") ?? record.Get("from_person");
                var to = record.Get("to_person_id") ?? record.Get("to_person");
                if (from != null) subjects.Add(from);
                if (to != null && to != from) subjects.Add(to);
                return subjects;
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>Domain separator between the sanitized body and the hash of a
    /// sanitized item id. MUST be a character the sanitizer folds away (i.e. NOT
    /// in the Graph-safe set letters/digits/'-'), so it can appear in NO other
    /// position of any produced id — see <see cref="BuildItemId"/>.</summary>
    internal const char HashSeparator = '_';

    /// <summary>
    /// Graph-safe, collision-free item id. Record ids that are already
    /// Graph-safe (ASCII alphanumeric plus '-') keep the legacy
    /// `{dataset}-{id}` shape unchanged. Ids containing ANY other character
    /// are sanitized (char → '-') AND suffixed with a short stable SHA-256 of
    /// the raw id, so two distinct raw ids can never fold onto the same item
    /// id (e.g. 'acct:12/3' vs 'acct-12-3' — sanitization alone is not
    /// injective, and a collision would let one subject's PUT overwrite
    /// another's item or a DSAR tombstone mis-target). Deterministic: erasure
    /// recomputes the same id from the same raw id.
    ///
    /// The pass-through alphabet is STRICTLY ASCII [A-Za-z0-9-] (round-2
    /// hardening): unicode letters/digits — precomposed accents (NFC "é"),
    /// homoglyphs (Cyrillic 'а', U+212A KELVIN SIGN), non-ASCII digit systems
    /// (Arabic-Indic '١') — previously passed `char.IsLetterOrDigit` straight
    /// into the id, producing non-Graph-safe ids whose identity depended on the
    /// producer's unicode normalization form and on any case-folding /
    /// normalizing layer downstream. Now every such id is sanitized + hashed:
    /// the id is a pure function of the raw id's exact UTF-8 bytes (no
    /// normalization anywhere), so NFC/NFD spellings, zero-width/RTL controls
    /// and combining sequences each map to their own stable, ASCII-only id.
    ///
    /// Injectivity proof sketch: the sanitized (hashed) form and the legacy
    /// pass-through form share the same [A-Za-z0-9-] alphabet, so a hash
    /// appended with '-' could be forged by a graph-safe raw id crafted to equal
    /// '{sanitizedBody}-{hash}' — a distinct raw id folding onto the same item
    /// id (a real, zero-work collision). The hash is therefore joined with
    /// <see cref="HashSeparator"/> ('_'), which the sanitizer folds to '-' on
    /// input: a graph-safe (pass-through) id can never contain '_', the sanitized
    /// body never contains '_', and the hex hash never contains '_'. Hence a
    /// hashed id contains EXACTLY ONE '_' and a pass-through id contains NONE —
    /// the two output spaces are provably disjoint.
    /// </summary>
    public static string BuildItemId(string dataset, string recordId)
    {
        var raw = $"{dataset}-{recordId}";
        var sanitized = false;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('-');
                sanitized = true;
            }
        }
        if (!sanitized)
            return sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordId)))
            .ToLowerInvariant()[..12];
        return $"{sb}{HashSeparator}{hash}";
    }

    public ExternalItem Transform(FeedRecord record, IReadOnlyList<AclEntry> acl)
    {
        SeatAclBuilder.AssertNeverEveryone(acl);

        var recordId = record.Id
            ?? throw new TransformException(
                $"{record.Dataset} record has no id field (looked for id/altrata_id/person_id/org_id/relationship_id)");

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["altrataId"] = recordId,
            ["dataset"] = record.Dataset,
            ["title"] = BuildTitle(record, recordId),
            ["piiClassification"] = PiiClassifier.ClassifyLabel(record),
        };

        CopyIfPresent(record, properties, "personName", "person_name", "name", "full_name");
        CopyIfPresent(record, properties, "organizationName", "organization_name", "org_name", "organization", "employer");
        CopyIfPresent(record, properties, "roleTitle", "role_title", "role", "title", "position");
        CopyIfPresent(record, properties, "country", "country");
        CopyIfPresent(record, properties, "netWorthUsd", "net_worth_usd", "net_worth", "estimated_net_worth");
        CopyIfPresent(record, properties, "sourceUpdated", "updated_utc", "last_updated", "as_of_date");

        // Entity resolution: deterministic link to the internal CRM contact,
        // with the opt-in fuzzy tier beneath it. Provenance (rule + confidence)
        // is stamped on the item.
        if (_resolver != null && record.Dataset == Datasets.PersonProfile)
        {
            var match = _resolver.Resolve(
                recordId,
                record.Get("email"),
                record.Get("person_name") ?? record.Get("name") ?? record.Get("full_name"),
                record.Get("employer") ?? record.Get("organization_name") ?? record.Get("org_name"),
                role: record.Get("role") ?? record.Get("role_title") ?? record.Get("title")
                      ?? record.Get("position"));
            if (match != null)
            {
                properties["crmContactId"] = match.CrmContactId;
                properties["crmMatchRule"] = match.MatchRule;
                properties["crmMatchConfidence"] = match.Confidence.ToString(
                    "0.00", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // Relationship-path materialization: bounded per-person summary joined
        // from the precomputed index. Path properties are metadata only — they
        // never touch the ACL (asserted seat-only above and re-checked below).
        if (_pathIndex != null && record.Dataset == Datasets.PersonProfile)
        {
            var summary = _pathIndex.Summarize(recordId);
            if (summary != null)
            {
                properties["firstDegreeCount"] = summary.FirstDegreeCount;
                properties["secondDegreeCount"] = summary.SecondDegreeCount;
                properties["pathCount"] = summary.PathCount;
                if (summary.TopConnectedOrgs.Count > 0)
                    properties["topConnectedOrgs"] = summary.TopConnectedOrgs.ToList();
                properties["strongestPathSummary"] = summary.StrongestPathSummary;
            }
        }

        // Unified data classification & sensitivity labeling (opt-in). Folds the
        // existing PII label in as one input; only LABELS/categories are stamped
        // (never the raw detected values — those already live on the seat-gated
        // item, and must not leak to spans/logs/manifests).
        if (_classification.Enabled)
        {
            var label = SensitivityClassifier.ClassifyLabel(record);
            var categories = SensitivityClassifier.DetectCategories(record);
            properties[SensitivityLabelProp] = label;
            if (categories.Count > 0)
                properties[DetectedCategoriesProp] = categories.ToList();
            if (!string.IsNullOrWhiteSpace(_classification.Residency))
                properties[DataResidencyProp] = _classification.Residency;
            ClassificationStats.RecordItem(label, categories);
        }

        // Defence in depth: classification/path metadata must never widen visibility.
        SeatAclBuilder.AssertNeverEveryone(acl);

        return new ExternalItem
        {
            Id = BuildItemId(record.Dataset, recordId),
            Acl = acl,
            Properties = properties,
            Content = new ExternalItemContent { Type = "text", Value = BuildContent(record) },
        };
    }

    private static void CopyIfPresent(FeedRecord record, Dictionary<string, object?> properties,
        string propertyName, params string[] sourceFields)
    {
        foreach (var field in sourceFields)
        {
            var value = record.Get(field);
            if (value != null)
            {
                properties[propertyName] = value;
                return;
            }
        }
    }

    internal static string BuildTitle(FeedRecord record, string recordId)
    {
        var name = record.Get("person_name") ?? record.Get("name") ?? record.Get("full_name")
                   ?? record.Get("organization_name") ?? record.Get("org_name");
        return record.Dataset switch
        {
            Datasets.PersonProfile => name ?? $"Person {recordId}",
            Datasets.Organization => name ?? $"Organization {recordId}",
            Datasets.BoardMembership =>
                $"{name ?? recordId} — {record.Get("role") ?? record.Get("role_title") ?? "Board role"} at {record.Get("organization_name") ?? record.Get("org_name") ?? "organization"}",
            Datasets.RelationshipPath =>
                $"Relationship path {record.Get("from_person_name") ?? record.Get("from_person_id") ?? "?"} → {record.Get("to_person_name") ?? record.Get("to_person_id") ?? "?"}",
            Datasets.WealthIndicator => $"Wealth profile — {name ?? recordId}",
            Datasets.CareerHistory => $"Career history — {name ?? recordId}",
            _ => name ?? recordId,
        };
    }

    internal static string BuildContent(FeedRecord record)
    {
        var sb = new StringBuilder();
        foreach (var (field, value) in record.Fields.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            sb.Append(field.Replace('_', ' ')).Append(": ").Append(value).Append('\n');
        }
        return sb.ToString();
    }
}
