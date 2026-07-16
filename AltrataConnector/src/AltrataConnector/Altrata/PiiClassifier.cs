// Altrata/PiiClassifier.cs
// ------------------------
// Personal-data classification. Every externalItem is tagged with the HIGHEST
// classification label that applies to its dataset and fields, surfaced as the
// `piiClassification` property so Search/Purview policies can act on it.
//
//   None      < Personal < Sensitive
//   None:      organization-level data with no personal fields
//   Personal:  identified-person data (profiles, careers, board roles, paths)
//   Sensitive: wealth / net-worth / income indicators tied to a person

namespace AltrataConnector.Altrata;

public enum PiiLevel
{
    None = 0,
    Personal = 1,
    Sensitive = 2,
}

public static class PiiClassifier
{
    public static string Label(PiiLevel level) => level switch
    {
        PiiLevel.Sensitive => "PII-Sensitive-Wealth",
        PiiLevel.Personal => "PII-Personal",
        _ => "Non-Personal",
    };

    /// <summary>Baseline level per dataset.</summary>
    public static PiiLevel DatasetLevel(string dataset) => dataset switch
    {
        Datasets.WealthIndicator => PiiLevel.Sensitive,
        Datasets.Organization => PiiLevel.None,
        _ => PiiLevel.Personal,  // PersonProfile, BoardMembership, RelationshipPath, CareerHistory
    };

    /// <summary>Field names that escalate the classification when present.</summary>
    private static readonly string[] SensitiveFieldFragments =
    {
        "net_worth", "networth", "wealth", "income", "assets", "liquidity", "donation_capacity",
    };

    private static readonly string[] PersonalFieldFragments =
    {
        "person_name", "full_name", "first_name", "last_name",
        "email", "birth", "phone", "home_address", "person_id",
    };

    /// <summary>
    /// Classification for a record: the dataset baseline escalated by any
    /// sensitive/personal fields that carry a value.
    /// </summary>
    public static PiiLevel Classify(FeedRecord record)
    {
        var level = DatasetLevel(record.Dataset);
        foreach (var (field, value) in record.Fields)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            var lower = field.ToLowerInvariant();
            if (SensitiveFieldFragments.Any(lower.Contains))
                return PiiLevel.Sensitive;  // already the maximum
            if (level < PiiLevel.Personal && PersonalFieldFragments.Any(lower.Contains))
                level = PiiLevel.Personal;
        }
        return level;
    }

    public static string ClassifyLabel(FeedRecord record) => Label(Classify(record));
}
