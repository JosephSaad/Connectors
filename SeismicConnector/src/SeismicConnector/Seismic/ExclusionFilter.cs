// Seismic/ExclusionFilter.cs
// --------------------------
// The "No MNE" ingest-time exclusion filter.
//
// Content carrying a material-nonpublic-event (MNE) classification — or living
// in a configured restricted library/teamsite — is NEVER ingested into the
// Graph connection. Rules are config-driven (config/exclusions.json) and
// evaluated for every item on every crawl:
//
//   * flag rules      — any configured flag property containing an excluded
//                       flag value (case-insensitive) excludes the item;
//   * library rules   — items in a restricted teamsite (by id or name) are
//                       excluded, as are teamsites marked isRestricted when
//                       excludeRestrictedTeamsites is true;
//   * property rules  — arbitrary name/equals pairs for bespoke taxonomies.
//
// Every exclusion is recorded in the crawl's reconciliation report so the skip
// list is auditable (docs/EXCLUSIONS.md). If an already-ingested item later
// gains an excluded flag, the ingest pipeline withdraws it on the next crawl —
// the filter itself is stateless; the pipeline compares the decision against
// the tracked-item store.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeismicConnector.Seismic;

/// <summary>A bespoke property exclusion rule: excluded when property <c>name</c> equals <c>equals</c>.</summary>
public sealed class PropertyRule
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("equals")]
    public required string ExpectedValue { get; init; }
}

/// <summary>Deserialized config/exclusions.json.</summary>
public sealed class ExclusionRules
{
    /// <summary>Flag values that exclude an item (e.g. <c>MNE</c>).</summary>
    [JsonPropertyName("excludedFlags")]
    public List<string> ExcludedFlags { get; init; } = new();

    /// <summary>Content property names inspected for excluded flags.</summary>
    [JsonPropertyName("flagProperties")]
    public List<string> FlagProperties { get; init; } = new();

    /// <summary>Restricted library/teamsite NAMES (case-insensitive).</summary>
    [JsonPropertyName("restrictedLibraries")]
    public List<string> RestrictedLibraries { get; init; } = new();

    /// <summary>Restricted teamsite IDS.</summary>
    [JsonPropertyName("restrictedTeamsiteIds")]
    public List<string> RestrictedTeamsiteIds { get; init; } = new();

    /// <summary>Also exclude any teamsite Seismic itself marks restricted. Default true.</summary>
    [JsonPropertyName("excludeRestrictedTeamsites")]
    public bool ExcludeRestrictedTeamsites { get; init; } = true;

    [JsonPropertyName("propertyRules")]
    public List<PropertyRule> PropertyRules { get; init; } = new();

    public static ExclusionRules LoadFile(string path)
    {
        if (!File.Exists(path))
            return new ExclusionRules();  // no rules file → nothing excluded
        return JsonSerializer.Deserialize<ExclusionRules>(File.ReadAllText(path)) ?? new ExclusionRules();
    }
}

/// <summary>The outcome of evaluating one item against the exclusion rules.</summary>
public readonly record struct ExclusionDecision(bool Excluded, string? Reason, string? Rule)
{
    public static readonly ExclusionDecision Allowed = new(false, null, null);

    public static ExclusionDecision Exclude(string reason, string rule) => new(true, reason, rule);
}

/// <summary>Evaluates the config-driven No-MNE rules. Stateless and thread-safe.</summary>
public sealed class ExclusionFilter
{
    private readonly ExclusionRules _rules;
    private readonly HashSet<string> _excludedFlags;
    private readonly HashSet<string> _restrictedLibraryNames;
    private readonly HashSet<string> _restrictedTeamsiteIds;

    public ExclusionFilter(ExclusionRules rules)
    {
        _rules = rules;
        _excludedFlags = new HashSet<string>(rules.ExcludedFlags, StringComparer.OrdinalIgnoreCase);
        _restrictedLibraryNames = new HashSet<string>(rules.RestrictedLibraries, StringComparer.OrdinalIgnoreCase);
        _restrictedTeamsiteIds = new HashSet<string>(rules.RestrictedTeamsiteIds, StringComparer.Ordinal);
    }

    /// <summary>True when an entire teamsite is off-limits (its items are never even listed).</summary>
    public bool IsTeamsiteExcluded(SeismicTeamsite teamsite)
    {
        if (_restrictedTeamsiteIds.Contains(teamsite.Id))
            return true;
        if (_restrictedLibraryNames.Contains(teamsite.Name))
            return true;
        if (_rules.ExcludeRestrictedTeamsites && teamsite.IsRestricted)
            return true;
        return false;
    }

    /// <summary>
    /// Evaluate one content item. <paramref name="teamsite"/> is optional — when
    /// provided, library-level rules are applied too (items fetched by id may
    /// not carry a resolved teamsite).
    /// </summary>
    public ExclusionDecision Evaluate(SeismicContent content, SeismicTeamsite? teamsite = null)
    {
        // 1. Library / teamsite rules.
        if (_restrictedTeamsiteIds.Contains(content.TeamsiteId))
        {
            return ExclusionDecision.Exclude(
                $"teamsite '{content.TeamsiteId}' is in restrictedTeamsiteIds", "restricted-teamsite-id");
        }
        if (teamsite is not null)
        {
            if (_restrictedLibraryNames.Contains(teamsite.Name))
            {
                return ExclusionDecision.Exclude(
                    $"library '{teamsite.Name}' is in restrictedLibraries", "restricted-library");
            }
            if (_rules.ExcludeRestrictedTeamsites && teamsite.IsRestricted)
            {
                return ExclusionDecision.Exclude(
                    $"teamsite '{teamsite.Name}' is flagged restricted in Seismic", "seismic-restricted-teamsite");
            }
        }

        // 2. MNE / classification flag rules.
        foreach (var propertyName in _rules.FlagProperties)
        {
            foreach (var value in content.PropertyValues(propertyName))
            {
                if (_excludedFlags.Contains(value.Trim()))
                {
                    return ExclusionDecision.Exclude(
                        $"property '{propertyName}' carries excluded flag '{value}'", "mne-flag");
                }
            }
        }

        // 3. Bespoke property rules.
        foreach (var rule in _rules.PropertyRules)
        {
            foreach (var value in content.PropertyValues(rule.Name))
            {
                if (string.Equals(value.Trim(), rule.ExpectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return ExclusionDecision.Exclude(
                        $"property '{rule.Name}' equals '{rule.ExpectedValue}'", "property-rule");
                }
            }
        }

        return ExclusionDecision.Allowed;
    }
}
