// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/Classification.cs
// -----------------------
// #6 — connector-applied classification tag (ADVISORY) + optional ACL
// enforcement.
//
// HONESTY (important): this is a CONNECTOR-APPLIED CLASSIFICATION TAG derived
// from a Salesforce field. It is NOT a Microsoft Purview sensitivity label and
// the connector does NOT apply, publish, or enforce any Purview label. By
// itself the tag is advisory metadata — it does not change who can see an item.
// The only access control the connector enforces is the item ACL derived from
// Salesforce's sharing model (OWD / roles / groups / shares), which Graph DOES
// enforce at query time.
//
// OPTIONAL ENFORCEMENT (#6b): when CLASSIFICATION_ENFORCE_ACL=true AND an Entra
// group id is configured (CLASSIFICATION_RESTRICTED_GROUP), items whose tag is
// in the top ("Restricted") tier have their ACL narrowed to a single grant to
// that Entra group — a belt-and-braces control on top of the Salesforce ACL for
// the most sensitive records. Default OFF ⇒ advisory only, ACLs untouched,
// byte-identical to today.
//
// Configuration:
//   CLASSIFICATION_FIELD             Salesforce field on the record whose value is
//                                    the classification tag (unset ⇒ feature off).
//   CLASSIFICATION_RESTRICTED_VALUES CSV of tag values treated as the top tier
//                                    (default "Restricted"; case-insensitive).
//   CLASSIFICATION_ENFORCE_ACL       true ⇒ restrict top-tier item ACLs (default false).
//   CLASSIFICATION_RESTRICTED_GROUP  Entra (AAD) group object id the restricted ACL
//                                    grants to. Required for enforcement to apply.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Graph;

/// <summary>Outcome of evaluating one record against the classification policy.</summary>
public readonly record struct ClassificationDecision(
    List<Dictionary<string, string>>? Acl,
    bool Restricted,
    string? Tag);

/// <summary>Advisory classification tagging + optional ACL enforcement (#6).</summary>
public static class Classification
{
    public const string FieldEnvVar = "CLASSIFICATION_FIELD";
    public const string RestrictedValuesEnvVar = "CLASSIFICATION_RESTRICTED_VALUES";
    public const string EnforceAclEnvVar = "CLASSIFICATION_ENFORCE_ACL";
    public const string RestrictedGroupEnvVar = "CLASSIFICATION_RESTRICTED_GROUP";

    /// <summary>Default top-tier tag value when CLASSIFICATION_RESTRICTED_VALUES is unset.</summary>
    public const string DefaultRestrictedValue = "Restricted";

    private static string? Trimmed(string envVar)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>Salesforce field carrying the tag, or null when the feature is off.</summary>
    public static string? Field => Trimmed(FieldEnvVar);

    /// <summary>True when a classification field is configured (tagging active).</summary>
    public static bool Enabled => Field != null;

    /// <summary>True when top-tier items should have their ACL restricted.</summary>
    public static bool EnforceAcl => EnvFlags.IsTrue(EnforceAclEnvVar);

    /// <summary>Entra (AAD) group object id restricted items are granted to, or null.</summary>
    public static string? RestrictedGroup => Trimmed(RestrictedGroupEnvVar);

    /// <summary>Tag values (case-insensitive) treated as the top "Restricted" tier.</summary>
    public static IReadOnlySet<string> RestrictedValues
    {
        get
        {
            var raw = Trimmed(RestrictedValuesEnvVar);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (raw == null)
            {
                set.Add(DefaultRestrictedValue);
                return set;
            }
            foreach (var value in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(value);
            if (set.Count == 0)
                set.Add(DefaultRestrictedValue);
            return set;
        }
    }

    /// <summary>The advisory tag value for a record (raw field value), or null when off/absent.</summary>
    public static string? TagFor(JsonObject record)
    {
        var field = Field;
        if (field == null)
            return null;
        var value = record[field]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>True when the record's advisory tag is in the top (Restricted) tier.</summary>
    public static bool IsRestricted(JsonObject record)
    {
        var tag = TagFor(record);
        return tag != null && RestrictedValues.Contains(tag);
    }

    /// <summary>
    /// Immutable per-crawl snapshot of the classification settings, read once so the
    /// per-record hot path does no env lookups.
    /// </summary>
    public readonly record struct Policy(
        string? Field,
        bool EnforceAcl,
        string? RestrictedGroup,
        IReadOnlySet<string> RestrictedValues)
    {
        public bool Enabled => Field != null;

        /// <summary>True when enforcement is fully configured (enforce + group + a field).</summary>
        public bool EnforcementActive => Enabled && EnforceAcl && !string.IsNullOrEmpty(RestrictedGroup);

        public static Policy Read() =>
            new(Classification.Field, Classification.EnforceAcl, Classification.RestrictedGroup, Classification.RestrictedValues);

        public string? TagFor(JsonObject record)
        {
            if (Field == null)
                return null;
            var value = record[Field]?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public bool IsRestricted(JsonObject record)
        {
            var tag = TagFor(record);
            return tag != null && RestrictedValues.Contains(tag);
        }
    }

    /// <summary>Graph ACL grant to an Entra (Azure AD) security group.</summary>
    internal static Dictionary<string, string> EntraGroupGrant(string groupId) => new()
    {
        ["accessType"] = "grant",
        ["type"] = "group",   // AAD group (vs. connector-created "externalGroup")
        ["value"] = groupId,
    };

    /// <summary>
    /// Evaluate a record against the policy. Advisory by default: <paramref name="acl"/>
    /// is returned unchanged and <c>Restricted=false</c> unless enforcement is active
    /// AND the record is top-tier, in which case the ACL is replaced with a single
    /// grant to the configured Entra group. The advisory <c>Tag</c> is always surfaced.
    /// </summary>
    public static ClassificationDecision Evaluate(in Policy policy, JsonObject record, List<Dictionary<string, string>>? acl)
    {
        var tag = policy.TagFor(record);
        if (!policy.EnforcementActive || tag == null || !policy.RestrictedValues.Contains(tag))
            return new ClassificationDecision(acl, false, tag);

        var restricted = new List<Dictionary<string, string>> { EntraGroupGrant(policy.RestrictedGroup!) };
        return new ClassificationDecision(restricted, true, tag);
    }

    /// <summary>Convenience overload that reads the policy live (used by tests).</summary>
    public static ClassificationDecision Evaluate(JsonObject record, List<Dictionary<string, string>>? acl) =>
        Evaluate(Policy.Read(), record, acl);
}
