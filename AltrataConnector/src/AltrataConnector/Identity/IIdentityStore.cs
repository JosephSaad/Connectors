// Identity/IIdentityStore.cs
// --------------------------
// Identity / entitlement / crosswalk store.
//
//   * seats            — the licensed principals every ACL is built from
//   * crm_contacts     — internal CRM contacts used for entity resolution
//   * crosswalk        — altrata_id ↔ crm_contact_id links (+ match rule)
//   * ingested_items   — registry of every externalItem we own (drives the
//                        seat-change re-ACL pass and purge-all)
//
// Default backend: SQLite at data/{CONNECTOR_ID}_identity.db.
// USE_SQL_SERVER=true: SqlServerIdentityStore (same shape, SQL Server tables).

namespace AltrataConnector.Identity;

/// <summary>Kind of a licensed seat principal.</summary>
public enum SeatPrincipalKind
{
    /// <summary>Entra user identified by UPN.</summary>
    UserUpn,
    /// <summary>Entra user identified by object id (GUID).</summary>
    UserObjectId,
    /// <summary>Entra security group (all members are seated).</summary>
    Group,
}

public sealed record SeatPrincipal(SeatPrincipalKind Kind, string Value);

/// <summary>Internal CRM contact used for deterministic entity resolution.</summary>
public sealed record CrmContact
{
    public required string Id { get; init; }
    public string? Email { get; init; }
    public string? Name { get; init; }
    public string? Employer { get; init; }
    /// <summary>Job title / role, used as a hint by the fuzzy matching tier.</summary>
    public string? Role { get; init; }
}

public sealed record CrosswalkEntry(
    string AltrataId, string CrmContactId, string MatchRule, DateTime LinkedUtc);

public sealed record IngestedItem(
    string ItemId, string Dataset, string AclHash, DateTime LastIngestedUtc);

/// <summary>One relationship edge in the path index (RelationshipPath dataset).
/// Intermediaries == 0 means a direct (first-degree) connection.</summary>
public sealed record PathEdge(
    string PersonA, string PersonB, double Strength, int Intermediaries,
    string? PersonAName = null, string? PersonBName = null);

/// <summary>One person → org membership in the path index (BoardMembership).</summary>
public sealed record PersonOrg(string PersonId, string Org);

public interface IIdentityStore : IDisposable
{
    // ---- seats -------------------------------------------------------------
    void ReplaceSeats(IEnumerable<SeatPrincipal> seats);
    IReadOnlyList<SeatPrincipal> GetSeats();

    // ---- CRM contacts ---------------------------------------------------------
    void ReplaceCrmContacts(IEnumerable<CrmContact> contacts);
    CrmContact? FindContactByEmail(string emailLower);
    CrmContact? FindContactByNameEmployer(string nameNormalized, string employerNormalized);
    /// <summary>All contacts (normalized fields) — the fuzzy tier's candidate pool.</summary>
    IReadOnlyList<CrmContact> ListCrmContacts();
    int CountCrmContacts();

    // ---- crosswalk ----------------------------------------------------------------
    void UpsertCrosswalk(CrosswalkEntry entry);
    CrosswalkEntry? GetCrosswalk(string altrataId);
    /// <summary>Delete a crosswalk row (per-subject erasure).</summary>
    void RemoveCrosswalk(string altrataId);
    IReadOnlyList<CrosswalkEntry> ListCrosswalk();

    // ---- ingested item registry ------------------------------------------------------
    void RecordIngestedItem(IngestedItem item);
    void RemoveIngestedItem(string itemId);
    IReadOnlyList<IngestedItem> ListIngestedItems();
    IReadOnlyList<IngestedItem> ListItemsWithAclHashOtherThan(string aclHash);
    int CountIngestedItems();

    // ---- item ↔ subject reverse index (per-subject erasure) --------------------------
    /// <summary>Link an ingested item to the subject person id(s) it concerns, so a
    /// DSAR erasure can find every externalItem (PersonProfile + derived) for a person.</summary>
    void RecordItemSubjects(string itemId, IReadOnlyCollection<string> subjectIds);
    /// <summary>Every item id concerning a subject (drives forget-subject).</summary>
    IReadOnlyList<string> ListItemsForSubject(string subjectId);

    // ---- relationship path index (RELATIONSHIP_PATHS) --------------------------------
    /// <summary>Replace the whole path index (deterministic full rebuild per crawl).</summary>
    void ReplacePathIndex(IReadOnlyCollection<PathEdge> edges, IReadOnlyCollection<PersonOrg> personOrgs);
    /// <summary>Load the persisted edges + person-org rows (joined onto person items).</summary>
    (IReadOnlyList<PathEdge> Edges, IReadOnlyList<PersonOrg> PersonOrgs) LoadPathIndex();
    /// <summary>Drop a person from the path index (tombstone withdrawal).</summary>
    void RemovePersonFromPathIndex(string personId);
    int CountPathEdges();

    /// <summary>Wipe everything (purge-all).</summary>
    void WipeAll();
}
