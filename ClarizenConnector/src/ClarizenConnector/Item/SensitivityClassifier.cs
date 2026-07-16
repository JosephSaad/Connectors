// Item/SensitivityClassifier.cs
// -----------------------------
// Unified data-classification & sensitivity labeling (docs/CLASSIFICATION.md).
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
    public const string LabelProperty = "SensitivityLabel";
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

    /// <summary>The text a classifier scans: content body + all string / string[]
    /// property values (excluding the taxonomy properties themselves).</summary>
    internal static string ScanText(ExternalItem item)
    {
        var sb = new StringBuilder(item.Content);
        foreach (var (name, value) in item.Properties)
        {
            if (name is LabelProperty or CategoriesProperty)
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
