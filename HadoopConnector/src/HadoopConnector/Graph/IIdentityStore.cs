// Graph/IIdentityStore.cs
// -----------------------
// Persistent principal-mapping store: BDH (Salesforce) users → Entra ID
// identities. SQLite by default (data/{CONNECTOR_ID}_identity.db); SQL Server
// when USE_SQL_SERVER=true (dbo.Principals — see docs/SQL_CONTRACT.md).

namespace HadoopConnector.Graph;

public sealed record PrincipalMapping(
    string SourceId,
    string PrincipalType,   // "user" | "group"
    string? Email,
    string? EntraId,
    DateTime UpdatedUtc);

public interface IIdentityStore : IDisposable
{
    /// <summary>Insert or update a mapping (keyed on SourceId).</summary>
    void Upsert(PrincipalMapping mapping);

    /// <summary>Lookup one mapping by Salesforce user id; null when unknown.</summary>
    PrincipalMapping? Find(string sourceId);

    /// <summary>All stored mappings.</summary>
    List<PrincipalMapping> All();

    /// <summary>Mappings with a resolved Entra id.</summary>
    int ResolvedCount();

    /// <summary>Total mappings.</summary>
    int Count();

    /// <summary>Remove every mapping (test/reset use).</summary>
    void Clear();
}
