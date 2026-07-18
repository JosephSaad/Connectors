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

    /// <summary><c>IDENTITY_SYNC_ON_INCREMENTAL=true</c> — identity sync on incremental crawls too.</summary>
    public static bool IdentitySyncOnIncremental => IsTrue("IDENTITY_SYNC_ON_INCREMENTAL");
}
