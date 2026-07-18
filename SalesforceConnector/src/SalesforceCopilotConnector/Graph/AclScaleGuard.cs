// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/AclScaleGuard.cs
// ----------------------
// #9 — large-group ACL scale guard (Salesforce-specific).
//
// The default legacy engine (USE_GROUP_ACL=false) expands Salesforce sharing
// into a flat list of per-USER ACEs. For a record shared to a large role,
// public group, queue, or an org-wide group, that expansion can produce
// thousands of ACEs on a single item — and a Microsoft Graph external item has
// a hard ~4 MB size ceiling and a bounded per-item ACL. An item whose ACL blows
// past that ceiling is rejected by Graph (or silently truncated), which is both
// an ingest failure and a potential OVER-permission if truncation drops deny
// entries.
//
// This guard:
//   * records a per-item ACE-count metric for EVERY item (max_item_ace_count),
//     so the ceiling risk is observable even before it bites; and
//   * when ACL_MAX_ACES is set and an item's ACE count exceeds it, FIRES:
//       - action "warn"     (default) — keep the ACL, log a warning + metric.
//       - action "group"    — replace the ACL with a single grant to a
//                             configured Entra group (ACL_SCALE_GUARD_GROUP),
//                             collapsing thousands of user ACEs to one group ACE.
//       - action "fallback" — replace the ACL with deny-everyone (safe fail:
//                             the item is ingested but hidden until re-resolved).
//
// For large orgs the recommended long-term fix is USE_GROUP_ACL=true (group-
// reference ACLs stay small and track membership automatically); this guard is
// the protection for deployments still on the user-expansion engine.
//
// Default UNSET (ACL_MAX_ACES=0) ⇒ metric only, no item ever altered — behavior
// byte-identical to today.

using System.Globalization;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Graph;

/// <summary>Guard actions when an item's ACL exceeds the configured threshold.</summary>
public static class AclScaleGuardActions
{
    public const string Warn = "warn";
    public const string Group = "group";
    public const string Fallback = "fallback";
}

/// <summary>Outcome of applying the scale guard to one item.</summary>
public readonly record struct ScaleGuardDecision(
    List<Dictionary<string, string>>? Acl,
    bool Fired,
    int AceCount,
    string Action);

/// <summary>Per-item ACL size guard protecting the Graph per-item ACL / 4 MB ceiling (#9).</summary>
public static class AclScaleGuard
{
    public const string ThresholdEnvVar = "ACL_MAX_ACES";
    public const string ActionEnvVar = "ACL_SCALE_GUARD_ACTION";
    public const string GroupEnvVar = "ACL_SCALE_GUARD_GROUP";

    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>Threshold ACE count; 0 / unset / unparseable ⇒ guard off (metric only).</summary>
    public static int Threshold
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(ThresholdEnvVar);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : 0;
        }
    }

    /// <summary>Configured action when the guard fires (default "warn").</summary>
    public static string Action
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(ActionEnvVar);
            if (string.IsNullOrWhiteSpace(raw))
                return AclScaleGuardActions.Warn;
            var value = raw.Trim().ToLowerInvariant();
            return value is AclScaleGuardActions.Warn or AclScaleGuardActions.Group or AclScaleGuardActions.Fallback
                ? value
                : AclScaleGuardActions.Warn;
        }
    }

    /// <summary>Entra group id for the "group" action, or null.</summary>
    public static string? Group
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(GroupEnvVar);
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
    }

    /// <summary>Per-crawl snapshot so the per-item hot path does no env lookups.</summary>
    public readonly record struct Policy(int Threshold, string Action, string? Group)
    {
        public bool Enabled => Threshold > 0;

        public static Policy Read() => new(AclScaleGuard.Threshold, AclScaleGuard.Action, AclScaleGuard.Group);
    }

    private static List<Dictionary<string, string>> DenyEveryone() => new()
    {
        new() { ["accessType"] = "deny", ["type"] = "everyone", ["value"] = "everyone" },
    };

    private static List<Dictionary<string, string>> GroupGrant(string groupId) => new()
    {
        new() { ["accessType"] = "grant", ["type"] = "group", ["value"] = groupId },
    };

    /// <summary>
    /// Observe and (when configured) guard one item's ACL. Always records the
    /// ACE-count metric. When the guard is off or the count is within threshold the
    /// ACL is returned unchanged (<c>Fired=false</c>). When it fires, the returned
    /// ACL depends on the action; the caller logs / ledgers the decision.
    /// </summary>
    public static ScaleGuardDecision Apply(
        in Policy policy, string itemId, string objectType, List<Dictionary<string, string>>? acl)
    {
        var count = acl?.Count ?? 0;
        Metrics.ObserveItemAceCount(count);

        if (!policy.Enabled || count <= policy.Threshold)
            return new ScaleGuardDecision(acl, false, count, AclScaleGuardActions.Warn);

        Metrics.IncAclScaleGuardFired();

        switch (policy.Action)
        {
            case AclScaleGuardActions.Group when !string.IsNullOrEmpty(policy.Group):
                Logger.Warning(
                    $"[ACLScaleGuard] {objectType}/{itemId}: ACL has {count} ACE(s) > threshold {policy.Threshold} — " +
                    $"collapsing to group ACL '{policy.Group}' (protects the Graph per-item ACL / 4MB ceiling; " +
                    "USE_GROUP_ACL=true is the durable fix for large orgs).");
                return new ScaleGuardDecision(GroupGrant(policy.Group!), true, count, AclScaleGuardActions.Group);

            case AclScaleGuardActions.Fallback:
                Logger.Warning(
                    $"[ACLScaleGuard] {objectType}/{itemId}: ACL has {count} ACE(s) > threshold {policy.Threshold} — " +
                    "applying deny-everyone fallback (item ingested but hidden until its ACL is re-resolved smaller).");
                return new ScaleGuardDecision(DenyEveryone(), true, count, AclScaleGuardActions.Fallback);

            default:  // warn (also the fallthrough when "group" has no group configured)
                Logger.Warning(
                    $"[ACLScaleGuard] {objectType}/{itemId}: ACL has {count} ACE(s) > threshold {policy.Threshold} — " +
                    "WARN only (ACL unchanged; risk of exceeding the Graph per-item ACL / 4MB ceiling). " +
                    "Consider USE_GROUP_ACL=true, or set ACL_SCALE_GUARD_ACTION=group/fallback.");
                return new ScaleGuardDecision(acl, true, count, AclScaleGuardActions.Warn);
        }
    }

    /// <summary>Convenience overload reading the policy live (used by tests).</summary>
    public static ScaleGuardDecision Apply(string itemId, string objectType, List<Dictionary<string, string>>? acl) =>
        Apply(Policy.Read(), itemId, objectType, acl);
}
