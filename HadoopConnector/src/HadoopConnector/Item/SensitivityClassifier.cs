// Item/SensitivityClassifier.cs
// -----------------------------
// Connector-applied data classification TAG (docs/CLASSIFICATION.md). This is an
// ADVISORY classification the connector computes and stamps as Graph refiner
// properties — it is NOT a Microsoft Purview-enforced sensitivity label: on its
// own it does not encrypt content or gate access (see CLASSIFICATION_ENFORCE_ACL
// in Graph/Ingest.cs for the optional, opt-in ACL enforcement). The wire property
// name stays "SensitivityLabel" for schema back-compat.
//
// Derives, for every externalItem, a single taxonomy:
//
//   SensitivityLabel ∈ { Public, Internal, Confidential, Restricted }  (tag)
//   DetectedCategories : string collection (PII, PCI, Secret, Financial, ...)
//
// from two inputs, in precedence order (highest wins):
//
//   1. Detected PII / PCI / Secret (content scan)  ⇒ Restricted
//   2. Per-object default (schema.json)            ⇒ baseline (default Internal)
//
// Gated by CLASSIFICATION (default off) — when off the classifier is never
// constructed and no properties are added, so default behaviour is unchanged.

using System.Text;
using HadoopConnector.Config;
using HadoopConnector.Content;
using HadoopConnector.Graph;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Item;

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

        var label = DeriveLabel(objectConfig.SensitivityDefault, detected);

        item.Properties[LabelProperty] = label.ToString();
        item.Properties[CategoriesProperty] = detected.ToArray();

        Metrics.IncItemsClassified(label.ToString());
        foreach (var category in detected)
            Metrics.IncSensitiveDetection(category);

        return new ClassificationOutcome(label, detected.ToArray());
    }

    /// <summary>Precedence: Restricted (PII/PCI/Secret) &gt; per-object default.
    /// Pure/testable.</summary>
    internal static SensitivityLabel DeriveLabel(
        string? objectDefault, IReadOnlySet<string> detected)
    {
        var label = ParseLabel(objectDefault) ?? SensitivityLabel.Internal;
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
