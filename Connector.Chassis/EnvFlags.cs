// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/EnvFlags.cs
// --------------------------
// The fleet's single definition of what a boolean environment variable MEANS.
// Truthy is true/1/yes, falsy is false/0/no, case-insensitive and trimmed —
// the same vocabulary as Seismic.Settings.BoolEnv, which is what this file has
// always claimed parity with.
//
// It did not have it. Before this was consolidated the chassis held SIX
// independent parsers of that vocabulary with THREE different behaviours:
//
//   EnvFlags.IsTrue              did NOT trim          unrecognised -> false
//   EnvFlags.IsTrueOrDefault     trimmed               unrecognised -> false (NOT the fallback)
//   EnvFlags.IsFalse             trimmed               unrecognised -> not-false
//   CircuitBreaker.ReadBool      trimmed               unrecognised -> false
//   EventLogSink.IsEnvTrue       did NOT trim          unrecognised -> false
//   SecretProvider.UseKeyVault   did NOT trim          unrecognised -> false
//
// Two of those cost real safety. `CLASSIFICATION_ENFORCE_ACL=true ` — one
// trailing space, the kind a .env line or a folded YAML scalar produces — read
// as OFF through IsTrue, and, measured end to end, ALSO suppressed the
// AppConfig.Validate() guard that would otherwise have failed the load: ACL
// enforcement silently disabled with validate-config reporting success. And
// CIRCUIT_BREAKER=on left both of Seismic's critical breakers in passthrough,
// the exact failure the IsFalse remark below has always said must not happen.
//
// Everything now routes through Parse. The rules it enforces:
//
//   * TRIM ALWAYS. Whitespace is deployment plumbing, not intent.
//   * AN UNRECOGNISED VALUE IS TREATED AS ABSENT, AND SAYS SO. It carries no
//     information, so inventing one from it is how a typo silently changes
//     behaviour. Falling back to the declared default is the only reading that
//     cannot flip a gate the operator never meant to touch — in either
//     direction — and a one-time warning naming the variable and the value is
//     what turns a silent misconfiguration into a visible one.
//
// The chassis cannot fail hard here: these are read lazily from deep inside a
// crawl, and throwing would take the connector down mid-run over a typo. Making
// a bad value REFUSE TO LOAD belongs in each connector's validate-config, which
// is where the codebase's own rule ("typos must fail validate-config / startup,
// not mid-crawl") puts it. This layer's job is to be honest about what it saw.

using System.Collections.Concurrent;
using System.Globalization;

namespace Connector.Chassis;

/// <summary>Boolean feature flags read from the environment for the command layer.</summary>
public static class EnvFlags
{
    private static readonly ConcurrentDictionary<string, byte> WarnedOnce = new(StringComparer.Ordinal);

    /// <summary>The recognised truthy spellings. Public so a connector's validate-config can name them.</summary>
    public static readonly IReadOnlyList<string> TruthyValues = new[] { "true", "1", "yes" };

    /// <summary>The recognised falsy spellings.</summary>
    public static readonly IReadOnlyList<string> FalsyValues = new[] { "false", "0", "no" };

    /// <summary>
    /// The one parser. <c>true</c>/<c>false</c> for a recognised value, <c>null</c>
    /// when the variable is unset, blank, or holds something outside the vocabulary.
    /// </summary>
    /// <remarks>
    /// Returning null for an unrecognised value — rather than false — is the
    /// behaviour change that matters, and it is deliberately the SAFE direction:
    /// null means "no opinion", so every caller falls back to its own declared
    /// default instead of a typo silently choosing one. Warned once per
    /// (variable, value) so a flag read in a per-item loop cannot flood the log.
    /// </remarks>
    public static bool? Parse(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (raw is null)
            return null;

        var value = raw.Trim().ToLowerInvariant();
        if (value.Length == 0)
            return null;
        if (value is "true" or "1" or "yes")
            return true;
        if (value is "false" or "0" or "no")
            return false;

        if (WarnedOnce.TryAdd($"{name}={value}", 0))
        {
            // Resolved lazily, not in a static field: EnvFlags is read from
            // SqlGateway and HaCoordinator during their own construction, and a
            // logger captured at type-init would fix the host's LoggerFactory
            // seam before the host has had a chance to assign it.
            Chassis.GetLogger(Chassis.Identity.BaseLoggerName).Warning(
                $"{name}='{raw}' is not a recognised boolean "
                + $"({string.Join("/", TruthyValues)} or {string.Join("/", FalsyValues)}, case-insensitive) — "
                + "ignoring it and using the built-in default. If you meant to change this setting, "
                + "the value is not doing anything.");
        }
        return null;
    }

    /// <summary>Integer env var with a default for unset/unparseable values.</summary>
    public static int GetInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    /// <summary>String env var with a default for unset/blank values.</summary>
    /// <remarks>
    /// Blank counts as unset for the same reason blank is null in <see cref="Parse"/>:
    /// an env var set to whitespace carries no intent, and a caller that treated
    /// "   " as a real value would be configured by deployment plumbing.
    /// </remarks>
    public static string GetString(string name, string defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? defaultValue : raw;
    }

    /// <summary>
    /// <c>USE_SQL_SERVER</c> + a non-empty <c>SQL_CONNECTION_STRING</c> — the SQL
    /// Server state backend. Both are required: the flag alone cannot reach a
    /// database, and silently falling back to SQLite on a missing connection
    /// string is the behaviour every connector already relied on.
    /// </summary>
    public static bool UseSqlServer =>
        IsTrue("USE_SQL_SERVER")
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING"));

    /// <summary><c>HA_MODE</c> — active-active multi-node crawling (requires the SQL backend).</summary>
    public static bool HaMode => IsTrue("HA_MODE");

    /// <summary>
    /// True when the named env var is explicitly truthy. Unset, blank and
    /// unrecognised are all false — the right reading for a default-OFF gate.
    /// </summary>
    public static bool IsTrue(string name) => Parse(name) == true;

    /// <summary>
    /// The variable's value, or <paramref name="fallback"/> when it is unset,
    /// blank or unrecognised.
    /// </summary>
    /// <remarks>
    /// Unrecognised now yields the fallback rather than false. Under the old
    /// behaviour this method broke its own name: <c>IsTrueOrDefault(x, true)</c>
    /// returned FALSE for a typo, so it could not be used for a default-ON gate
    /// at all — which is why <see cref="IsFalse"/> was added and why
    /// IdentitySyncOnIncremental, the one caller that did use it that way, was
    /// wrong. Safe to change: it had no other caller anywhere in the fleet.
    /// </remarks>
    public static bool IsTrueOrDefault(string name, bool fallback) => Parse(name) ?? fallback;

    /// <summary>
    /// True only when the named env var is EXPLICITLY <c>false</c>/<c>0</c>/<c>no</c>.
    /// Unset, blank, or anything unrecognised is not false.
    /// </summary>
    /// <remarks>
    /// For default-ON switches, written as <c>!IsFalse("CIRCUIT_BREAKER")</c>: a
    /// protective default must be switched off deliberately, never by a value
    /// nobody meant.
    ///
    /// This is now EQUIVALENT to <c>IsTrueOrDefault(name, true)</c>, where once it
    /// was not — that divergence was the defect, not the design. Both are kept
    /// because they answer different questions and read differently at a call
    /// site: IsFalse asks "did the operator explicitly turn this off", which is
    /// the honest question for a protective default.
    /// </remarks>
    public static bool IsFalse(string name) => Parse(name) == false;

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
    /// <remarks>
    /// Spelled with IsFalse because this is a protective default: leaving ACLs
    /// stale for a full-crawl cycle is the harmful direction, so only an explicit
    /// "false" may choose it. It previously used IsTrueOrDefault(name, true),
    /// which — before the fix above — turned the gate OFF on any typo.
    /// </remarks>
    public static bool IdentitySyncOnIncremental => !IsFalse("IDENTITY_SYNC_ON_INCREMENTAL");

    /// <summary>Test seam: forget which unrecognised values have already been warned about.</summary>
    internal static void ResetWarningsForTests() => WarnedOnce.Clear();
}
