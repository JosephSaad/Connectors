// Altrata/RelationshipPathIndex.cs
// --------------------------------
// Relationship-path materialization (RELATIONSHIP_PATHS). Precomputes bounded
// per-person path summaries from the RelationshipPath / BoardMembership /
// Organization feed datasets and joins them onto PersonProfile externalItems
// at transform time. See docs/RELATIONSHIP_PATHS.md.
//
// Representation is deliberately BOUNDED — a person item never carries the raw
// graph: only degree counts, a total path count, a short top-orgs list
// (capped) and one human-readable "strongest path" sentence.
//
// The persisted index (edges + person-org rows) lives in the identity store
// (SQLite / SQL Server) so it is deterministic, reconcilable and delta-aware:
// a full crawl rebuilds it from the current feed contents, and a withdrawn
// (tombstoned) person is dropped from it. This class is the in-memory view
// that computes the summaries; it never touches ACLs.

using AltrataConnector.Identity;

namespace AltrataConnector.Altrata;

/// <summary>Bounded, materializable summary for one person.</summary>
public sealed record PersonPathSummary(
    int FirstDegreeCount,
    int SecondDegreeCount,
    int PathCount,
    IReadOnlyList<string> TopConnectedOrgs,
    string StrongestPathSummary);

public sealed class RelationshipPathIndex
{
    public const int DefaultTopOrgLimit = 3;

    // Direct (intermediaries == 0) undirected adjacency.
    private readonly Dictionary<string, HashSet<string>> _directAdjacency = new(StringComparer.Ordinal);
    // Every edge touching a person (any intermediary count), for path count + strongest.
    private readonly Dictionary<string, List<PathEdge>> _pathsByPerson = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _personOrgs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _personName = new(StringComparer.Ordinal);

    private readonly int _topOrgLimit;

    private RelationshipPathIndex(int topOrgLimit) => _topOrgLimit = Math.Max(1, topOrgLimit);

    public int EdgeCount { get; private set; }

    /// <summary>Build the in-memory index from persisted edges + person-org rows.</summary>
    public static RelationshipPathIndex Build(
        IReadOnlyCollection<PathEdge> edges,
        IReadOnlyCollection<PersonOrg> personOrgs,
        int topOrgLimit = DefaultTopOrgLimit)
    {
        var index = new RelationshipPathIndex(topOrgLimit);

        foreach (var edge in edges)
        {
            if (string.IsNullOrEmpty(edge.PersonA) || string.IsNullOrEmpty(edge.PersonB)
                || edge.PersonA == edge.PersonB)
                continue;  // skip self-loops / malformed edges
            index.EdgeCount++;

            AddPath(index._pathsByPerson, edge.PersonA, edge);
            AddPath(index._pathsByPerson, edge.PersonB, edge);

            if (edge.Intermediaries == 0)
            {
                Link(index._directAdjacency, edge.PersonA, edge.PersonB);
                Link(index._directAdjacency, edge.PersonB, edge.PersonA);
            }

            RecordName(index._personName, edge.PersonA, edge.PersonAName);
            RecordName(index._personName, edge.PersonB, edge.PersonBName);
        }

        foreach (var org in personOrgs)
        {
            if (string.IsNullOrEmpty(org.PersonId) || string.IsNullOrEmpty(org.Org))
                continue;
            var list = index._personOrgs.TryGetValue(org.PersonId, out var existing)
                ? existing
                : index._personOrgs[org.PersonId] = new List<string>();
            if (!list.Contains(org.Org, StringComparer.OrdinalIgnoreCase))
                list.Add(org.Org);
        }

        return index;
    }

    /// <summary>
    /// Compute the bounded summary for a person, or null when the person has no
    /// paths at all (nothing to materialize — the item stays clean).
    /// </summary>
    public PersonPathSummary? Summarize(string personId)
    {
        var hasPaths = _pathsByPerson.TryGetValue(personId, out var paths);
        var direct = _directAdjacency.TryGetValue(personId, out var neighbours)
            ? neighbours
            : new HashSet<string>(StringComparer.Ordinal);
        if (!hasPaths && direct.Count == 0)
            return null;
        paths ??= new List<PathEdge>();

        var firstDegree = direct.Count;

        // Second degree: neighbours-of-neighbours, excluding self and first-degree.
        var second = new HashSet<string>(StringComparer.Ordinal);
        foreach (var neighbour in direct)
        {
            if (!_directAdjacency.TryGetValue(neighbour, out var beyond))
                continue;
            foreach (var candidate in beyond)
            {
                if (candidate != personId && !direct.Contains(candidate))
                    second.Add(candidate);
            }
        }

        var topOrgs = TopConnectedOrgs(direct);
        var strongest = StrongestSummary(personId, paths);

        return new PersonPathSummary(firstDegree, second.Count, paths.Count, topOrgs, strongest);
    }

    /// <summary>Orgs of the person's direct neighbours, ranked by frequency then
    /// name (deterministic), capped at the top-org limit.</summary>
    private IReadOnlyList<string> TopConnectedOrgs(HashSet<string> direct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var neighbour in direct)
        {
            if (!_personOrgs.TryGetValue(neighbour, out var orgs))
                continue;
            foreach (var org in orgs)
                counts[org] = counts.TryGetValue(org, out var c) ? c + 1 : 1;
        }
        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(_topOrgLimit)
            .Select(kv => kv.Key)
            .ToList();
    }

    private string StrongestSummary(string personId, List<PathEdge> paths)
    {
        if (paths.Count == 0)
            return BuildSummaryString(0, 0, personId);

        // Strongest = max strength; tie-break fewer intermediaries, then other-id
        // (deterministic regardless of edge insertion order).
        PathEdge? best = null;
        foreach (var edge in paths)
        {
            if (best == null
                || edge.Strength > best.Strength
                || (edge.Strength == best.Strength && edge.Intermediaries < best.Intermediaries)
                || (edge.Strength == best.Strength && edge.Intermediaries == best.Intermediaries
                    && string.CompareOrdinal(Other(edge, personId), Other(best, personId)) < 0))
            {
                best = edge;
            }
        }

        var other = Other(best!, personId);
        var target = FirstOrg(other) ?? Name(other) ?? other;
        return BuildSummaryString(paths.Count, best!.Intermediaries, target);
    }

    /// <summary>Pure, testable summary-sentence builder.</summary>
    public static string BuildSummaryString(int pathCount, int intermediaries, string target)
    {
        var paths = pathCount == 1 ? "1 path" : $"{pathCount} paths";
        if (intermediaries <= 0)
            return $"{paths} directly to {target}";
        var hop = intermediaries == 1 ? "1 intermediary" : $"{intermediaries} intermediaries";
        return $"{paths} via {hop} to {target}";
    }

    private string? FirstOrg(string personId) =>
        _personOrgs.TryGetValue(personId, out var orgs) && orgs.Count > 0 ? orgs[0] : null;

    private string? Name(string personId) =>
        _personName.TryGetValue(personId, out var name) ? name : null;

    private static string Other(PathEdge edge, string personId) =>
        edge.PersonA == personId ? edge.PersonB : edge.PersonA;

    private static void AddPath(Dictionary<string, List<PathEdge>> map, string person, PathEdge edge)
    {
        if (!map.TryGetValue(person, out var list))
            map[person] = list = new List<PathEdge>();
        list.Add(edge);
    }

    private static void Link(Dictionary<string, HashSet<string>> adjacency, string from, string to)
    {
        if (!adjacency.TryGetValue(from, out var set))
            adjacency[from] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(to);
    }

    private static void RecordName(Dictionary<string, string> names, string person, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !names.ContainsKey(person))
            names[person] = name!.Trim();
    }
}
