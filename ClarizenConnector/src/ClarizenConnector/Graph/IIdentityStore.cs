// Graph/IIdentityStore.cs
// -----------------------
// Persistent principal-mapping store: Clarizen users/groups → Entra ID
// identities. SQLite by default (data/{CONNECTOR_ID}_identity.db); SQL Server
// when USE_SQL_SERVER=true (dbo.Principals — see docs/SQL_CONTRACT.md).

namespace ClarizenConnector.Graph;

public sealed record PrincipalMapping(
    string ClarizenId,
    string PrincipalType,   // "user" | "group"
    string? Email,
    string? EntraId,
    DateTime UpdatedUtc);

public interface IIdentityStore : IDisposable
{
    /// <summary>Insert or update a mapping (keyed on ClarizenId).</summary>
    void Upsert(PrincipalMapping mapping);

    /// <summary>Lookup one mapping by Clarizen id; null when unknown.</summary>
    PrincipalMapping? Find(string clarizenId);

    /// <summary>All stored mappings.</summary>
    List<PrincipalMapping> All();

    /// <summary>Mappings with a resolved Entra id.</summary>
    int ResolvedCount();

    /// <summary>Total mappings.</summary>
    int Count();

    /// <summary>Remove every mapping (test/reset use).</summary>
    void Clear();
}
