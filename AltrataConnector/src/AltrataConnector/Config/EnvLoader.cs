// Config/EnvLoader.cs
// -------------------
// Layered .env loading, matching the reference connector:
//
//   env/.env.local        non-secret config (per-node)
//   env/.env.local.user   SECRET_* values (never committed)
//   ./.env.local          repo-root fallbacks
//   ./.env.local.user
//
// Values already present in the real process environment always win, so
// container / Windows-service environment blocks can override anything in the
// files. Files are simple KEY=VALUE lines; '#' comments, blank lines, optional
// `export ` prefix and single/double quotes are supported.

namespace AltrataConnector.Config;

public static class EnvLoader
{
    private static readonly object Sync = new();
    private static bool _loaded;

    /// <summary>Relative candidate files, in load order (first hit per key wins
    /// among files; process env wins over all files).</summary>
    public static readonly string[] CandidateFiles =
    {
        Path.Combine("env", ".env.local"),
        Path.Combine("env", ".env.local.user"),
        ".env.local",
        ".env.local.user",
    };

    /// <summary>Load the layered env files once per process (idempotent).</summary>
    public static void LoadOnce(string? baseDirectory = null)
    {
        lock (Sync)
        {
            if (_loaded)
                return;
            Load(baseDirectory ?? Directory.GetCurrentDirectory());
            _loaded = true;
        }
    }

    /// <summary>Force a (re)load — test seam.</summary>
    internal static void Load(string baseDirectory)
    {
        foreach (var relative in CandidateFiles)
        {
            var path = Path.Combine(baseDirectory, relative);
            if (!File.Exists(path))
                continue;
            foreach (var (key, value) in Parse(File.ReadAllLines(path)))
            {
                // Process environment (and earlier files) win.
                if (Environment.GetEnvironmentVariable(key) == null)
                    Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _loaded = false;
        }
    }

    /// <summary>Parse KEY=VALUE lines. Returns pairs in file order.</summary>
    public static IEnumerable<(string Key, string Value)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }
            else
            {
                // Strip trailing inline comments on unquoted values.
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0)
                    value = value[..hash].TrimEnd();
            }

            if (key.Length > 0)
                yield return (key, value);
        }
    }
}
