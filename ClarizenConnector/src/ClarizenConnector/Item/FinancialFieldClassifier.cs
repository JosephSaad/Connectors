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
    /// Apply the configured financial-data policy to <paramref name="item"/>
    /// in place. Returns true when the item was classified as financial.
    /// </summary>
    public static bool Apply(ExternalItem item, ObjectConfig objectConfig, AppConfig config)
    {
        if (!ContainsFinancialData(item, objectConfig))
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
