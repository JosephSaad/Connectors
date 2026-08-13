// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/EnvFlags.cs
// --------------------------
// Command-layer feature gates read from environment variables. Uses the same
// truthy semantics as Seismic.Settings.BoolEnv (true / 1 / yes, case-insensitive)
// so behavior is consistent across the codebase. Every flag defaults to OFF, so an
// unset environment reproduces the original single-node, file-backed behavior exactly.

using System.Globalization;

namespace Connector.Chassis;

/// <summary>Boolean feature flags read from the environment for the command layer.</summary>
public static class EnvFlags
{
    /// <summary>Integer env var with a default for unset/unparseable values.</summary>
    public static int GetInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

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
    /// True only when the named env var is EXPLICITLY <c>false</c>/<c>0</c>/<c>no</c>
    /// (case-insensitive). Unset, blank, or anything unrecognised is not false.
    /// </summary>
    /// <remarks>
    /// For default-ON switches, written as <c>!IsFalse("CIRCUIT_BREAKER")</c>. This is
    /// deliberately NOT the same as <c>IsTrueOrDefault(name, true)</c>, and the
    /// difference is the whole reason it exists: given an unrecognised value such as
    /// a typo, <c>IsTrueOrDefault</c> returns false and <c>!IsFalse</c> returns true.
    /// For a protective default like the circuit breaker, a mistyped env var must not
    /// be what silently switches it off — only a deliberate, recognised "off" should.
    /// </remarks>
    public static bool IsFalse(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw is not null && raw.Trim().ToLowerInvariant() is "false" or "0" or "no";
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
