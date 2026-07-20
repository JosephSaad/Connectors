// Config/SchemaConfig.cs
// ----------------------
// Models + loader for config/schema.json (the BDH object list) and
// config/graph-schema.json (the Graph connection schema properties).
//
// schema.json drives everything object-related: which Salesforce-shaped BDH
// objects are crawled, which fields map to Graph property names, how each
// object's ACL is sourced (aclMode), and where its export lives (sourcePath).

using System.Text.Json;
using System.Text.Json.Serialization;
using HadoopConnector.Item;

namespace HadoopConnector.Config;

/// <summary>What a per-column policy does to a restricted BDH column.</summary>
public enum ColumnPolicyAction
{
    /// <summary>No policy configured — the column is emitted normally.</summary>
    None = 0,

    /// <summary>The column is never emitted: no Graph property, no content line,
    /// no title. Neither the name nor the value reaches the index.</summary>
    Drop,

    /// <summary>The Graph property name / content key is retained but the VALUE
    /// is replaced with <see cref="ColumnPolicy.MaskMarker"/>, so the item shape
    /// stays stable and the restriction is visible to a reader.</summary>
    Mask,
}

/// <summary>
/// Per-COLUMN restriction policies (WP-HD-3).
/// <para>
/// This connector's sensitivity model is per-OBJECT, and its ACL model is
/// coarse (owner / one flat group / public — see docs/ACL_POSTURE.md). Together
/// that means a restricted column (compensation, margin, personal identifiers)
/// reaches the index for everyone who can see the record. columnPolicies is the
/// enforcement MECHANISM for stripping such a column at conversion time.
/// </para>
/// <para>
/// The policy DATA is hand-authored in schema.json today; it is expected to come
/// from Apache Ranger or the Salesforce FLS manifest later. The mechanism is
/// identical either way, which is why it exists ahead of that source.
/// </para>
/// <para>
/// DELIBERATELY only two actions. An ACL-rewriting action (restrictToGroup and
/// friends) is OUT OF SCOPE here: it would interact with the classification
/// ACL-rewrite ordering in IngestPipeline.ApplyClassification, and is deferred
/// until the Ranger work settles the ACL model. Both actions below change only
/// what is EMITTED for a column; neither touches the item ACL.
/// </para>
/// </summary>
public static class ColumnPolicy
{
    public const string Drop = "drop";
    public const string Mask = "mask";

    /// <summary>Fixed replacement for a masked value. Mirrors the
    /// DeadLetterRedaction shape: names/keys survive, values do not.</summary>
    public const string MaskMarker = "[RESTRICTED]";

    internal static readonly string[] ValidActions = { Drop, Mask };

    /// <summary>
    /// BDH columns a policy CANNOT restrict, because the connector is built on
    /// their value rather than merely emitting it.
    /// <para>
    /// Today that is exactly one column: <c>Id</c>. <c>BdhRecord.RawId</c> reads
    /// it (case-insensitively) and it becomes the <c>externalItem.id</c> Graph
    /// indexes under, the <c>Url</c> deep link <c>ItemConverter.BuildUrl</c>
    /// composes, the content title line's fallback when <c>Name</c> is dropped,
    /// the inventory/deletion-sync key and the dead-letter + retry-failed key.
    /// Dropping it does not restrict the record; it makes the record
    /// unindexable, unreconcilable and unretryable. Masking it collides every
    /// row of the object onto one id.
    /// </para>
    /// <para>
    /// So the policy is REJECTED at load rather than accepted and half-applied.
    /// The alternative — silently gating only the Graph property while the value
    /// keeps riding on the id and the Url — is the failure mode this whole
    /// mechanism exists to avoid: a control that reports a restriction it is not
    /// delivering. To keep the id out of the property map, remove the column
    /// from selectedFields; no policy is needed or possible.
    /// </para>
    /// </summary>
    internal static readonly string[] IdentityColumns = { "Id" };

    /// <summary>True when a policy on this column could not be honoured because
    /// the value is load-bearing for item identity (case-insensitive, matching
    /// <see cref="Hdfs.BdhRecord.Get"/>).</summary>
    internal static bool IsIdentityColumn(string column) =>
        IdentityColumns.Contains(column.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Parse a configured action; <c>null</c> when unrecognized (the
    /// caller decides whether that is a config error — it always is at load).</summary>
    internal static ColumnPolicyAction? Parse(string? action) => action switch
    {
        not null when action.Equals(Drop, StringComparison.OrdinalIgnoreCase) => ColumnPolicyAction.Drop,
        not null when action.Equals(Mask, StringComparison.OrdinalIgnoreCase) => ColumnPolicyAction.Mask,
        _ => null,
    };
}

public sealed class SchemaConfig
{
    [JsonPropertyName("objectList")]
    public List<ObjectConfig> ObjectList { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SchemaConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        SchemaConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<SchemaConfig>(json, Options);
        }
        catch (JsonException exc)
        {
            // Malformed JSON, or a value of the wrong TYPE for its member
            // (`"coarseAclAcknowledged": null` / `"objectList": {}` / a number
            // where a string belongs). Every other schema mistake produces an
            // InvalidDataException naming the offending key, and an operator
            // should never have to tell a parse failure apart from a validation
            // failure by the exception type. exc.Path is the JSON path of the
            // offending key, so it is quoted verbatim.
            throw new InvalidDataException(
                $"Schema config '{path}' could not be read"
                + (string.IsNullOrEmpty(exc.Path) ? string.Empty : $" at {exc.Path}")
                + $": {exc.Message}",
                exc);
        }
        if (config is null)
            throw new InvalidDataException($"Could not parse schema config '{path}'.");
        config.Validate(path);
        return config;
    }

    public static string DefaultPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "schema.json");

    internal static readonly string[] ValidAclModes = { "ownerOnly", "group", "public" };

    /// <summary>The aclModes that grant access WIDER than the record owner and
    /// therefore cannot express the source system's row/column restrictions:
    /// 'group' is one flat Entra group for every row of the object, 'public' is
    /// everyoneExceptGuests. Both require an explicit
    /// <see cref="ObjectConfig.CoarseAclAcknowledged"/> attestation to pass
    /// <c>validate-config --strict</c> — see docs/ACL_POSTURE.md.</summary>
    internal static readonly string[] CoarseAclModes = { "group", "public" };

    internal void Validate(string path)
    {
        // FIRST, before anything dereferences the model: replace every null the
        // deserializer wrote over a property initializer with that member's empty
        // value (see ConfigNullNormalizer for the mechanism and the semantics).
        // After this line no member of this model is null, so every check below —
        // and every consumer downstream of Load — reasons about EMPTY, which they
        // already do, instead of about null, which none of them did.
        ConfigNullNormalizer.Normalize(this, path);

        if (ObjectList.Count == 0)
            throw new InvalidDataException($"Schema config '{path}' has an empty objectList.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in ObjectList)
        {
            if (string.IsNullOrWhiteSpace(obj.ObjectName))
                throw new InvalidDataException($"Schema config '{path}': objectName missing.");
            if (!seen.Add(obj.ObjectName))
                throw new InvalidDataException($"Schema config '{path}': duplicate objectName '{obj.ObjectName}'.");
            if (obj.SelectedFields.Count == 0)
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has no selectedFields.");
            if (!ValidAclModes.Contains(obj.AclMode, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has invalid aclMode "
                    + $"'{obj.AclMode}' (expected ownerOnly | group | public).");
            }
            if (string.Equals(obj.AclMode, "group", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(obj.AclGroupId))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' uses aclMode=group "
                    + "but has no aclGroupId (an Entra group object id is required).");
            }
            // An attestation is a sign-off on a SPECIFIC named exposure. On an
            // ownerOnly object it attests to nothing today — and worse, it lies
            // in wait: flip that object to group/public later and the stale
            // 'true' silently pre-approves the widening, so --strict passes and
            // the review this control exists to force never happens. Reject it
            // (the fix is deleting one line).
            if (obj.CoarseAclAcknowledged && !obj.IsCoarseAcl)
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' sets "
                    + $"coarseAclAcknowledged=true but its aclMode is '{obj.AclMode}' (ownerOnly), "
                    + "which is not a coarse grant. Remove the attestation: leaving it in place "
                    + "would pre-approve an over-sharing exposure nobody has reviewed if the "
                    + "aclMode is later widened to group or public.");
            }
            ValidateSelectedFields(path, obj);
            ValidateAttestationBinding(path, obj);
            ValidateColumnPolicies(path, obj);
        }
    }

    /// <summary>
    /// Validate the BINDING between a coarse-ACL attestation and the posture it
    /// signed off on (<c>coarseAclAcknowledgedFor</c>).
    /// <para>
    /// The bare <c>coarseAclAcknowledged</c> bool records only that SOMEONE
    /// signed, never WHAT they signed. That makes it survive every widening of
    /// the exposure it supposedly covers: flip <c>group</c> → <c>public</c>, or
    /// swap <c>aclGroupId</c> for a group ten times the size, and the stale
    /// 'true' silently pre-approves an exposure nobody reviewed. Binding the
    /// attestation to the posture token turns any change of posture into a
    /// config contradiction, which forces the fresh human review the control
    /// exists for.
    /// </para>
    /// <para>
    /// A MISMATCH is a hard load error, not merely a <c>--strict</c> failure:
    /// the crawl reads this same file, and a config that asserts something false
    /// about its own authorisation posture must not run at all. A MISSING
    /// binding is deliberately NOT a load error (older configs still parse) but
    /// does not satisfy <c>--strict</c> — see <c>ValidateConfig</c>.
    /// </para>
    /// </summary>
    private static void ValidateAttestationBinding(string path, ObjectConfig obj)
    {
        var attestedFor = obj.CoarseAclAcknowledgedFor.Trim();
        if (attestedFor.Length == 0)
            return;

        // Same reasoning as the ownerOnly rule for the bool: a binding left
        // behind on a narrowed object is a loaded gun aimed at the next widening.
        if (!obj.IsCoarseAcl)
        {
            throw new InvalidDataException(
                $"Schema config '{path}': object '{obj.ObjectName}' sets "
                + $"coarseAclAcknowledgedFor='{attestedFor}' but its aclMode is '{obj.AclMode}' "
                + "(ownerOnly), which is not a coarse grant. Remove the attestation entirely: "
                + "leaving it in place would pre-approve an over-sharing exposure nobody has "
                + "reviewed if the aclMode is later widened to group or public.");
        }

        if (!obj.CoarseAclAcknowledged)
        {
            throw new InvalidDataException(
                $"Schema config '{path}': object '{obj.ObjectName}' sets "
                + $"coarseAclAcknowledgedFor='{attestedFor}' without "
                + "\"coarseAclAcknowledged\": true. A binding with no sign-off is a half-deleted "
                + "attestation, not an attestation: set both, or remove both.");
        }

        if (!IsWellFormedPostureToken(attestedFor))
        {
            throw new InvalidDataException(
                $"Schema config '{path}': object '{obj.ObjectName}' has an unrecognized "
                + $"coarseAclAcknowledgedFor value '{attestedFor}'. It must name the posture that "
                + "was actually reviewed, verbatim: \"public\", or \"group:<aclGroupId>\". This "
                + $"object's effective posture is '{obj.CoarseAclPostureToken}'.");
        }

        if (!string.Equals(attestedFor, obj.CoarseAclPostureToken, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Schema config '{path}': object '{obj.ObjectName}' carries a coarse-ACL "
                + $"attestation signed for posture '{attestedFor}', but its EFFECTIVE posture is "
                + $"now '{obj.CoarseAclPostureToken}'. The sign-off does not cover the posture "
                + "this object is actually in, so it is void — the exposure has changed and nobody "
                + "has reviewed the new one. Re-review the object and, if the new posture is "
                + $"accepted, set \"coarseAclAcknowledgedFor\": \"{obj.CoarseAclPostureToken}\". "
                + "(Any change is treated as needing review, in both directions: this connector "
                + "cannot know whether one Entra group is broader than another.)");
        }
    }

    /// <summary>True for a syntactically valid posture token: <c>public</c> or
    /// <c>group:&lt;non-empty id&gt;</c>.</summary>
    private static bool IsWellFormedPostureToken(string token) =>
        token.Equals("public", StringComparison.OrdinalIgnoreCase)
        || (token.StartsWith("group:", StringComparison.OrdinalIgnoreCase) && token.Length > "group:".Length);

    /// <summary>
    /// Validate one object's selectedFields MAPPING — the column → Graph property
    /// map every other surface is built out of.
    /// <para>
    /// An empty column name or an empty property name (which is what a JSON
    /// <c>null</c> value normalises to — see <see cref="ConfigNullNormalizer"/>)
    /// names nothing on either side: the column is never read and the property
    /// cannot be registered in graph-schema.json. Rejected, naming the entry.
    /// </para>
    /// <para>
    /// TWO COLUMNS MAPPED TO ONE PROPERTY is rejected for the same reason the
    /// duplicate columnPolicies rejection exists: only ONE of them can occupy
    /// <c>externalItem.properties[name]</c>, the winner is whichever the
    /// dictionary happens to enumerate last, and the loser's value — along with
    /// any drop/mask policy protecting it — is silently discarded. Concretely,
    /// <c>{"Salary":"Comp","Bonus":"Comp"}</c> with <c>{"Salary":"mask"}</c> wrote
    /// <c>[RESTRICTED]</c> into <c>Comp</c> and then overwrote it with Bonus's
    /// real value, while preflight went on printing <c>masked=[Salary]</c>. Same
    /// family as the identity-column, unselected-column and colliding-property
    /// rejections: a report of a restriction the item does not deliver.
    /// </para>
    /// <para>
    /// <c>_bdh_</c> values are exempt because they are not property names at all:
    /// they route the column into the content BODY under its own column name (see
    /// <c>ItemConverter.BuildContent</c>), so two columns carrying the same
    /// placeholder produce two distinct content lines and overwrite nothing.
    /// </para>
    /// </summary>
    private static void ValidateSelectedFields(string path, ObjectConfig obj)
    {
        // Case-insensitive and trimmed on the property side, matching
        // CollidingPropertySource and the graph-schema lookup in ValidateConfig:
        // a collision a casing difference hid would defeat the rejection with one
        // letter, and Graph property names are not case-sensitive.
        var seenProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, property) in obj.SelectedFields)
        {
            if (string.IsNullOrWhiteSpace(column))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has a selectedFields entry "
                    + $"with an empty column name (mapped to '{property}'). A BDH column with no "
                    + "name is never read, so the property would be emitted empty on every record. "
                    + "Name the column or remove the entry.");
            }
            if (string.IsNullOrWhiteSpace(property))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' maps selectedFields column "
                    + $"'{column}' to an empty Graph property name. (A JSON null reads as empty "
                    + "everywhere in this file.) A property with no name cannot be declared in "
                    + "graph-schema.json or indexed, so the column would be read and thrown away. "
                    + $"Give '{column}' a property name, or remove the entry.");
            }
            // Not a Graph property: a content-body placeholder keyed by the
            // COLUMN name, so it cannot collide with anything.
            if (property.StartsWith("_bdh_", StringComparison.Ordinal))
                continue;
            var normalized = property.Trim();
            // DIRECTION (b) of the standard-property hole: a column mapped ONTO a
            // property ItemConverter already emits by itself. Loop 1 of Convert
            // runs AFTER the standard properties are set, so the mapped column
            // simply overwrites one — `{"Comp":"Url"}` with no Comp column in the
            // record replaced the deep link with null and every item lost its Url;
            // add `{"Comp":"mask"}` and every item's Url became the mask marker
            // instead. Preflight was green in both cases. Same family as the
            // two-columns-one-property rejection just below: only one value can
            // occupy that property and the loser is silently discarded — here the
            // loser is a structural property nobody wrote in selectedFields, so
            // nothing in the config even hints at what was lost.
            if (StandardProperty(normalized) is { } standard)
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' maps selectedFields column "
                    + $"'{column}' to the Graph property '{normalized}', which this connector emits "
                    + "on EVERY item by itself (the standard properties are "
                    + $"{string.Join(", ", AlwaysEmittedProperties.Names)}; "
                    + $"'{SensitivityClassifier.LabelProperty}' and "
                    + $"'{SensitivityClassifier.CategoriesProperty}' are written by the classifier "
                    + "after conversion, whenever CLASSIFICATION=true). The mapped column "
                    + $"overwrites the standard '{standard}' on every record — so the value the "
                    + "connector guarantees for that property is destroyed, silently, and a "
                    + $"drop/mask policy on '{column}' would rewrite '{standard}' on every item "
                    + $"rather than restrict a column. Map '{column}' to a property name of its own.");
            }
            if (seenProperties.TryGetValue(normalized, out var firstColumn))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' maps TWO selectedFields "
                    + $"columns — '{firstColumn}' and '{column}' — to the same Graph property "
                    + $"'{normalized}'. Only one of them can occupy that property on the item, and "
                    + "which one wins is dictionary-order luck; the other column's value, and any "
                    + "columnPolicies drop/mask protecting it, is silently discarded while "
                    + "validate-config still reports the policy as applied. Map each column to its "
                    + "own property name (property names are matched case-insensitively).");
            }
            seenProperties[normalized] = column;
        }
    }

    /// <summary>
    /// Validate one object's columnPolicies. Both rejections here exist because
    /// the silent-failure mode of a restriction control is the dangerous one: a
    /// policy that does not apply leaves the column fully indexed while the
    /// config reads as though it is protected.
    /// </summary>
    private static void ValidateColumnPolicies(string path, ObjectConfig obj)
    {
        // Case-insensitive, because BdhRecord.Get and the enforcement lookup are
        // too — 'comp__c' and 'Comp__c' name the SAME column, so two such keys
        // with different actions would make the winner dictionary-order luck.
        var seenColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, action) in obj.ColumnPolicies)
        {
            if (string.IsNullOrWhiteSpace(column))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has a columnPolicies entry "
                    + "with an empty column name.");
            }
            if (ColumnPolicy.Parse(action) is null)
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has an invalid "
                    + $"columnPolicies action '{action}' for column '{column}' (expected "
                    + $"{ColumnPolicy.Drop} | {ColumnPolicy.Mask}). Note that an ACL-rewriting "
                    + "action is deliberately not supported — a column policy changes only what "
                    + "is emitted for that column, never who the item is granted to.");
            }
            if (seenColumns.TryGetValue(column, out var first))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has duplicate "
                    + $"columnPolicies entries '{first}' and '{column}', which differ only in case "
                    + "and therefore name the same column (lookup is case-insensitive). Keep one, "
                    + "so which restriction applies is never dictionary-order luck.");
            }
            seenColumns[column] = column;

            // A policy on the IDENTITY column cannot be honoured: the value is
            // the externalItem id and the Url built from it, both of which are
            // structural. Gating only the Graph property would leave the value
            // fully indexed while validate-config announced it as restricted —
            // exactly the silent-failure mode this validation exists to stop.
            if (ColumnPolicy.IsIdentityColumn(column))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has a columnPolicies entry "
                    + $"for the identity column '{column}', which cannot be restricted. That value "
                    + "IS the externalItem id Graph indexes the record under, the 'Url' deep link "
                    + "built from it, the content title fallback, and the key used by inventory, "
                    + "deletion sync, the dead-letter queue and retry-failed — so a drop/mask would "
                    + "strip only the Graph property while the value stayed in the index by three "
                    + "other routes, and the policy would report a restriction that is not real. "
                    + $"Remove '{column}' from this object's selectedFields if it should not be a "
                    + "searchable property, and delete the policy. To restrict who can SEE the "
                    + "record, narrow its aclMode (see docs/ACL_POSTURE.md); to stop indexing it at "
                    + "all, filter it out in filters.json.");
            }

            // A policy on an unselected column restricts nothing at all: the
            // column is not read, and the typo'd real column stays unrestricted.
            if (!obj.SelectedFields.Keys.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has a columnPolicies entry "
                    + $"for column '{column}', which is not in that object's selectedFields. The "
                    + "policy would silently restrict nothing while the column it was meant to "
                    + "cover stays fully indexed. Fix the column name or remove the policy.");
            }

            // A policed column whose NAME is the Graph property that a DIFFERENT
            // column maps to. The policy itself applies correctly — but every
            // report of it names the COLUMN, and a property of that same name
            // goes on reaching the index from the other column, so preflight
            // prints "dropped=[RecordId]" over a populated, indexed RecordId
            // property. Same family as the two rejections above: the restriction
            // cannot be read off the report as true, and the reader who most
            // needs to trust it is the one who wrote 'RecordId' meaning the
            // property rather than the column.
            // DIRECTION (a) of the standard-property hole. Exactly the rejection
            // below, except the property of that name comes from ItemConverter's
            // own standard set rather than from another selectedFields entry:
            // policing a column called 'Url' makes preflight print "dropped=[Url]"
            // while a populated, indexed Url property rides on every item. The
            // restricted column's VALUE does not leak — but the report cannot be
            // read as true, which is the whole point of these rejections.
            if (StandardProperty(column) is { } standardProperty)
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has a columnPolicies entry "
                    + $"for column '{column}', but '{standardProperty}' is ALSO a Graph property "
                    + "this connector emits on EVERY item by itself, independently of "
                    + $"selectedFields (the standard properties are "
                    + $"{string.Join(", ", AlwaysEmittedProperties.Names)}; "
                    + $"'{SensitivityClassifier.LabelProperty}' and "
                    + $"'{SensitivityClassifier.CategoriesProperty}' are written by the classifier "
                    + "after conversion, whenever CLASSIFICATION=true). The policy "
                    + $"restricts the BDH column, while the property '{standardProperty}' carries "
                    + "on being populated and indexed for every record — so preflight would report "
                    + $"'{column}' as dropped/masked with an indexed property of exactly that name "
                    + "still in place, and nobody reading that report could tell which of the two "
                    + $"the restriction covered. Rename the column mapping so '{column}' is not "
                    + "policed under a standard property's name, or remove the policy.");
            }

            if (CollidingPropertySource(obj, column) is { } source)
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has a columnPolicies entry "
                    + $"for column '{column}', but '{column}' is ALSO the Graph property that the "
                    + $"different column '{source}' maps to in selectedFields. The policy restricts "
                    + $"the column, while the Graph property '{column}' is still emitted for every "
                    + $"record of this object from '{source}' — so preflight would report "
                    + $"'{column}' as restricted while an indexed property of exactly that name "
                    + "carried on being populated, and nobody reading that report could tell which "
                    + "of the two the restriction covered. Rename the Graph property that "
                    + $"'{source}' maps to so no column shares a name with another column's "
                    + $"property, or — if the intent was to restrict what '{source}' exposes — "
                    + $"policy '{source}' instead.");
            }
        }
    }

    /// <summary>
    /// The OTHER selectedFields column that maps this policed column's name to a
    /// Graph property, or <c>null</c> when there is no such collision.
    /// <para>
    /// Case-insensitive on both sides, matching the column lookup in
    /// <c>BdhRecord.Get</c> and the property lookup in <c>ValidateConfig</c> —
    /// a collision that a casing difference hid would defeat the rejection with
    /// one letter. A column mapped to a property of its OWN name is the ordinary
    /// case and is skipped. So is a <c>_bdh_</c> placeholder, which is not a
    /// Graph property name at all but a marker routing the column into the
    /// content body under its own COLUMN name, and therefore cannot collide.
    /// </para>
    /// <para>
    /// A colliding property whose source column is ITSELF dropped never reaches
    /// the index, so the report is accurate and there is nothing to reject. Mask
    /// does not qualify: a masked column still emits a property of that name
    /// (carrying the marker), which is exactly the reading the report confuses.
    /// </para>
    /// </summary>
    /// <summary>
    /// The standard Graph property name matching <paramref name="name"/>, or
    /// <c>null</c> when it is not one. Sourced from
    /// <see cref="AlwaysEmittedProperties.Names"/> — the aggregate every emitter
    /// contributes its own list to — so this check cannot fall behind a change
    /// in any one of them. Reading a SINGLE emitter's list here is exactly the
    /// defect that let SensitivityClassifier's two properties through.
    /// Case-insensitive and trimmed, matching every other property comparison in
    /// this file: a collision one letter of casing could hide is not a rejection.
    /// </summary>
    private static string? StandardProperty(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var normalized = name.Trim();
        return AlwaysEmittedProperties.Names.FirstOrDefault(
            standard => string.Equals(standard, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string? CollidingPropertySource(ObjectConfig obj, string column)
    {
        var policed = column.Trim();
        foreach (var (sourceColumn, property) in obj.SelectedFields)
        {
            if (string.IsNullOrWhiteSpace(property)
                || property.StartsWith("_bdh_", StringComparison.Ordinal)
                || string.Equals(sourceColumn.Trim(), policed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(property.Trim(), policed, StringComparison.OrdinalIgnoreCase)
                && obj.PolicyFor(sourceColumn) != ColumnPolicyAction.Drop)
            {
                return sourceColumn;
            }
        }
        return null;
    }

    public ObjectConfig? FindObject(string objectName) =>
        ObjectList.FirstOrDefault(o =>
            string.Equals(o.ObjectName, objectName, StringComparison.OrdinalIgnoreCase));
}

public sealed class ObjectConfig
{
    /// <summary>Salesforce-shaped BDH object name (Contact, Account, Opportunity, Case, Lead, ...).</summary>
    [JsonPropertyName("objectName")]
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>Human-readable label used in result cards / logs.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>BDH column name → Graph property name. Fields prefixed
    /// <c>_bdh_</c> on the value side are read but consumed by computed
    /// properties (content body) rather than set directly.</summary>
    [JsonPropertyName("selectedFields")]
    public Dictionary<string, string> SelectedFields { get; set; } = new();

    /// <summary>
    /// Per-COLUMN restriction policies: BDH column name → <c>drop</c> | <c>mask</c>
    /// (see <see cref="ColumnPolicy"/>). Absent/empty ⇒ no column is restricted
    /// and item conversion is byte-identical to a config without the key.
    /// <para>
    /// Every key must name a column in <see cref="SelectedFields"/> and every
    /// value must be a known action; <see cref="SchemaConfig.Validate"/> rejects
    /// both violations at load, because a policy that silently does nothing is
    /// worse than no policy at all.
    /// </para>
    /// </summary>
    [JsonPropertyName("columnPolicies")]
    public Dictionary<string, string> ColumnPolicies { get; set; } = new();

    /// <summary>ACL sourcing mode: ownerOnly (default), group, public.
    /// BDH has no Salesforce sharing tables, so this is deliberately coarse —
    /// see docs and README for the trade-off vs the live Salesforce connector.</summary>
    [JsonPropertyName("aclMode")]
    public string AclMode { get; set; } = "ownerOnly";

    /// <summary>Entra group object id granted access when aclMode=group.</summary>
    [JsonPropertyName("aclGroupId")]
    public string AclGroupId { get; set; } = string.Empty;

    /// <summary>
    /// INTERIM CONTROL (pending the Apache Ranger integration). Explicit,
    /// in-file human attestation that this object's COARSE authorisation posture
    /// — aclMode 'group' (one flat Entra group for every row) or 'public'
    /// (everyoneExceptGuests) — has been reviewed and the resulting over-sharing
    /// exposure is knowingly accepted.
    /// <para>
    /// This does NOT reduce the exposure and does NOT silence the warning:
    /// validate-config still reports the posture on every run. It only records
    /// that a human signed off, which is what <c>--strict</c> gates on. Absent
    /// (or false) on a coarse object ⇒ <c>--strict</c> FAILS.
    /// </para>
    /// Only meaningful on a coarse object; SchemaConfig.Validate rejects it on
    /// ownerOnly so a stale 'true' can never pre-approve a later widening.
    /// </summary>
    [JsonPropertyName("coarseAclAcknowledged")]
    public bool CoarseAclAcknowledged { get; set; }

    /// <summary>
    /// WHAT the <see cref="CoarseAclAcknowledged"/> sign-off actually covers:
    /// the object's posture token at the moment it was reviewed — <c>public</c>
    /// or <c>group:&lt;aclGroupId&gt;</c> (see <see cref="CoarseAclPostureToken"/>).
    /// <para>
    /// Without this, the attestation is an unbound bool that records only that
    /// SOMEONE signed, and therefore survives every widening of the exposure it
    /// supposedly covers. With it, changing aclMode or aclGroupId voids the
    /// sign-off: the mismatch is a hard config-load error, so a widened posture
    /// forces a fresh review instead of inheriting the old one.
    /// </para>
    /// Empty = an UNBOUND attestation: still parses (older configs load), but it
    /// does not satisfy <c>validate-config --strict</c>.
    /// <para>
    /// NORMALISED TO EMPTY ON SET, because <c>"coarseAclAcknowledgedFor": null</c>
    /// is valid JSON and deserializes to a null reference. A JSON null is how
    /// JSON spells "absent", and an absent binding is deliberately not a load
    /// error — so null must land in exactly the same UNBOUND state rather than
    /// crash the config load. Normalising here rather than at one call site is
    /// deliberate: this property is dereferenced from BOTH
    /// <see cref="SchemaConfig.Validate"/> and
    /// <see cref="HasBoundCoarseAclAttestation"/> (which runs later, from
    /// validate-config), so guarding only the validator would leave the second
    /// crash live on a config that now loads.
    /// </para>
    /// </summary>
    [JsonPropertyName("coarseAclAcknowledgedFor")]
    public string CoarseAclAcknowledgedFor
    {
        get => _coarseAclAcknowledgedFor;
        set => _coarseAclAcknowledgedFor = value ?? string.Empty;
    }

    private string _coarseAclAcknowledgedFor = string.Empty;

    /// <summary>Record field carrying the Salesforce owner user id (aclMode=ownerOnly).</summary>
    [JsonPropertyName("ownerField")]
    public string OwnerField { get; set; } = string.Empty;

    /// <summary>Record field carrying the owner's email (fallback resolution).</summary>
    [JsonPropertyName("ownerEmailField")]
    public string OwnerEmailField { get; set; } = string.Empty;

    /// <summary>Sub-directory of the BDH root holding this object's partitions;
    /// defaults to the object name.</summary>
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Result-card icon URL.</summary>
    [JsonPropertyName("iconUrl")]
    public string IconUrl { get; set; } = string.Empty;

    /// <summary>Per-object baseline sensitivity label (Public | Internal |
    /// Confidential | Restricted). The unified classifier floors the derived
    /// label at this default; empty → Internal. See docs/CLASSIFICATION.md.</summary>
    [JsonPropertyName("sensitivityDefault")]
    public string SensitivityDefault { get; set; } = string.Empty;

    /// <summary>True when this object's aclMode grants access wider than the
    /// record owner ('group' or 'public') and therefore cannot express the row/
    /// column restrictions the source system enforces.</summary>
    [JsonIgnore]
    public bool IsCoarseAcl =>
        SchemaConfig.CoarseAclModes.Contains(AclMode, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True only when a sign-off exists AND names the posture this object is
    /// ACTUALLY in. This — not the bare bool — is what <c>--strict</c> gates on:
    /// an unbound 'true' attests to nothing identifiable and would silently
    /// carry over the next time the exposure widens.
    /// </summary>
    [JsonIgnore]
    public bool HasBoundCoarseAclAttestation =>
        CoarseAclAcknowledged
        && IsCoarseAcl
        && CoarseAclAcknowledgedFor.Trim().Length > 0
        && string.Equals(
            CoarseAclAcknowledgedFor.Trim(), CoarseAclPostureToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical string naming this object's EFFECTIVE coarse posture:
    /// <c>public</c>, or <c>group:&lt;aclGroupId&gt;</c>. Empty when the object
    /// is not coarse.</summary>
    [JsonIgnore]
    public string CoarseAclPostureToken =>
        string.Equals(AclMode, "public", StringComparison.OrdinalIgnoreCase) ? "public"
        : string.Equals(AclMode, "group", StringComparison.OrdinalIgnoreCase)
            ? $"group:{AclGroupId.Trim()}"
            : string.Empty;

    /// <summary>
    /// Case-insensitive column → action lookup, built once on first use.
    /// <para>
    /// Case-insensitive to match <c>BdhRecord.Get</c> (BDH exports vary in column
    /// casing): a policy that missed because of casing would silently leave the
    /// column indexed. Cached because <c>ItemConverter</c> consults it per field
    /// per record — 150M+ rows at BDH scale. Schema config is loaded once and
    /// treated as immutable thereafter, so the cache is built lazily and never
    /// invalidated; mutating <see cref="ColumnPolicies"/> after the first
    /// conversion is not supported.
    /// </para>
    /// </summary>
    private readonly Lazy<IReadOnlyDictionary<string, ColumnPolicyAction>> _policyLookup;

    public ObjectConfig()
    {
        _policyLookup = new Lazy<IReadOnlyDictionary<string, ColumnPolicyAction>>(BuildPolicyLookup);
    }

    private IReadOnlyDictionary<string, ColumnPolicyAction> BuildPolicyLookup()
    {
        var lookup = new Dictionary<string, ColumnPolicyAction>(
            ColumnPolicies.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (column, action) in ColumnPolicies)
        {
            // An unparseable action cannot reach here through Load (Validate
            // throws first), but a hand-built config could. FAIL CLOSED: treat
            // anything unrecognized as Drop rather than emitting the value.
            if (!string.IsNullOrWhiteSpace(column))
                lookup[column] = ColumnPolicy.Parse(action) ?? ColumnPolicyAction.Drop;
        }
        return lookup;
    }

    /// <summary>The policy for one BDH column (case-insensitive);
    /// <see cref="ColumnPolicyAction.None"/> when unrestricted.</summary>
    public ColumnPolicyAction PolicyFor(string column) =>
        ColumnPolicies.Count == 0
            ? ColumnPolicyAction.None
            : _policyLookup.Value.GetValueOrDefault(column, ColumnPolicyAction.None);

    /// <summary>True when this object restricts at least one column.</summary>
    [JsonIgnore]
    public bool HasColumnPolicies => ColumnPolicies.Count > 0;

    /// <summary>Columns never emitted for this object.</summary>
    [JsonIgnore]
    public IEnumerable<string> DroppedColumns => ColumnsWith(ColumnPolicyAction.Drop);

    /// <summary>Columns whose value is replaced by the redaction marker.</summary>
    [JsonIgnore]
    public IEnumerable<string> MaskedColumns => ColumnsWith(ColumnPolicyAction.Mask);

    [JsonIgnore]
    public int DroppedColumnCount => DroppedColumns.Count();

    [JsonIgnore]
    public int MaskedColumnCount => MaskedColumns.Count();

    private IEnumerable<string> ColumnsWith(ColumnPolicyAction action) =>
        ColumnPolicies.Count == 0
            ? Array.Empty<string>()
            : _policyLookup.Value
                .Where(kv => kv.Value == action)
                .Select(kv => kv.Key)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string EffectiveOwnerField =>
        string.IsNullOrWhiteSpace(OwnerField) ? "OwnerId" : OwnerField;

    [JsonIgnore]
    public string EffectiveOwnerEmailField =>
        string.IsNullOrWhiteSpace(OwnerEmailField) ? "OwnerEmail" : OwnerEmailField;

    /// <summary>Graph property names for the selected fields, excluding
    /// _bdh_-prefixed computed placeholders.
    /// <para>
    /// Entries with no property name are excluded rather than dereferenced. A
    /// config that came through <see cref="SchemaConfig.Load"/> can no longer
    /// contain one (the value is normalised and then rejected at load), but this
    /// type is also constructed by hand — by tests, by tools and by any future
    /// caller — and a computed member that NREs on an empty map is how the
    /// original defect reached the crawl.
    /// </para></summary>
    [JsonIgnore]
    public IEnumerable<KeyValuePair<string, string>> DirectPropertyFields =>
        SelectedFields.Where(kv =>
            !string.IsNullOrWhiteSpace(kv.Value)
            && !kv.Value.StartsWith("_bdh_", StringComparison.Ordinal));
}
