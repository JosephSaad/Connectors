// Infrastructure/EnvLoader.cs
// ---------------------------
// Layered .env loading, same scheme as the Salesforce connector:
//
//   1. env/.env.local          (non-secret config)
//   2. env/.env.local.user     (SECRET_* values, never committed)
//   3. ./.env.local            (repo-root fallback)
//   4. ./.env.local.user       (repo-root fallback)
//
// Values already present in the real process environment ALWAYS win over the
// files, so container / Windows-service environment blocks can override
// anything. Within the files, the first file that defines a key wins (later
// files never overwrite earlier ones or the process env).
//
// File syntax: KEY=VALUE lines; '#' comments; optional `export ` prefix;
// single/double quotes around the value are stripped; blank lines ignored.

namespace ClarizenConnector.Infrastructure;

public static class EnvLoader
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector");

    /// <summary>Load the layered env files relative to <paramref name="baseDir"/> (default: CWD).</summary>
    public static void LoadLayered(string? baseDir = null)
    {
        var root = baseDir ?? Directory.GetCurrentDirectory();
        foreach (var relative in new[]
                 {
                     Path.Combine("env", ".env.local"),
                     Path.Combine("env", ".env.local.user"),
                     ".env.local",
                     ".env.local.user",
                 })
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path))
                LoadFile(path);
        }
    }

    /// <summary>Load one env file; process-env values and earlier files win.</summary>
    public static void LoadFile(string path)
    {
        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var (key, value) = ParseLine(rawLine);
                if (key is null)
                    continue;
                if (Environment.GetEnvironmentVariable(key) is null)
                    Environment.SetEnvironmentVariable(key, value);
            }
        }
        catch (Exception exc)
        {
            Logger.Warning($"Could not load env file '{path}': {exc.Message}");
        }
    }

    /// <summary>Parse one KEY=VALUE line. Returns (null, "") for comments/blank/invalid lines.</summary>
    internal static (string? Key, string Value) ParseLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            return (null, string.Empty);
        if (line.StartsWith("export ", StringComparison.Ordinal))
            line = line["export ".Length..].TrimStart();

        var eq = line.IndexOf('=');
        if (eq <= 0)
            return (null, string.Empty);

        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        if (key.Length == 0 || key.Contains(' '))
            return (null, string.Empty);

        // Strip one pair of matching surrounding quotes.
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }
        else
        {
            // Trailing inline comment (only for unquoted values).
            var hash = value.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0)
                value = value[..hash].TrimEnd();
        }
        return (key, value);
    }
}
