// Infrastructure/LogPruner.cs
// ---------------------------
// LOG_RETENTION_DAYS pruning of per-run log directories (logs/{prefix}_{stamp}/).
// Disabled when LOG_RETENTION_DAYS is unset or <= 0. Never throws.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AltrataConnector.Infrastructure;

public static class LogPruner
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector");

    /// <summary>Run-directory name shape: {prefix}_{yyyyMMdd_HHmmss}.</summary>
    private static readonly Regex RunDirPattern =
        new(@"^[A-Za-z0-9\-]+_(\d{8}_\d{6})$", RegexOptions.Compiled);

    public static int RetentionDays
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("LOG_RETENTION_DAYS");
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
                ? days
                : 0;
        }
    }

    /// <summary>Delete run directories older than LOG_RETENTION_DAYS. Returns count removed.</summary>
    public static int Prune(string? logsRoot = null, DateTime? utcNow = null)
    {
        var days = RetentionDays;
        if (days <= 0)
            return 0;

        // DirectoryHardening.LogsDir, not chassis Logging.LogsRoot: the latter is
        // a settable static that only reflects LOGS_DIR once a run has started, so
        // pruning invoked outside a run would target a stale path.
        var root = logsRoot ?? DirectoryHardening.LogsDir;
        if (!Directory.Exists(root))
            return 0;

        var cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-days);
        var removed = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                var match = RunDirPattern.Match(name);
                if (!match.Success)
                    continue;
                if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd_HHmmss",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var stamp))
                    continue;
                if (string.Equals(dir, Logging.RunDirectory, StringComparison.Ordinal))
                    continue;  // never delete the active run
                if (stamp.ToUniversalTime() >= cutoff)
                    continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch (Exception exc)
                {
                    Logger.Warning($"Log pruning: could not delete {dir}: {exc.Message}");
                }
            }
        }
        catch (Exception exc)
        {
            Logger.Warning($"Log pruning failed: {exc.Message}");
        }
        if (removed > 0)
            Logger.Info($"Log pruning removed {removed} run director{(removed == 1 ? "y" : "ies")} older than {days} days");
        return removed;
    }
}
