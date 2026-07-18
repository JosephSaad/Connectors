// AclEngine/Models.cs
// -------------------
// Identity models for the BDH ACL engine. BDH carries Salesforce FIELDS but
// not the Salesforce sharing tables, so the only per-record principal signal
// is ownership (OwnerId / owner email). The identity directory is loaded from
// the BDH "User" object export (BDH_IDENTITY_OBJECT) and resolved to Entra
// object ids by email through Microsoft Graph.

namespace HadoopConnector.AclEngine;

/// <summary>One Salesforce user row from the BDH User export.</summary>
public sealed record BdhUser(string Id, string? Email, string? DisplayName, bool IsActive = true);

/// <summary>
/// In-memory user directory loaded once per crawl from the BDH User export.
/// Keys are Salesforce user ids; the email index supports owner-email ACLs.
/// </summary>
public sealed class UserDirectory
{
    public Dictionary<string, BdhUser> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, BdhUser> _byEmail = new(StringComparer.OrdinalIgnoreCase);

    public void Add(BdhUser user)
    {
        Users[user.Id] = user;
        if (!string.IsNullOrWhiteSpace(user.Email))
            _byEmail[user.Email!] = user;
    }

    public BdhUser? FindByEmail(string email) =>
        _byEmail.TryGetValue(email, out var user) ? user : null;

    public int Count => Users.Count;
}
