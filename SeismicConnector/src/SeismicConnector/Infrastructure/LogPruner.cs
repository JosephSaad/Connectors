// Infrastructure/LogPruner.cs
// ---------------------------
// LOG_RETENTION_DAYS > 0 prunes logs/{prefix}_{timestamp}/ run directories
// older than N days at the start of every command and each --continuous
// cycle. Root state files (sync_state.json, checkpoint_*.json,
// failed_records_*.jsonl) are NEVER touched. 0 / unset keeps everything.

using System.Globalization;
using System.Text.RegularExpressions;

namespace SeismicConnector.Infrastructure;

public static class LogPruner
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector");

    public const string RetentionEnvVar = "LOG_RETENTION_DAYS";

    /// <summary>Run-directory pattern: {prefix}_{yyyyMMdd_HHmmss}.</summary>
    private static readonly Regex RunDirPattern = new(
        @"^[A-Za-z0-9\-]+_(\d{8}_\d{6})$", RegexOptions.Compiled);

    public static int RetentionDays =>
        int.TryParse(
            Environment.GetEnvironmentVariable(RetentionEnvVar),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
            ? days
            : 0;

    /// <summary>Prune old run dirs under <paramref name="logsDir"/>. Never throws.</summary>
    public static int Prune(string logsDir, DateTime? nowLocal = null)
    {
        var retentionDays = RetentionDays;
        if (retentionDays <= 0)
            return 0;
        var cutoff = (nowLocal ?? DateTime.Now).AddDays(-retentionDays);
        var pruned = 0;
        try
        {
            if (!Directory.Exists(logsDir))
                return 0;
            foreach (var dir in Directory.GetDirectories(logsDir))
            {
                var name = Path.GetFileName(dir);
                var match = RunDirPattern.Match(name);
                if (!match.Success)
                    continue;
                if (!DateTime.TryParseExact(
                        match.Groups[1].Value, "yyyyMMdd_HHmmss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
                {
                    continue;
                }
                if (stamp >= cutoff)
                    continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    pruned++;
                }
                catch (Exception exc)
                {
                    Logger.Warning($"Log pruning: could not delete {dir}: {exc.Message}");
                }
            }
            if (pruned > 0)
                Logger.Info($"Log pruning: removed {pruned} run director{(pruned == 1 ? "y" : "ies")} older than {retentionDays} day(s)");
        }
        catch (Exception exc)
        {
            Logger.Warning($"Log pruning failed: {exc.Message}");
        }
        return pruned;
    }
}
