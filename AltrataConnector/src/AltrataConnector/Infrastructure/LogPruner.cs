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
    /// <param name="activeRunDir">
    /// The run directory this process is writing into, which is never pruned.
    /// Defaults to <see cref="Logging.RunDirectory"/>; tests pass it explicitly.
    /// </param>
    public static int Prune(string? logsRoot = null, DateTime? utcNow = null, string? activeRunDir = null)
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

        // Normalised once, outside the loop. The previous guard compared the
        // enumerated path to Logging.RunDirectory with a raw Ordinal string
        // compare, which is only correct while both happen to be spelled the same
        // way — and this connector deliberately resolves its logs root from
        // LOGS_DIR (DirectoryHardening.LogsDir) rather than from
        // Logging.LogsRoot, so the two are built independently. A relative
        // LOGS_DIR, a trailing separator, or any difference in case on Windows
        // makes the compare miss and the guard silently stop guarding.
        //
        // Altrata is not currently exposed the way Clarizen and Hadoop were —
        // Prune runs once per process, immediately after RunLog.StartRun, so the
        // active directory is always newer than the cutoff. This is defence
        // against that ordering changing, not a live fix.
        var active = activeRunDir ?? Logging.RunDirectory;
        var activeFull = active is null ? null : Path.GetFullPath(active);

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
                if (activeFull is not null
                    && string.Equals(Path.GetFullPath(dir), activeFull, StringComparison.OrdinalIgnoreCase))
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
