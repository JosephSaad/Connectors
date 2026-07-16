// Seismic/Settings.cs
// -------------------
// Environment-driven configuration for the Seismic Copilot Connector.
//
// Layering (lowest to highest precedence):
//   1. env/.env.local           (non-secret config)
//   2. env/.env.local.user      (SECRET_* values)
//   3. the real process environment (always wins)
//
// Fallback locations ./.env.local and ./.env.local.user in the repo root are
// honoured when the env/ copies do not exist, mirroring the Salesforce
// connector's loader. SECRET_* values are resolved through SecretProvider so
// USE_KEY_VAULT=true can route them to Azure Key Vault.

using System.Globalization;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Seismic;

/// <summary>Thrown when required configuration is missing or invalid.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }
}

public static class Settings
{
    private static readonly object LoadLock = new();
    private static bool _envLoaded;

    /// <summary>Test seam: force the next <see cref="EnsureEnvLoaded"/> to re-read the files.</summary>
    internal static void ResetEnvLoadedForTests()
    {
        lock (LoadLock)
        {
            _envLoaded = false;
        }
    }

    /// <summary>Reserved Microsoft connection-id prefixes (Graph rejects these).</summary>
    internal static readonly string[] ReservedConnectorPrefixes =
    {
        "Microsoft", "None", "Directory", "Exchange", "ExternalEvents", "LinkedIn",
        "Mail", "OfficeGraph", "People", "Powerbi", "SharePoint", "Teams", "Yammer",
        "Connectors", "OneDriveBusiness",
    };

    // ── env file loading ─────────────────────────────────────────────────────

    /// <summary>
    /// Load env/.env.local and env/.env.local.user (or the repo-root fallbacks)
    /// into the process environment. Values already present in the real
    /// environment are never overwritten. Idempotent.
    /// </summary>
    public static void EnsureEnvLoaded(string? baseDir = null)
    {
        lock (LoadLock)
        {
            if (_envLoaded)
                return;
            var root = baseDir ?? Directory.GetCurrentDirectory();
            foreach (var name in new[] { ".env.local", ".env.local.user" })
            {
                var primary = Path.Combine(root, "env", name);
                var fallback = Path.Combine(root, name);
                var path = File.Exists(primary) ? primary : (File.Exists(fallback) ? fallback : null);
                if (path is not null)
                    LoadEnvFile(path);
            }
            _envLoaded = true;
        }
    }

    /// <summary>Parse a dotenv-style file; process-env values win over file values.</summary>
    internal static void LoadEnvFile(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            // Strip a single layer of matching quotes.
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    // ── primitive readers ────────────────────────────────────────────────────

    /// <summary>Required env var; throws <see cref="ConfigException"/> when missing/empty.</summary>
    public static string RequireEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ConfigException($"Invalid configuration: Missing {name}");
        return value.Trim();
    }

    /// <summary>Required SECRET_* var resolved through <see cref="SecretProvider"/>.</summary>
    public static string RequireSecret(string name)
    {
        var value = SecretProvider.GetSecret(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ConfigException($"Invalid configuration: Missing {name}");
        return value.Trim();
    }

    public static string EnvOr(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    public static int IntEnv(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    /// <summary>Truthy semantics shared across the codebase: true / 1 / yes (case-insensitive).</summary>
    public static bool BoolEnv(string name, bool fallback = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return value.Trim().ToLowerInvariant() is "true" or "1" or "yes";
    }

    // ── connector id validation ──────────────────────────────────────────────

    /// <summary>
    /// Validate a Graph external-connection id: 3–32 chars, alphanumeric only,
    /// not starting with a reserved Microsoft prefix.
    /// </summary>
    public static void ValidateConnectorId(string connectorId)
    {
        if (connectorId.Length is < 3 or > 32)
            throw new ConfigException(
                $"Invalid CONNECTOR_ID '{connectorId}': must be 3-32 characters (got {connectorId.Length}).");
        if (!connectorId.All(char.IsLetterOrDigit))
            throw new ConfigException(
                $"Invalid CONNECTOR_ID '{connectorId}': only letters and digits are allowed.");
        foreach (var prefix in ReservedConnectorPrefixes)
        {
            if (connectorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ConfigException(
                    $"Invalid CONNECTOR_ID '{connectorId}': must not start with reserved prefix '{prefix}'.");
        }
    }
}
