// Infrastructure/LogPruner.cs
// ---------------------------
// LOG_RETENTION_DAYS pruning of logs/{prefix}_{yyyyMMdd_HHmmss}/ run
// directories. Root state files (sync_state.json, checkpoint_*.json,
// failed_records_*.jsonl) are NEVER touched — only run directories whose
// embedded timestamp is older than the retention window are deleted. Runs at
// the start of every command and each --continuous cycle; never throws.

using System.Globalization;
using System.Text.RegularExpressions;

namespace ClarizenConnector.Infrastructure;

public static partial class LogPruner
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector");

    public const string RetentionEnvVar = "LOG_RETENTION_DAYS";

    [GeneratedRegex(@"^(?<prefix>.+)_(?<stamp>\d{8}_\d{6})$")]
    private static partial Regex RunDirPattern();

    /// <summary>
    /// Delete run directories under <paramref name="logsRoot"/> older than
    /// LOG_RETENTION_DAYS. No-op when the env var is unset/invalid/&lt;=0.
    /// Returns the number of directories removed. Never throws.
    /// </summary>
    public static int PruneIfConfigured(string logsRoot, DateTime? nowLocal = null)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(RetentionEnvVar);
            if (string.IsNullOrWhiteSpace(raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
                || days <= 0)
            {
                return 0;
            }
            return Prune(logsRoot, days, nowLocal ?? DateTime.Now);
        }
        catch (Exception exc)
        {
            Logger.Warning($"Log pruning failed: {exc.Message}");
            return 0;
        }
    }

    /// <summary>Core pruning (testable): delete run dirs older than <paramref name="retentionDays"/>.</summary>
    internal static int Prune(string logsRoot, int retentionDays, DateTime nowLocal)
    {
        if (!Directory.Exists(logsRoot))
            return 0;

        var cutoff = nowLocal.AddDays(-retentionDays);
        var removed = 0;
        foreach (var dir in Directory.GetDirectories(logsRoot))
        {
            var name = Path.GetFileName(dir);
            var match = RunDirPattern().Match(name);
            if (!match.Success)
                continue;  // not a run directory — never touch
            if (!DateTime.TryParseExact(
                    match.Groups["stamp"].Value, "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            {
                continue;
            }
            if (stamp >= cutoff)
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception exc)
            {
                Logger.Warning($"Log pruning: could not delete '{dir}': {exc.Message}");
            }
        }
        if (removed > 0)
            Logger.Info($"Log pruning removed {removed} run director{(removed == 1 ? "y" : "ies")} older than {retentionDays} day(s)");
        return removed;
    }
}
