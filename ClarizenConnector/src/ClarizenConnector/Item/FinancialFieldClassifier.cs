// Item/FinancialFieldClassifier.cs
// --------------------------------
// Financial-field classification. Fields configured as financial in
// config/schema.json (budget, cost, rates, actuals, revenue) get special
// handling controlled by FINANCIAL_DATA_MODE:
//
//   tag    (default) — financial values are ingested normally; the item gets
//            classification properties (ContainsFinancialData=true,
//            DataClassification="financial") so search verticals / DLP can
//            key off them.
//   filter — financial property VALUES are stripped from the item before
//            ingestion (classification properties still set), so no reader
//            sees figures through Copilot regardless of ACL.
//   acl    — financial values are ingested but the item's grants are replaced
//            with a single grant to FINANCIAL_DATA_GROUP_ID (denies are
//            preserved), restricting the whole item to the finance group.
//
// Items with no populated financial fields are untouched in every mode.
//
// Financial fields routed to the searchable CONTENT body (selectedFields value
// "_cz_*") are governed too: the converter redacts their values from the
// content in filter mode, and a populated content-financial field classifies
// (and, in acl mode, restricts) the item exactly like a property would.

using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Item;

public static class FinancialFieldClassifier
{
    public const string ClassificationProperty = "DataClassification";
    public const string ContainsFinancialProperty = "ContainsFinancialData";
    public const string FinancialLabel = "financial";

    /// <summary>Graph property names of an object's financial fields.</summary>
    public static HashSet<string> FinancialPropertyNames(ObjectConfig objectConfig)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in objectConfig.FinancialFields)
        {
            if (objectConfig.SelectedFields.TryGetValue(field, out var property)
                && !property.StartsWith("_cz_", StringComparison.Ordinal))
            {
                names.Add(property);
            }
        }
        return names;
    }

    /// <summary>True when the item has at least one populated financial property.</summary>
    public static bool ContainsFinancialData(ExternalItem item, ObjectConfig objectConfig)
    {
        var financial = FinancialPropertyNames(objectConfig);
        return financial.Count > 0
            && item.Properties.Any(kv => financial.Contains(kv.Key) && kv.Value is not null);
    }

    /// <summary>
    /// Clarizen field names of financial fields routed to the searchable
    /// CONTENT body (mapped to a _cz_* placeholder) rather than to Graph
    /// properties. Their values never appear as properties, so property-based
    /// stripping cannot see them — the converter redacts them from the content
    /// body in filter mode, and the item is still classified/enforced.
    /// </summary>
    public static List<string> ContentFinancialFields(ObjectConfig objectConfig)
    {
        var fields = new List<string>();
        foreach (var field in objectConfig.FinancialFields)
        {
            if (objectConfig.SelectedFields.TryGetValue(field, out var property)
                && property.StartsWith("_cz_", StringComparison.Ordinal))
            {
                fields.Add(field);
            }
        }
        return fields;
    }

    /// <summary>True when the record carries a populated financial field that is
    /// routed to the content body (so the finished item contains financial data
    /// even though no financial PROPERTY is populated).</summary>
    public static bool RecordHasContentFinancial(ClarizenRecord record, ObjectConfig objectConfig)
    {
        foreach (var field in ContentFinancialFields(objectConfig))
        {
            if (!string.IsNullOrWhiteSpace(record.GetString(field)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Apply the configured financial-data policy to <paramref name="item"/>
    /// in place. Returns true when the item was classified as financial.
    /// </summary>
    public static bool Apply(ExternalItem item, ObjectConfig objectConfig, AppConfig config) =>
        Apply(item, objectConfig, config, contentFinancial: false);

    /// <summary>
    /// Apply the policy, additionally honouring a financial signal carried in
    /// the CONTENT body (<paramref name="contentFinancial"/>, computed by the
    /// converter from the source record): such an item is classified and — in
    /// acl mode — restricted even though no financial property is populated.
    /// (In filter mode the converter has already redacted the content value.)
    /// </summary>
    public static bool Apply(
        ExternalItem item, ObjectConfig objectConfig, AppConfig config, bool contentFinancial)
    {
        if (!ContainsFinancialData(item, objectConfig) && !contentFinancial)
            return false;

        item.Properties[ContainsFinancialProperty] = true;
        item.Properties[ClassificationProperty] = FinancialLabel;

        switch (config.FinancialDataMode)
        {
            case "filter":
                foreach (var property in FinancialPropertyNames(objectConfig))
                    item.Properties.Remove(property);
                break;

            case "acl":
                // Replace grants with the finance group; preserve denies.
                var denies = item.Acl.Where(e => e.AccessType == AclAccessType.Deny).ToList();
                item.Acl.Clear();
                item.Acl.Add(new AclEntry(
                    AclEntryType.Group, config.FinancialDataGroupId!, AclAccessType.Grant));
                item.Acl.AddRange(denies);
                break;

            case "tag":
            default:
                break;  // classification properties only
        }
        return true;
    }
}
