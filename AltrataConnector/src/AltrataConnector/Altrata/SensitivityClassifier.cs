// Altrata/SensitivityClassifier.cs
// --------------------------------
// Data-classification & sensitivity TAGGING. HONESTY NOTE: the value this
// produces is a CONNECTOR-APPLIED CLASSIFICATION TAG — advisory metadata the
// connector stamps on the item. It is NOT a Microsoft Purview sensitivity LABEL
// and is NOT enforced by Purview; the wire property is named `advisorySensitivity`
// only for back-compat. It is searchable/refinable so admins can build
// Microsoft Search policies on it, and — only when CLASSIFICATION_ENFORCE_ACL is
// on — the connector itself hard-restricts the top tier's ACL (see #6b in
// ItemTransformer). Formalizes the existing PII classification (PiiClassifier)
// into ONE taxonomy stamped on every externalItem when CLASSIFICATION is enabled:
//
//   * advisorySensitivity  ∈ {Public, Internal, Confidential, Restricted}
//                          (advisory classification tag; see honesty note above)
//   * detectedCategories — the category LABELS a field classifier confirmed
//                          (Name / Email / NationalId / WealthFigure)
//
// Altrata is inherently high-sensitivity: PersonProfile / WealthIndicator /
// RelationshipPath / CareerHistory ⇒ Restricted; Organization / BoardMembership
// ⇒ Confidential. The existing PiiClassifier level is folded in as ONE input
// (a floor), never duplicated.
//
// PII CAUTION (enforced by tests): DetectCategories returns category LABELS
// ONLY — never the raw detected values. Categories, labels, ids, counts and
// hashes are the only things allowed onto spans, logs and manifests (the same
// allowlist discipline as the tracing tags).

using System.Globalization;
using System.Text.RegularExpressions;

namespace AltrataConnector.Altrata;

/// <summary>Governance sensitivity taxonomy (ascending sensitivity).</summary>
public enum SensitivityLabel
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Restricted = 3,
}

/// <summary>Detected data-category labels (metadata only — never the values).</summary>
public static class DataCategories
{
    public const string Name = "Name";
    public const string Email = "Email";
    public const string NationalId = "NationalId";
    public const string WealthFigure = "WealthFigure";
}

public static class SensitivityClassifier
{
    // ---- taxonomy -----------------------------------------------------------

    public static string LabelName(SensitivityLabel label) => label.ToString();

    /// <summary>Dataset baseline label (Altrata's inherent sensitivity floor).</summary>
    public static SensitivityLabel DatasetBaseline(string dataset) => dataset switch
    {
        Datasets.PersonProfile => SensitivityLabel.Restricted,
        Datasets.WealthIndicator => SensitivityLabel.Restricted,
        Datasets.RelationshipPath => SensitivityLabel.Restricted,  // person-derived
        Datasets.CareerHistory => SensitivityLabel.Restricted,     // person-derived
        Datasets.BoardMembership => SensitivityLabel.Confidential,
        Datasets.Organization => SensitivityLabel.Confidential,
        _ => SensitivityLabel.Internal,
    };

    /// <summary>Fold the existing PII level in as a floor (one input, not a copy).</summary>
    public static SensitivityLabel FromPiiLevel(PiiLevel level) => level switch
    {
        PiiLevel.Sensitive => SensitivityLabel.Restricted,
        PiiLevel.Personal => SensitivityLabel.Confidential,
        _ => SensitivityLabel.Internal,
    };

    /// <summary>
    /// Unified label for a record: the higher of the dataset baseline and the
    /// PII-classifier floor. So an Organization/Board record that actually
    /// carries a wealth figure escalates to Restricted, while ordinary board
    /// data stays Confidential.
    /// </summary>
    public static SensitivityLabel Classify(FeedRecord record)
    {
        var baseline = DatasetBaseline(record.Dataset);
        var piiFloor = FromPiiLevel(PiiClassifier.Classify(record));
        return (SensitivityLabel)Math.Max((int)baseline, (int)piiFloor);
    }

    public static string ClassifyLabel(FeedRecord record) => LabelName(Classify(record));

    // ---- field classifier (dependency-free; LABELS only, never values) ------

    private static readonly Regex EmailValue =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    // US SSN 123-45-6789, or a bare 9-digit id (NINO/passport-ish shapes are
    // name-driven since their value formats vary widely).
    private static readonly Regex NationalIdValue =
        new(@"^(\d{3}-\d{2}-\d{4}|\d{9})$", RegexOptions.Compiled);

    // Person-name fragments only — deliberately NOT bare "name", so
    // "org_name"/"organization_name" are not misread as a personal name.
    private static readonly string[] NameFields =
        { "person_name", "full_name", "first_name", "last_name", "given_name", "surname" };
    private static readonly string[] EmailFields = { "email", "e_mail", "mail" };
    private static readonly string[] NationalIdFields =
        { "ssn", "national_id", "nationalid", "nino", "tax_id", "taxid", "passport" };
    private static readonly string[] WealthFields =
        { "net_worth", "networth", "wealth", "income", "assets", "liquidity", "donation_capacity" };

    /// <summary>
    /// Confirm the data categories present in a record from field names + value
    /// shapes. Returns the category LABELS only — the raw values never leave the
    /// record. Deterministic and side-effect free.
    /// </summary>
    public static IReadOnlyList<string> DetectCategories(FeedRecord record)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (field, value) in record.Fields)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            var name = field.ToLowerInvariant();
            var trimmed = value.Trim();

            if (Contains(name, NationalIdFields) || NationalIdValue.IsMatch(trimmed))
                found.Add(DataCategories.NationalId);
            else if (Contains(name, EmailFields) || EmailValue.IsMatch(trimmed))
                found.Add(DataCategories.Email);
            if (Contains(name, WealthFields) || LooksLikeWealthFigure(name, trimmed))
                found.Add(DataCategories.WealthFigure);
            // Person name: specific person-name fragments, or the exact field
            // "name" (never "org_name"/"organization_name").
            if (name == "name" || Contains(name, NameFields))
                found.Add(DataCategories.Name);
        }
        return found.ToList();
    }

    private static bool Contains(string fieldName, string[] fragments) =>
        fragments.Any(fieldName.Contains);

    private static bool LooksLikeWealthFigure(string fieldName, string value) =>
        (fieldName.Contains("usd") || fieldName.Contains("amount") || fieldName.Contains("value"))
        && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
        && n >= 1000;
}
