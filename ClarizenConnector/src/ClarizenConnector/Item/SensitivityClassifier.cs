// Item/SensitivityClassifier.cs
// -----------------------------
// Unified data-classification & sensitivity tagging (docs/CLASSIFICATION.md).
//
// IMPORTANT: the "advisorySensitivity" tag this derives is a connector-applied,
// ADVISORY classification TAG stamped as a Graph external-item property. It is
// NOT a Microsoft Purview / MIP sensitivity label and is NOT enforced by
// Purview, DLP, or encryption. By itself it only informs search verticals and
// downstream tooling. To make the top tier actually restrictive, turn on
// CLASSIFICATION_ENFORCE_ACL (see EnforceRestrictedAcl) — that locks Restricted
// items to a configured Entra group at the connector's own ACL layer. The Graph
// The WIRE PROPERTY is "advisorySensitivity" -- renamed from "SensitivityLabel",
// which an auditor reads as a Microsoft Purview label and which this is not.
// The C# enum below keeps its name: it is internal and reads correctly in code.
// Seismic and Altrata made the same split in #41.
//
// Derives, for every externalItem, a single taxonomy:
//
//   SensitivityLabel ∈ { Public, Internal, Confidential, Restricted }
//   DetectedCategories : string collection (PII, PCI, Secret, Financial, ...)
//
// from three inputs, in precedence order (highest wins):
//
//   1. Detected PII / PCI / Secret (content scan)  ⇒ Restricted
//   2. Financial-field classification (folded in)  ⇒ at least Confidential
//   3. Per-object default (schema.json)            ⇒ baseline (default Internal)
//
// The financial signal is NOT re-detected here — it reuses the result the
// FinancialFieldClassifier already stamped (ContainsFinancialData), so the two
// schemes don't duplicate. FINANCIAL_DATA_MODE's filter/acl ENFORCEMENT stays
// in the financial classifier; this class only folds financial into the label.
//
// Gated by CLASSIFICATION (default off) — when off the classifier is never
// constructed and no properties are added, so default behaviour is unchanged.

using System.Text;
using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Item;

/// <summary>Ordered sensitivity taxonomy — higher ordinal = more sensitive.</summary>
public enum SensitivityLabel
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Restricted = 3,
}

public readonly record struct ClassificationOutcome(
    SensitivityLabel Label, IReadOnlyList<string> Categories);

public sealed class SensitivityClassifier
{
    public const string LabelProperty = "advisorySensitivity";
    public const string CategoriesProperty = "DetectedCategories";
    public const string FinancialCategory = "Financial";

    /// <summary>Categories that force the top (Restricted) label.</summary>
    internal static readonly HashSet<string> RestrictedCategories =
        new(StringComparer.Ordinal) { "PII", "PCI", "Secret" };

    private readonly ContentClassifier _content;

    public SensitivityClassifier(ContentClassifier content) => _content = content;

    /// <summary>
    /// Classify <paramref name="item"/> in place: scan its text, fold in the
    /// financial signal and the per-object default, set the two taxonomy
    /// properties, and record metrics. Returns the outcome (for the manifest).
    /// </summary>
    public ClassificationOutcome Classify(ExternalItem item, ObjectConfig objectConfig)
    {
        var detected = new SortedSet<string>(_content.Detect(ScanText(item)), StringComparer.Ordinal);

        // Financial fold-in: reuse the already-stamped signal, don't re-detect.
        var isFinancial = item.Properties.TryGetValue(
                FinancialFieldClassifier.ContainsFinancialProperty, out var fin)
            && fin is true;
        if (isFinancial)
            detected.Add(FinancialCategory);

        var label = DeriveLabel(objectConfig.SensitivityDefault, detected, isFinancial);

        item.Properties[LabelProperty] = label.ToString();
        item.Properties[CategoriesProperty] = detected.ToArray();

        Metrics.IncItemsClassified(label.ToString());
        foreach (var category in detected)
            Metrics.IncSensitiveDetection(category);

        return new ClassificationOutcome(label, detected.ToArray());
    }

    /// <summary>
    /// Optional enforcement (CLASSIFICATION_ENFORCE_ACL): when on and a group is
    /// configured, a top-tier (Restricted) item has its ACL grants replaced with
    /// a single grant to <see cref="AppConfig.ClassificationRestrictedGroupId"/>,
    /// preserving explicit denies — turning the advisory tag into a real ACL
    /// restriction at the connector layer. Returns true when it restricted the
    /// item. No-op (returns false) when enforcement is off, the label is below
    /// Restricted, or no group is configured. Non-Restricted items are untouched.
    /// </summary>
    public static bool EnforceRestrictedAcl(ExternalItem item, SensitivityLabel label, AppConfig config)
    {
        if (!config.ClassificationEnforceAcl
            || label != SensitivityLabel.Restricted
            || string.IsNullOrWhiteSpace(config.ClassificationRestrictedGroupId))
        {
            return false;
        }

        var denies = item.Acl.Where(e => e.AccessType == AclAccessType.Deny).ToList();
        item.Acl.Clear();
        item.Acl.Add(new AclEntry(
            AclEntryType.Group, config.ClassificationRestrictedGroupId!, AclAccessType.Grant));
        item.Acl.AddRange(denies);
        return true;
    }

    /// <summary>Precedence: Restricted (PII/PCI/Secret) &gt; Confidential
    /// (Financial) &gt; per-object default. Pure/testable.</summary>
    internal static SensitivityLabel DeriveLabel(
        string? objectDefault, IReadOnlySet<string> detected, bool isFinancial)
    {
        var label = ParseLabel(objectDefault) ?? SensitivityLabel.Internal;
        if (isFinancial && label < SensitivityLabel.Confidential)
            label = SensitivityLabel.Confidential;
        if (detected.Any(RestrictedCategories.Contains))
            label = SensitivityLabel.Restricted;
        return label;
    }

    /// <summary>Parse a label name (case-insensitive); null when unset/unknown.</summary>
    internal static SensitivityLabel? ParseLabel(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : Enum.TryParse<SensitivityLabel>(raw.Trim(), ignoreCase: true, out var v) ? v : null;

    /// <summary>
    /// The text a scanner sees: content body + all string / string[] property
    /// values.
    /// <para>
    /// <paramref name="includeTaxonomyProperties"/> is the ONE place the two
    /// scanners legitimately differ, and it is a difference in what they are
    /// asking, not a drift:
    /// </para>
    /// <list type="bullet">
    /// <item>The CLASSIFIER (default, false) excludes <see cref="LabelProperty"/>
    /// and <see cref="CategoriesProperty"/> because those are its OWN outputs.
    /// Feeding them back in would let the string "PII" in DetectedCategories
    /// re-detect as PII on the next pass.</item>
    /// <item>The CONTENT GATE (true) includes them, because it is not asking
    /// "what did I derive?" but "what text reaches the index?". A schema can map
    /// a source field onto one of those reserved names, and the value then lands
    /// in Copilot grounding context like any other property — excluding it would
    /// be a gate bypass, which is exactly what it was.</item>
    /// </list>
    /// </summary>
    internal static string ScanText(ExternalItem item, bool includeTaxonomyProperties = false)
    {
        var sb = new StringBuilder(item.Content);
        foreach (var (name, value) in item.Properties)
        {
            if (!includeTaxonomyProperties && name is LabelProperty or CategoriesProperty)
                continue;
            switch (value)
            {
                case string s:
                    sb.Append('\n').Append(s);
                    break;
                case IEnumerable<string> list:
                    foreach (var v in list)
                        sb.Append('\n').Append(v);
                    break;
            }
        }
        return sb.ToString();
    }
}
