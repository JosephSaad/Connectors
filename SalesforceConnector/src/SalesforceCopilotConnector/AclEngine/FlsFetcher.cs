// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclEngine/FlsFetcher.cs   (WP-SF-2)
// -----------------------------------
// Salesforce FIELD-LEVEL SECURITY (FLS) — read the per-field read permissions
// for the configured objects and turn them into a per-object set of fields that
// must never reach the index.
//
// Why this exists
// ---------------
// The connector reproduces Salesforce's RECORD-level sharing model faithfully
// (see AclEngine/GroupAclBuilder.cs). It did not, until this work package,
// evaluate FIELD-level security at all: FLS existed only as a hand-maintained
// per-object "flsFields" array in config/schema.json. A user permitted to see a
// record therefore saw EVERY indexed field on it — including fields Salesforce
// itself would hide (compensation, margin, and similar).
//
//
// ══ THE DECISION RULE ═══════════════════════════════════════════════════════
//
// An indexed Graph external item carries ONE property set and ONE content body,
// shared by every principal on that item's ACL. Field visibility is therefore
// per-ITEM, not per-user: there is no mechanism to show a property to one ACL
// principal and hide it from another.
//
// Consequently a field that SOME but not ALL principals on the item's ACL can
// read MUST BE DROPPED. This is the least-privilege union, and it is what
// FLS_MODE=strict (the DEFAULT) does.
//
// FLS_MODE=permissive is a documented, deliberately WEAKER escape hatch: it
// drops only fields that NO principal on the ACL can read. Said plainly:
// PERMISSIVE MODE CAN EXPOSE A FIELD TO A PRINCIPAL SALESFORCE WOULD DENY.
// It exists for orgs where strict over-drops badly enough to break grounding,
// and it should be treated as an accepted risk, not a default.
//
// Two facts keep strict from degenerating into "drop everything":
//
//   1. Salesforce's FieldPermissions table only holds rows for fields whose FLS
//      is CONFIGURABLE. A field with no rows at all (Id, Name, …) is not
//      governed by FLS and is never dropped on FLS grounds.
//   2. "Principals in scope" is not every permission set in the org — it is the
//      set of permission-set parents (profiles and permission sets) that are
//      actually ASSIGNED to an active user AND grant read on the object. Those
//      are exactly the principals whose FLS can matter for that object's items.
//
// ════════════════════════════════════════════════════════════════════════════
//
//
// Data sources (both go through the existing SalesforceClient — no new HTTP path):
//
//   * FieldPermissions   SELECT Field, SobjectType, PermissionsRead, ParentId
//                        FROM FieldPermissions WHERE SobjectType IN (…)
//   * scope              SELECT PermissionSetId FROM PermissionSetAssignment
//                        WHERE PermissionSetId IN (SELECT ParentId FROM ObjectPermissions
//                              WHERE SObjectType = '…' AND PermissionsRead = true)
//                          AND Assignee.IsActive = true
//   * describe fallback  SalesforceClient.DescribeSObjectAsync — describe reflects
//                        the RUNNING USER's FLS, so fields the integration user
//                        cannot read simply do not appear. Used when the org
//                        denies the integration user access to FieldPermissions.
//
// Failure posture is FAIL CLOSED throughout: an unreadable scope query yields an
// empty scope, and an empty scope under strict mode drops every governed field.
// Over-dropping degrades grounding; under-dropping is an over-sharing incident.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>How field permissions are collapsed into a single per-item decision.</summary>
public enum FlsMode
{
    /// <summary>
    /// DEFAULT. Drop a governed field unless EVERY principal in scope can read it
    /// (least-privilege union). Safe: never exposes a field Salesforce would deny.
    /// </summary>
    Strict,

    /// <summary>
    /// WEAKER ESCAPE HATCH. Drop a governed field only when NO principal in scope
    /// can read it. Can expose a field to a principal Salesforce would deny.
    /// </summary>
    Permissive,
}

/// <summary>
/// How DOTTED cross-object relationship fields ("Contact.Phone" on Case) are
/// treated. They are a distinct problem from own-object fields: the permission
/// facts that govern them live on the RELATED object, not the one being crawled.
/// </summary>
public enum FlsRelationshipMode
{
    /// <summary>
    /// DEFAULT. Resolve the relationship to its target object and evaluate the
    /// sub-field against THAT object's permissions. A relationship whose target
    /// cannot be resolved — or whose permissions were not fetched — fails CLOSED
    /// and the field is dropped.
    /// </summary>
    Evaluate,

    /// <summary>Drop every dotted relationship field unconditionally. Maximally conservative.</summary>
    Drop,

    /// <summary>
    /// PRE-WP-SF-3 BEHAVIOUR. Do not evaluate dotted fields at all. They were
    /// silently never FLS-checked, because ComputeDrops matched them against
    /// GovernedFields, which only ever holds BARE own-object field names.
    /// This is an escape hatch for orgs where evaluate over-drops; it re-opens a
    /// known over-sharing gap.
    /// </summary>
    Ignore,
}

/// <summary>Environment-driven FLS configuration.</summary>
public static class FlsSettings
{
    public const string EnforcementEnvVar = "FLS_ENFORCEMENT";
    public const string ModeEnvVar = "FLS_MODE";
    public const string CacheTtlEnvVar = "FLS_CACHE_TTL_HOURS";

    /// <summary>Default cache lifetime for a fetched permission snapshot.</summary>
    public const int DefaultCacheTtlHours = 24;

    /// <summary>
    /// <c>FLS_ENFORCEMENT</c> — <b>defaults ON</b>. This is a correctness fix for a
    /// live over-sharing exposure, not a feature, so it is opt-OUT (like
    /// <c>DELETION_SYNC</c>). Set <c>false</c>/<c>0</c>/<c>no</c>/<c>off</c> to
    /// restore the pre-WP-SF-2 behaviour exactly — including the historical
    /// content-body leak of the manual <c>flsFields</c> list.
    /// </summary>
    public static bool Enforcement
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnforcementEnvVar);
            if (string.IsNullOrWhiteSpace(raw))
                return true;
            return raw.Trim().ToLowerInvariant() is not ("false" or "0" or "no" or "off");
        }
    }

    /// <summary><c>FLS_MODE</c> — <c>strict</c> (default) or <c>permissive</c>.</summary>
    public static FlsMode Mode => ParseMode(Environment.GetEnvironmentVariable(ModeEnvVar));

    /// <summary>Parse an FLS_MODE value; anything unrecognised (or blank) means strict.</summary>
    public static FlsMode ParseMode(string? raw)
    {
        return (raw ?? "").Trim().ToLowerInvariant() == "permissive"
            ? FlsMode.Permissive
            : FlsMode.Strict;
    }

    /// <summary>
    /// <c>FLS_RELATIONSHIP_FIELDS</c> — <c>evaluate</c> (default), <c>drop</c> or
    /// <c>ignore</c>. See <see cref="FlsRelationshipMode"/>. Anything unrecognised
    /// means <c>evaluate</c>, so a typo fails towards the safe behaviour.
    /// </summary>
    public const string RelationshipFieldsEnvVar = "FLS_RELATIONSHIP_FIELDS";

    /// <summary><c>FLS_RELATIONSHIP_FIELDS</c> — how dotted cross-object fields are treated.</summary>
    public static FlsRelationshipMode RelationshipFields =>
        ParseRelationshipMode(Environment.GetEnvironmentVariable(RelationshipFieldsEnvVar));

    /// <summary>Parse an FLS_RELATIONSHIP_FIELDS value; anything unrecognised means evaluate.</summary>
    public static FlsRelationshipMode ParseRelationshipMode(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "drop" => FlsRelationshipMode.Drop,
            "ignore" => FlsRelationshipMode.Ignore,
            _ => FlsRelationshipMode.Evaluate,
        };

    /// <summary><c>FLS_CACHE_TTL_HOURS</c> — how long a cached snapshot stays fresh (default 24).</summary>
    public static int CacheTtlHours
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(CacheTtlEnvVar);
            if (!string.IsNullOrWhiteSpace(raw)
                && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                return parsed;
            }
            return DefaultCacheTtlHours;
        }
    }
}

/// <summary>One field dropped from the index, with the reason (for the audit manifest).</summary>
public sealed record FlsDrop(string Field, string Reason);

/// <summary>Per-object drop summary for the audit manifest.</summary>
public sealed record FlsObjectDrops(int PrincipalsInScope, List<FlsDrop> Drops);

/// <summary>
/// Field-read permission facts for one Salesforce object.
///
/// <para><see cref="GovernedFields"/> is the crucial one: it holds only the
/// fields that appear in Salesforce's FieldPermissions table, i.e. the fields
/// whose FLS is actually configurable. Anything outside it (Id, Name, standard
/// always-readable fields) is NOT governed by FLS and is never dropped here.</para>
/// </summary>
public sealed class FlsObjectPermissions
{
    public FlsObjectPermissions(
        string objectName,
        HashSet<string> principalsInScope,
        HashSet<string> governedFields,
        Dictionary<string, HashSet<string>> readersByField,
        bool fromDescribeFallback = false,
        HashSet<string>? describeVisibleFields = null,
        DateTimeOffset? fetchedAt = null,
        bool fieldPermissionRowsSeen = true)
    {
        ObjectName = objectName;
        PrincipalsInScope = principalsInScope;
        GovernedFields = governedFields;
        ReadersByField = readersByField;
        FromDescribeFallback = fromDescribeFallback;
        DescribeVisibleFields = describeVisibleFields ?? new HashSet<string>(StringComparer.Ordinal);
        FetchedAt = fetchedAt ?? DateTimeOffset.UtcNow;
        FieldPermissionRowsSeen = fieldPermissionRowsSeen;
    }

    public string ObjectName { get; }

    /// <summary>
    /// Permission-set parents (profiles + permission sets) that are assigned to at
    /// least one active user AND grant read on this object. These are the only
    /// principals whose FLS can affect an item of this object type.
    /// </summary>
    public HashSet<string> PrincipalsInScope { get; }

    /// <summary>Fields that appear in FieldPermissions at all, i.e. FLS-governed fields.</summary>
    public HashSet<string> GovernedFields { get; }

    /// <summary>Field → the permission-set parents that grant <c>PermissionsRead</c>.</summary>
    public Dictionary<string, HashSet<string>> ReadersByField { get; }

    /// <summary>True when FieldPermissions was unreadable and describe was used instead.</summary>
    public bool FromDescribeFallback { get; }

    /// <summary>Fields visible to the integration user, per describe (fallback mode only).</summary>
    public HashSet<string> DescribeVisibleFields { get; }

    public DateTimeOffset FetchedAt { get; }

    /// <summary>
    /// True when the FieldPermissions query returned AT LEAST ONE row (for the
    /// query as a whole, not just this object).
    ///
    /// <para>Without this, a zero-row FieldPermissions result was indistinguishable
    /// from "this org governs nothing" — a silent fail-OPEN. Every real Salesforce
    /// org has FieldPermissions rows, so zero rows across the whole query means the
    /// integration user almost certainly cannot read the table (a 200-with-no-rows,
    /// not the exception the describe fallback catches).</para>
    /// </summary>
    public bool FieldPermissionRowsSeen { get; }

    /// <summary>
    /// True when this object's permissions are consistent with a silent fail-open:
    /// no governed fields AND no FieldPermissions rows anywhere in the query.
    ///
    /// <para>This is a SIGNAL ONLY — it deliberately changes no drop decision, since
    /// acting on it would false-drop every field of a genuinely ungoverned org. It
    /// is logged loudly and recorded in the audit manifest so an operator can tell
    /// "nothing is governed" apart from "we could not see what is governed".</para>
    /// </summary>
    public bool IsSuspectedFailOpen =>
        !FromDescribeFallback && !FieldPermissionRowsSeen && GovernedFields.Count == 0;

    // ── Cache serialisation ───────────────────────────────────────────────────

    /// <summary>Serialise for the <c>fls_cache</c> store (stable key order).</summary>
    public string ToJson()
    {
        var readers = new JsonObject();
        foreach (var field in ReadersByField.Keys.OrderBy(f => f, StringComparer.Ordinal))
        {
            var arr = new JsonArray();
            foreach (var parent in ReadersByField[field].OrderBy(p => p, StringComparer.Ordinal))
                arr.Add(parent);
            readers[field] = arr;
        }

        return new JsonObject
        {
            ["version"] = 1,
            ["objectName"] = ObjectName,
            ["fetchedAt"] = FetchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["fromDescribeFallback"] = FromDescribeFallback,
            ["fieldPermissionRowsSeen"] = FieldPermissionRowsSeen,
            ["principalsInScope"] = SortedArray(PrincipalsInScope),
            ["governedFields"] = SortedArray(GovernedFields),
            ["describeVisibleFields"] = SortedArray(DescribeVisibleFields),
            ["readersByField"] = readers,
        }.ToJsonString();
    }

    /// <summary>Deserialise a <c>fls_cache</c> payload; returns null when unusable.</summary>
    public static FlsObjectPermissions? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject doc)
                return null;
            if ((int?)doc["version"] != 1)
                return null;

            var readers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (doc["readersByField"] is JsonObject readersObj)
            {
                foreach (var (field, node) in readersObj)
                {
                    readers[field] = node is JsonArray arr
                        ? arr.Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal);
                }
            }

            DateTimeOffset fetchedAt = default;
            if (doc["fetchedAt"]?.GetValue<string>() is { Length: > 0 } stamp)
                DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out fetchedAt);

            return new FlsObjectPermissions(
                objectName: doc["objectName"]?.GetValue<string>() ?? "",
                principalsInScope: ReadSet(doc["principalsInScope"]),
                governedFields: ReadSet(doc["governedFields"]),
                readersByField: readers,
                fromDescribeFallback: doc["fromDescribeFallback"]?.GetValue<bool>() ?? false,
                describeVisibleFields: ReadSet(doc["describeVisibleFields"]),
                fetchedAt: fetchedAt,
                // Absent in pre-WP-SF-3 cache rows. Those were written by a fetch that
                // we cannot re-examine, so assume rows WERE seen rather than raise a
                // fail-open alarm on every upgraded deployment's first run.
                fieldPermissionRowsSeen: doc["fieldPermissionRowsSeen"]?.GetValue<bool>() ?? true);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentNullException)
        {
            // A corrupt cache row is treated as absent — the caller re-fetches and
            // overwrites it, exactly like the field_cache precedent.
            return null;
        }
    }

    private static JsonArray SortedArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values.OrderBy(v => v, StringComparer.Ordinal))
            arr.Add(v);
        return arr;
    }

    private static HashSet<string> ReadSet(JsonNode? node) =>
        node is JsonArray arr
            ? arr.Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>Field permissions for every configured object, as of one crawl.</summary>
public sealed class FlsSnapshot
{
    private readonly Dictionary<string, FlsObjectPermissions> _objects =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, FlsObjectPermissions> Objects => _objects;

    internal void Set(string objectName, FlsObjectPermissions permissions) =>
        _objects[objectName] = permissions;

    /// <summary>Permissions for <paramref name="objectName"/>, or null if not fetched.</summary>
    public FlsObjectPermissions? Get(string objectName) =>
        _objects.TryGetValue(objectName, out var p) ? p : null;
}

/// <summary>
/// Collapses per-principal field permissions into the single per-item decision
/// the Graph data model forces on us. See "THE DECISION RULE" at the top of this
/// file — this is where it is actually applied.
/// </summary>
public static class FlsPolicy
{
    /// <summary>
    /// Return the fields that must be dropped from an item of this object type.
    ///
    /// <param name="perms">Fetched field-read permissions for the object.</param>
    /// <param name="candidateFields">The fields the connector would otherwise index.</param>
    /// <param name="mode">Strict (least-privilege union) or permissive.</param>
    /// <param name="principalScope">
    /// Optional narrower principal set — pass the principals on a specific item's
    /// ACL to scope the union to that item. When null, the object-wide scope
    /// (<see cref="FlsObjectPermissions.PrincipalsInScope"/>) is used, which is the
    /// conservative choice: it over-drops rather than under-drops.
    /// </param>
    /// </summary>
    public static List<FlsDrop> ComputeDrops(
        FlsObjectPermissions perms,
        IEnumerable<string> candidateFields,
        FlsMode mode,
        IReadOnlySet<string>? principalScope = null,
        FlsRelationshipMode? relationshipMode = null,
        IReadOnlyDictionary<string, FlsObjectPermissions>? relatedPermissions = null)
    {
        ArgumentNullException.ThrowIfNull(perms);
        ArgumentNullException.ThrowIfNull(candidateFields);

        var scope = principalScope ?? perms.PrincipalsInScope;
        var relationships = relationshipMode ?? FlsSettings.RelationshipFields;
        var drops = new List<FlsDrop>();

        foreach (var field in candidateFields
                     .Where(f => !string.IsNullOrEmpty(f))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            // ── DESCRIBE FALLBACK ─────────────────────────────────────────────
            // Handled first and on its own, because it is a fundamentally
            // different kind of evidence: not per-principal permission rows, but
            // the integration user's OWN view of the object. Everything the crawl
            // then reads flows through that same user, so the check is complete by
            // construction — a field absent from describe is one Salesforce will
            // not return values for anyway. The relationship logic below does not
            // apply here (see the note on it).
            if (perms.FromDescribeFallback)
            {
                if (!perms.DescribeVisibleFields.Contains(field))
                {
                    drops.Add(new FlsDrop(
                        field,
                        "describe fallback: field not visible to the integration user "
                        + "(FieldPermissions was not readable)"));
                }
                continue;
            }

            // ── CROSS-OBJECT RELATIONSHIP FIELDS ──────────────────────────────
            // "Contact.Phone" on Case is governed by CONTACT's FieldPermissions,
            // not Case's. Matching it against this object's GovernedFields (which
            // only ever holds bare own-object names) never matched, so these were
            // silently never FLS-evaluated at all.
            if (field.Contains('.', StringComparison.Ordinal))
            {
                var relationshipDrop = EvaluateRelationshipField(
                    field, mode, relationships, relatedPermissions);
                if (relationshipDrop is not null)
                    drops.Add(relationshipDrop);
                continue;
            }

            // ── COMPOUND FIELDS — KNOWN LIMITATION ────────────────────────────
            // Each candidate is judged ONLY on its own FieldPermissions evidence.
            // FieldPermissions governs the COMPONENTS (BillingStreet, BillingCity,
            // …); a compound (BillingAddress) carries no rows of its own, so a
            // restricted component does NOT drop the compound and its value can
            // still reach the index inside the compound.
            //
            // Propagation was implemented by INFERRING components from field names
            // and has been removed as unsound: on a Person-Accounts org
            // Salutation/FirstName/LastName carry real rows, so restricting any one
            // of them dropped Account.Name for EVERY account — including business
            // accounts whose Name is unrelated plain text. Compound membership must
            // be read from describe metadata to be correct. Operators who need this
            // today add the compound itself to the manual flsFields list.
            var reason = DropReason(perms, field, mode, scope);
            if (reason is not null)
            {
                drops.Add(new FlsDrop(field, reason));
            }
        }

        return drops;
    }

    /// <summary>
    /// The per-field decision for a BARE own-object field. Returns the drop reason,
    /// or <c>null</c> when the field may be indexed.
    /// </summary>
    private static string? DropReason(
        FlsObjectPermissions perms,
        string field,
        FlsMode mode,
        IReadOnlySet<string> scope)
    {
        // Describe fallback: we have no per-principal facts at all, only the
        // integration user's own view. A field Salesforce hides from that user
        // is hidden from us too, so it cannot be indexed.
        //
        // Reached only for a RELATED object whose own fetch fell back to describe;
        // the crawled object's own fallback is short-circuited in ComputeDrops.
        if (perms.FromDescribeFallback)
        {
            return perms.DescribeVisibleFields.Contains(field)
                ? null
                : "describe fallback: field not visible to the integration user "
                  + "(FieldPermissions was not readable)";
        }

        // Not in FieldPermissions ⇒ Salesforce does not govern this field's FLS.
        if (!perms.GovernedFields.Contains(field))
            return null;

        var readers = perms.ReadersByField.TryGetValue(field, out var r)
            ? r
            : new HashSet<string>(StringComparer.Ordinal);
        var readersInScope = readers.Count == 0 ? 0 : readers.Count(scope.Contains);

        if (mode == FlsMode.Strict)
        {
            // Fail closed: with nobody in scope we cannot prove ANY principal may
            // read a governed field, so it is dropped.
            if (scope.Count == 0)
                return "strict: no principal in scope could be proven to read this field";

            // THE RULE: readable by some but not all ⇒ dropped, because the item
            // carries one property set shared by every ACL principal.
            if (readersInScope < scope.Count)
                return $"strict: readable by {readersInScope} of {scope.Count} principal(s) in scope";

            return null;
        }

        return readersInScope == 0
            ? $"permissive: readable by 0 of {scope.Count} principal(s) in scope"
            : null;
    }

    /// <summary>
    /// Decide a dotted "Relationship.Field" candidate against the RELATED object's
    /// permissions. Returns the drop, or <c>null</c> to keep the field.
    /// </summary>
    private static FlsDrop? EvaluateRelationshipField(
        string field,
        FlsMode mode,
        FlsRelationshipMode relationships,
        IReadOnlyDictionary<string, FlsObjectPermissions>? relatedPermissions)
    {
        if (relationships == FlsRelationshipMode.Ignore)
            return null;

        if (relationships == FlsRelationshipMode.Drop)
        {
            return new FlsDrop(
                field,
                "relationship: FLS_RELATIONSHIP_FIELDS=drop withholds every cross-object field");
        }

        var separator = field.IndexOf('.', StringComparison.Ordinal);
        var relationshipName = field[..separator];
        var subField = field[(separator + 1)..];

        // A multi-hop path ("Owner.UserRole.Id") would need the permissions of an
        // object two joins away. We do not resolve those, so they fail closed.
        if (subField.Length == 0 || subField.Contains('.', StringComparison.Ordinal))
        {
            return new FlsDrop(
                field,
                $"relationship: multi-hop path '{field}' cannot be resolved to a single object");
        }

        if (relatedPermissions is null
            || !relatedPermissions.TryGetValue(relationshipName, out var related))
        {
            // FAIL CLOSED. The relationship's target object is unknown, or its
            // permissions were not fetched, so nothing about this field is provable.
            return new FlsDrop(
                field,
                $"relationship: no field permissions available for '{relationshipName}' "
                + "(declare it in the object's relationshipObjects map so it can be evaluated)");
        }

        var reason = DropReason(related, subField, mode, related.PrincipalsInScope);
        return reason is null
            ? null
            : new FlsDrop(field, $"relationship {relationshipName} → {related.ObjectName}: {reason}");
    }
}

/// <summary>
/// Reads Salesforce field-level read permissions for the configured objects.
///
/// Reuses the existing <see cref="SalesforceClient"/> (and therefore its token
/// refresh, pagination and error handling) — no new HTTP path is opened. Results
/// are cached per org+object in the identity store's <c>fls_cache</c> table.
/// </summary>
public class FlsFetcher
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine.fls");

    private readonly SalesforceClient _sf;
    private readonly IIdentityStore? _store;
    private readonly string _instanceUrl;

    public FlsFetcher(SalesforceClient sfClient, IIdentityStore? store, string instanceUrl)
    {
        _sf = sfClient;
        _store = store;
        _instanceUrl = instanceUrl;
    }

    /// <summary>
    /// Fetch (or reuse cached) field-read permissions for <paramref name="objectNames"/>.
    ///
    /// <c>virtual</c> so tests and the ingest wiring can substitute it.
    /// </summary>
    public virtual async Task<FlsSnapshot> FetchAsync(
        IEnumerable<string> objectNames,
        bool forceRefresh = false)
    {
        var snapshot = new FlsSnapshot();
        var wanted = objectNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0)
            return snapshot;

        // ── 1. Serve what the cache can ───────────────────────────────────────
        var stale = new List<string>();
        foreach (var objectName in wanted)
        {
            var cached = forceRefresh ? null : ReadCache(objectName);
            if (cached is not null)
            {
                snapshot.Set(objectName, cached);
                continue;
            }
            stale.Add(objectName);
        }
        if (stale.Count == 0)
        {
            Logger.Info($"[FLS] All {wanted.Count} object(s) served from fls_cache");
            return snapshot;
        }

        // ── 2. FieldPermissions for everything still stale (one query) ────────
        Dictionary<string, Dictionary<string, HashSet<string>>>? readersByObject = null;
        Dictionary<string, HashSet<string>>? governedByObject = null;
        var rowsSeen = true;
        try
        {
            (readersByObject, governedByObject, rowsSeen) = await FetchFieldPermissionsAsync(stale);
            if (!rowsSeen)
            {
                // Not an exception — a 200 with an empty result set. Indistinguishable
                // from "this org governs nothing" without this check, and that is a
                // silent fail-OPEN: strict mode drops nothing and every field ships.
                Logger.Error(
                    "[FLS] FieldPermissions returned ZERO rows for "
                    + $"{string.Join(", ", stale)}. Every real Salesforce org has FieldPermissions "
                    + "rows, so this almost certainly means the integration user cannot read the "
                    + "table rather than that nothing is governed. NO FIELD WILL BE DROPPED ON FLS "
                    + "GROUNDS for these objects — treat this as a possible over-sharing exposure. "
                    + "The affected objects are listed under \"suspectedFailOpen\" in the audit manifest.");
            }
        }
        catch (Exception exc) when (exc is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            Logger.Warning(
                $"[FLS] FieldPermissions is not readable ({exc.Message}) — falling back to describe. "
                + "Describe reflects the integration user's own FLS, so any field Salesforce hides "
                + "from that user is dropped from the index.");
        }

        foreach (var objectName in stale)
        {
            FlsObjectPermissions perms;
            if (readersByObject is null || governedByObject is null)
            {
                perms = await FetchViaDescribeAsync(objectName);
            }
            else
            {
                var scope = await FetchPrincipalScopeAsync(objectName);
                perms = new FlsObjectPermissions(
                    objectName: objectName,
                    principalsInScope: scope,
                    governedFields: governedByObject.TryGetValue(objectName, out var g)
                        ? g
                        : new HashSet<string>(StringComparer.Ordinal),
                    readersByField: readersByObject.TryGetValue(objectName, out var r)
                        ? r
                        : new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
                    fieldPermissionRowsSeen: rowsSeen);
                Logger.Info(
                    $"[FLS] {objectName}: {perms.GovernedFields.Count} FLS-governed field(s), "
                    + $"{scope.Count} principal(s) in scope");
            }

            snapshot.Set(objectName, perms);
            WriteCache(objectName, perms);
        }

        return snapshot;
    }

    // ── Salesforce reads ──────────────────────────────────────────────────────

    /// <summary>
    /// One FieldPermissions query covering every stale object.
    ///
    /// Returns (object → field → parents granting read, object → governed fields).
    /// A field only lands in "governed" if FieldPermissions has a row for it — rows
    /// with PermissionsRead=false still count, because their existence is what
    /// proves Salesforce governs the field.
    /// </summary>
    private async Task<(Dictionary<string, Dictionary<string, HashSet<string>>> Readers,
                        Dictionary<string, HashSet<string>> Governed,
                        bool RowsSeen)>
        FetchFieldPermissionsAsync(List<string> objectNames)
    {
        var inList = string.Join(", ", objectNames.Select(n => $"'{SoqlLiteral(n)}'"));
        var soql =
            "SELECT Field, SobjectType, PermissionsRead, ParentId "
            + "FROM FieldPermissions "
            + $"WHERE SobjectType IN ({inList}) "
            + "ORDER BY SobjectType, Field";

        Logger.Info($"[FLS] SOQL: {soql}");
        var records = await _sf.QueryAllAsync(soql);
        Logger.Info($"[FLS] Fetched {records.Count} FieldPermissions row(s)");

        var readers = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var governed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var objectName in objectNames)
        {
            readers[objectName] = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            governed[objectName] = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var record in records)
        {
            var sobjectType = (string?)record["SobjectType"] ?? "";
            if (!readers.TryGetValue(sobjectType, out var objectReaders))
                continue;

            var field = NormalizeFieldName(sobjectType, (string?)record["Field"] ?? "");
            if (string.IsNullOrEmpty(field))
                continue;

            governed[sobjectType].Add(field);
            if (!objectReaders.TryGetValue(field, out var parents))
            {
                parents = new HashSet<string>(StringComparer.Ordinal);
                objectReaders[field] = parents;
            }

            var canRead = record["PermissionsRead"]?.GetValueKind() == JsonValueKind.True;
            var parentId = (string?)record["ParentId"] ?? "";
            if (canRead && parentId.Length > 0)
                parents.Add(parentId);
        }

        // records.Count is the QUERY-level fact, deliberately not per-object: an
        // object with no rows in an org that has plenty is genuinely ungoverned,
        // whereas zero rows for the whole query means we probably cannot see them.
        return (readers, governed, records.Count > 0);
    }

    /// <summary>
    /// The principals whose FLS matters for this object: permission-set parents
    /// assigned to an active user that grant read on the object itself.
    ///
    /// Mirrors the ObjectPermissions sub-select IdentityQueryClient already uses
    /// for GetAuthorizedUsersAsync, so the scope here matches the scope the ACL
    /// engine derives its groups from.
    /// </summary>
    private async Task<HashSet<string>> FetchPrincipalScopeAsync(string objectName)
    {
        var soql =
            "SELECT PermissionSetId FROM PermissionSetAssignment "
            + "WHERE PermissionSetId IN ("
            + "  SELECT ParentId FROM ObjectPermissions "
            + $"  WHERE SObjectType = '{SoqlLiteral(objectName)}' AND PermissionsRead = true"
            + ") "
            + "AND Assignee.IsActive = true";

        try
        {
            var records = await _sf.QueryAllAsync(soql);
            var scope = records
                .Select(r => (string?)r["PermissionSetId"] ?? "")
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            return scope;
        }
        catch (Exception exc) when (exc is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            // Fail closed: an empty scope makes strict mode drop every governed
            // field for this object rather than guess who can read what.
            Logger.Warning(
                $"[FLS] Principal-scope query failed for '{objectName}' ({exc.Message}) — "
                + "scope is empty, so strict mode will drop every FLS-governed field on this object.");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Describe-based fallback for orgs that deny the integration user access to
    /// FieldPermissions. Describe returns only the fields the RUNNING USER may
    /// see, so absence from the payload is itself the FLS signal.
    /// </summary>
    private async Task<FlsObjectPermissions> FetchViaDescribeAsync(string objectName)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var describe = await _sf.DescribeSObjectAsync(objectName);
            foreach (var field in (describe["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if ((string?)field["name"] is { Length: > 0 } name)
                    visible.Add(name);
            }
            Logger.Info($"[FLS] {objectName}: describe fallback — {visible.Count} field(s) visible to the integration user");
        }
        catch (Exception exc) when (exc is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            Logger.Error(
                $"[FLS] describe({objectName}) also failed ({exc.Message}) — no field is provably readable, "
                + "so every candidate field on this object is dropped.");
        }

        return new FlsObjectPermissions(
            objectName: objectName,
            principalsInScope: new HashSet<string>(StringComparer.Ordinal),
            governedFields: new HashSet<string>(StringComparer.Ordinal),
            readersByField: new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            fromDescribeFallback: true,
            describeVisibleFields: visible);
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    private FlsObjectPermissions? ReadCache(string objectName)
    {
        if (_store is null)
            return null;
        var perms = FlsObjectPermissions.FromJson(_store.GetCachedFls(_instanceUrl, objectName));
        if (perms is null)
            return null;

        var age = DateTimeOffset.UtcNow - perms.FetchedAt;
        if (age > TimeSpan.FromHours(FlsSettings.CacheTtlHours) || age < TimeSpan.FromHours(-1))
        {
            Logger.Info($"[FLS] Cached permissions for '{objectName}' are stale ({age:g}) — refetching");
            return null;
        }
        return perms;
    }

    private void WriteCache(string objectName, FlsObjectPermissions perms)
    {
        if (_store is null)
            return;
        try
        {
            _store.SaveCachedFls(_instanceUrl, objectName, perms.ToJson());
        }
        catch (Exception exc) when (exc is InvalidOperationException or IOException)
        {
            // A cache write failure must never fail the crawl — the next run refetches.
            Logger.Warning($"[FLS] Could not cache permissions for '{objectName}': {exc.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>FieldPermissions.Field is "Object.FieldName" — strip the object prefix.</summary>
    internal static string NormalizeFieldName(string objectName, string rawField)
    {
        if (string.IsNullOrEmpty(rawField))
            return "";
        var prefix = objectName + ".";
        return rawField.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? rawField[prefix.Length..]
            : rawField;
    }

    /// <summary>Escape a SOQL string literal (object API names cannot legally contain these, but be safe).</summary>
    private static string SoqlLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}

/// <summary>
/// Crawl-time orchestration: fetch permissions, compute the per-object drop set,
/// push it into the converter handlers, and emit the audit manifest.
/// </summary>
public static class FlsEnforcement
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine.fls");

    /// <summary>
    /// Apply field-level security to <paramref name="handlers"/> for this crawl.
    ///
    /// <para>Candidate fields are the handler's <c>selectedFields</c> keys — the
    /// fields the connector would otherwise index. Salesforce metadata/system
    /// columns (Id, OwnerId, CreatedDate, …) are deliberately out of scope: they
    /// are not FLS-governed in practice and the ACL engine needs them.</para>
    ///
    /// <para>Returns the per-object drop summary that was written to the manifest.</para>
    /// </summary>
    public static async Task<Dictionary<string, FlsObjectDrops>> ApplyAsync(
        FlsFetcher fetcher,
        IReadOnlyDictionary<string, Item.SalesforceObjectHandler> handlers,
        string connectorId,
        FlsMode? mode = null,
        string? logsDir = null,
        FlsRelationshipMode? relationshipMode = null)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(handlers);

        var effectiveMode = mode ?? FlsSettings.Mode;
        var effectiveRelationships = relationshipMode ?? FlsSettings.RelationshipFields;
        var summary = new Dictionary<string, FlsObjectDrops>(StringComparer.Ordinal);

        // Include child handlers — they convert inline child records through the
        // same two loops and would otherwise be an unguarded back door.
        var allHandlers = new Dictionary<string, Item.SalesforceObjectHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers.Values)
        {
            allHandlers[handler.ObjectName] = handler;
            foreach (var child in handler.ChildHandlers)
                allHandlers[child.ObjectName] = child;
        }
        if (allHandlers.Count == 0)
            return summary;

        // Dotted candidates need the RELATED object's permissions, so those objects
        // join the fetch. They are ordinary objects to FlsFetcher — no new query
        // shape, and anything already configured is served from the same snapshot.
        var wanted = new HashSet<string>(allHandlers.Keys, StringComparer.OrdinalIgnoreCase);
        if (effectiveRelationships == FlsRelationshipMode.Evaluate)
        {
            foreach (var handler in allHandlers.Values)
            {
                foreach (var target in RelationshipTargets(handler).Values)
                    wanted.Add(target);
            }
        }

        var snapshot = await fetcher.FetchAsync(wanted);
        var suspectedFailOpen = new List<string>();

        foreach (var (objectName, handler) in allHandlers)
        {
            var perms = snapshot.Get(objectName);
            if (perms is null)
                continue;

            if (perms.IsSuspectedFailOpen)
                suspectedFailOpen.Add(objectName);

            IReadOnlyDictionary<string, FlsObjectPermissions>? related = null;
            if (effectiveRelationships == FlsRelationshipMode.Evaluate)
            {
                var resolved = new Dictionary<string, FlsObjectPermissions>(StringComparer.OrdinalIgnoreCase);
                foreach (var (relationshipName, targetObject) in RelationshipTargets(handler))
                {
                    if (snapshot.Get(targetObject) is { } targetPerms)
                        resolved[relationshipName] = targetPerms;
                }
                related = resolved;
            }

            var drops = FlsPolicy.ComputeDrops(
                perms,
                handler.SelectedFields.Keys,
                effectiveMode,
                relationshipMode: effectiveRelationships,
                relatedPermissions: related);
            if (drops.Count == 0)
                continue;

            handler.ApplyFlsDrops(drops.Select(d => d.Field));
            summary[objectName] = new FlsObjectDrops(perms.PrincipalsInScope.Count, drops);
            Logger.Info(
                $"[FLS] {objectName}: withholding {drops.Count} field(s) from the index "
                + $"({effectiveMode.ToString().ToLowerInvariant()} mode) — "
                + string.Join(", ", drops.Select(d => d.Field)));
        }

        FlsManifest.Write(
            connectorId, effectiveMode, enforcement: true, summary, logsDir, suspectedFailOpen);
        return summary;
    }

    /// <summary>
    /// Map each relationship name used by this handler's dotted selectedFields to
    /// the Salesforce object it points at.
    ///
    /// <para>Resolution is OPERATOR-DECLARED via the per-object
    /// <c>relationshipObjects</c> map in config/schema.json. It is deliberately not
    /// inferred from the relationship name: guessing wrong means evaluating a field
    /// against the wrong object's permissions, which is the exact under-drop this
    /// work package exists to close. An undeclared relationship simply does not
    /// appear here, and <see cref="FlsPolicy"/> then fails it closed.</para>
    /// </summary>
    private static Dictionary<string, string> RelationshipTargets(Item.SalesforceObjectHandler handler)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in handler.SelectedFields.Keys)
        {
            var separator = key.IndexOf('.', StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            var relationshipName = key[..separator];
            if (handler.RelationshipObjects.TryGetValue(relationshipName, out var target)
                && !string.IsNullOrWhiteSpace(target))
            {
                targets[relationshipName] = target;
            }
        }
        return targets;
    }
}

/// <summary>
/// The audit artifact: <c>logs/fls_manifest_{connectorId}.json</c>.
///
/// Lists, per object, every field dropped and why. The format is deliberately
/// small and stable — a sibling connector (the Hadoop BDH connector, which
/// mirrors the same Salesforce records under a coarser ACL model) consumes it to
/// apply the same drops. Treat the shape below as a contract:
///
/// <code>
/// {
///   "version": 1,
///   "connectorId": "SalesforceCRM",
///   "generatedAt": "2026-07-19T12:00:00.0000000Z",
///   "enforcement": true,
///   "mode": "strict",
///   "objects": {
///     "Account": {
///       "principalsInScope": 2,
///       "dropped": [ { "field": "Compensation__c", "reason": "strict: readable by 1 of 2 principal(s) in scope" } ]
///     }
///   }
/// }
/// </code>
///
/// Objects and dropped fields are sorted so successive runs diff meaningfully.
/// </summary>
public static class FlsManifest
{
    /// <summary>Current manifest schema version. Bump only on a breaking shape change.</summary>
    public const int Version = 1;

    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine.fls");

    /// <summary>Write the manifest and return its full path.</summary>
    public static string Write(
        string connectorId,
        FlsMode mode,
        bool enforcement,
        IReadOnlyDictionary<string, FlsObjectDrops> drops,
        string? logsDir = null,
        IEnumerable<string>? suspectedFailOpenObjects = null)
    {
        ArgumentNullException.ThrowIfNull(drops);

        var dir = logsDir ?? Path.GetFullPath("logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"fls_manifest_{connectorId}.json");

        var objects = new JsonObject();
        foreach (var objectName in drops.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var entry = drops[objectName];
            var dropped = new JsonArray();
            foreach (var drop in entry.Drops.OrderBy(d => d.Field, StringComparer.Ordinal))
            {
                dropped.Add(new JsonObject
                {
                    ["field"] = drop.Field,
                    ["reason"] = drop.Reason,
                });
            }
            objects[objectName] = new JsonObject
            {
                ["principalsInScope"] = entry.PrincipalsInScope,
                ["dropped"] = dropped,
            };
        }

        // Additive key (manifest stays version 1): objects whose FieldPermissions
        // came back empty in a way that is consistent with a silent fail-OPEN.
        // No drop decision depends on it — it exists so an operator can tell
        // "nothing is governed" apart from "we could not see what is governed".
        var suspects = new JsonArray();
        foreach (var objectName in (suspectedFailOpenObjects ?? Enumerable.Empty<string>())
                     .Where(o => !string.IsNullOrEmpty(o))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(o => o, StringComparer.Ordinal))
        {
            suspects.Add(objectName);
        }

        var doc = new JsonObject
        {
            ["version"] = Version,
            ["connectorId"] = connectorId,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["enforcement"] = enforcement,
            ["mode"] = mode == FlsMode.Permissive ? "permissive" : "strict",
            ["objects"] = objects,
            ["suspectedFailOpen"] = suspects,
        };

        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var total = drops.Values.Sum(d => d.Drops.Count);
        Logger.Info($"[FLS] Audit manifest written: {path} ({total} field(s) dropped across {drops.Count} object(s))");
        if (suspects.Count > 0)
        {
            Logger.Error(
                $"[FLS] {suspects.Count} object(s) flagged as SUSPECTED FAIL-OPEN in {path} — "
                + "FieldPermissions was empty, so no field was dropped on FLS grounds for them.");
        }
        return path;
    }
}
