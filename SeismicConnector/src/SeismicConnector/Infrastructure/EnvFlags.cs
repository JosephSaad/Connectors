// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/EnvFlags.cs
// --------------------------
// Command-layer feature gates read from environment variables. Uses the same
// truthy semantics as Seismic.Settings.BoolEnv (true / 1 / yes, case-insensitive)
// so behavior is consistent across the codebase. Every flag defaults to OFF, so an
// unset environment reproduces the original single-node, file-backed behavior exactly.

namespace SeismicConnector.Infrastructure;

/// <summary>Boolean feature flags read from the environment for the command layer.</summary>
public static class EnvFlags
{
    /// <summary>True when the named env var is <c>true</c>/<c>1</c>/<c>yes</c> (case-insensitive).</summary>
    public static bool IsTrue(string name)
    {
        var value = (Environment.GetEnvironmentVariable(name) ?? "false").ToLowerInvariant();
        return value is "true" or "1" or "yes";
    }

    /// <summary>
    /// True when the named env var is truthy, defaulting to <paramref name="fallback"/>
    /// when unset/blank. (An explicit <c>false</c>/<c>0</c>/<c>no</c> always wins.)
    /// </summary>
    public static bool IsTrueOrDefault(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return raw.Trim().ToLowerInvariant() is "true" or "1" or "yes";
    }

    /// <summary>
    /// <c>IDENTITY_SYNC_ON_INCREMENTAL</c> — run the (incremental) identity crawl on
    /// incremental content crawls too, not just full crawls. DEFAULT TRUE:
    /// re-syncing entitlements every incremental shrinks the ACL-staleness lag
    /// from the full-crawl cadence (hours–day) down to the incremental cadence.
    /// Note the residual lag is still non-real-time: a permission change is only
    /// reflected at the next incremental crawl (and re-ACL of already-indexed
    /// items needs PERMISSION_REACL / the `reacl` sweep, scheduled on a cadence).
    /// Set the env var to false to restore the old full-crawl-only behaviour.
    /// </summary>
    public static bool IdentitySyncOnIncremental =>
        IsTrueOrDefault("IDENTITY_SYNC_ON_INCREMENTAL", fallback: true);
}
