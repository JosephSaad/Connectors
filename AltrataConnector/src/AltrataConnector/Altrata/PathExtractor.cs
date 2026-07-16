// Altrata/PathExtractor.cs
// ------------------------
// Extract path-index rows (edges + person-org memberships) from the
// RelationshipPath / BoardMembership / Organization feed datasets. Tombstones
// are honoured by the caller (delete records are skipped, so a full rebuild
// drops them); PersonProfile tombstones are collected so the caller can remove
// those persons from the index. Deterministic and side-effect free.

using System.Globalization;
using AltrataConnector.Identity;

namespace AltrataConnector.Altrata;

/// <summary>Accumulated path-index rows plus the person ids withdrawn this crawl.</summary>
public sealed class PathIndexBuild
{
    public List<PathEdge> Edges { get; } = new();
    public List<PersonOrg> PersonOrgs { get; } = new();
    /// <summary>Person ids whose PersonProfile is tombstoned — dropped from the index.</summary>
    public HashSet<string> WithdrawnPersonIds { get; } = new(StringComparer.Ordinal);

    // org id → display name, resolved from the Organization dataset.
    private readonly Dictionary<string, string> _orgNames = new(StringComparer.OrdinalIgnoreCase);

    internal void RegisterOrgName(string orgId, string name) => _orgNames[orgId] = name;

    internal string ResolveOrg(string org) =>
        _orgNames.TryGetValue(org, out var name) ? name : org;
}

public static class PathExtractor
{
    /// <summary>
    /// Fold one dataset's records into the build. Organizations should be fed
    /// before BoardMemberships so org-id → name resolution is available, but the
    /// result is still correct either way (unresolved ids fall back to the id).
    /// </summary>
    public static void Accumulate(PathIndexBuild build, string dataset, IReadOnlyList<FeedRecord> records)
    {
        switch (dataset)
        {
            case Datasets.Organization:
                foreach (var record in records)
                {
                    if (record.IsTombstone)
                        continue;
                    var orgId = record.Get("org_id") ?? record.Get("id");
                    var name = record.Get("organization_name") ?? record.Get("org_name");
                    if (orgId != null && name != null)
                        build.RegisterOrgName(orgId, name);
                }
                break;

            case Datasets.RelationshipPath:
                foreach (var record in records)
                {
                    if (record.IsTombstone)
                        continue;
                    var from = record.Get("from_person_id") ?? record.Get("from_person");
                    var to = record.Get("to_person_id") ?? record.Get("to_person");
                    if (from == null || to == null)
                        continue;
                    build.Edges.Add(new PathEdge(
                        from, to,
                        ParseDouble(record.Get("path_strength") ?? record.Get("strength")),
                        ParseInt(record.Get("intermediary_count") ?? record.Get("intermediaries")),
                        record.Get("from_person_name"),
                        record.Get("to_person_name")));
                }
                break;

            case Datasets.BoardMembership:
                foreach (var record in records)
                {
                    if (record.IsTombstone)
                        continue;
                    var person = record.Get("person_id");
                    var org = record.Get("org_name") ?? record.Get("organization_name") ?? record.Get("org_id");
                    if (person == null || org == null)
                        continue;
                    build.PersonOrgs.Add(new PersonOrg(person, build.ResolveOrg(org)));
                }
                break;

            case Datasets.PersonProfile:
                foreach (var record in records)
                {
                    if (record.IsTombstone && record.Id != null)
                        build.WithdrawnPersonIds.Add(record.Id);
                }
                break;
        }
    }

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n) : 0;
}
