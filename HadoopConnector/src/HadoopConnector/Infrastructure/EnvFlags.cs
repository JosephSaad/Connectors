// Infrastructure/EnvFlags.cs
// --------------------------
// Boolean feature gates read from environment variables. Truthy values are
// true / 1 / yes (case-insensitive); every flag defaults to OFF so an unset
// environment reproduces the single-node, file/SQLite behaviour exactly.

using System.Globalization;

namespace HadoopConnector.Infrastructure;

public static class EnvFlags
{
    /// <summary>True when the named env var is <c>true</c>/<c>1</c>/<c>yes</c> (case-insensitive).</summary>
    public static bool IsTrue(string name)
    {
        var value = (Environment.GetEnvironmentVariable(name) ?? "false").ToLowerInvariant();
        return value is "true" or "1" or "yes";
    }

    /// <summary>Integer env var with a default; invalid values fall back to the default.</summary>
    public static int GetInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    /// <summary>String env var with a default for unset/empty.</summary>
    public static string GetString(string name, string defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? defaultValue : raw;
    }

    /// <summary><c>USE_SQL_SERVER=true</c> + <c>SQL_CONNECTION_STRING</c> — SQL Server state backend.</summary>
    public static bool UseSqlServer =>
        IsTrue("USE_SQL_SERVER")
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING"));

    /// <summary><c>HA_MODE=true</c> — active-active multi-node crawling (requires SQL backend).</summary>
    public static bool HaMode => IsTrue("HA_MODE");

    /// <summary>
    /// <c>IDENTITY_SYNC_ON_INCREMENTAL</c> — re-sync the entitlement (BDH→Entra)
    /// mapping on incremental crawls too, not just on full crawls. Default ON so
    /// entitlement freshness tracks the incremental cadence rather than lagging
    /// to the next full crawl; set <c>false</c> to restrict it to full crawls.
    /// Residual lag remains non-real-time: the mapping refreshes each incremental,
    /// but an item is only re-emitted with an updated ACL when its SOURCE record
    /// changes — items whose records are unchanged keep their prior ACL until the
    /// next FULL crawl, so schedule full crawls at your entitlement-freshness SLA.
    /// </summary>
    public static bool IdentitySyncOnIncremental
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL");
            // Unset → ON (safe default). Explicit false/0/no opts out.
            return string.IsNullOrWhiteSpace(raw)
                || raw.Trim().ToLowerInvariant() is "true" or "1" or "yes";
        }
    }
}
