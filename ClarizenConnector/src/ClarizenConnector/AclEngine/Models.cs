// AclEngine/Models.cs
// -------------------
// Directory snapshot models used by the ACL resolver: Clarizen users, groups
// (with membership), and per-project membership (who can see a project and
// its child work items).

namespace ClarizenConnector.AclEngine;

public sealed record ClarizenUser(string Id, string? Email, string? DisplayName, bool IsSuperUser = false);

public sealed record ClarizenGroup(string Id, string? Name, List<string> MemberUserIds);

/// <summary>
/// In-memory directory snapshot loaded once per crawl (users, groups,
/// project membership). Keys are raw Clarizen ids ("/User/17", "/Group/3").
/// </summary>
public sealed class DirectorySnapshot
{
    public Dictionary<string, ClarizenUser> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ClarizenGroup> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Project id → principal ids (users and groups) with access to the project.</summary>
    public Dictionary<string, List<string>> ProjectMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddProjectMember(string projectId, string principalId)
    {
        if (!ProjectMembers.TryGetValue(projectId, out var members))
        {
            members = new List<string>();
            ProjectMembers[projectId] = members;
        }
        if (!members.Contains(principalId, StringComparer.OrdinalIgnoreCase))
            members.Add(principalId);
    }

    public IReadOnlyList<string> MembersOf(string projectId) =>
        ProjectMembers.TryGetValue(projectId, out var members)
            ? members
            : Array.Empty<string>();
}
