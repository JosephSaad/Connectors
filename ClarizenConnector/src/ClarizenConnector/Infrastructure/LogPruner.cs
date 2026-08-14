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
    /// <param name="activeRunDir">
    /// The run directory this process is writing into, which is never pruned.
    /// Defaults to <see cref="Logging.RunDirectory"/>, which is what production
    /// wants; tests pass it explicitly.
    /// </param>
    public static int PruneIfConfigured(string logsRoot, DateTime? nowLocal = null, string? activeRunDir = null)
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
            return Prune(logsRoot, days, nowLocal ?? DateTime.Now, activeRunDir);
        }
        catch (Exception exc)
        {
            Logger.Warning($"Log pruning failed: {exc.Message}");
            return 0;
        }
    }

    /// <summary>Core pruning (testable): delete run dirs older than <paramref name="retentionDays"/>.</summary>
    internal static int Prune(string logsRoot, int retentionDays, DateTime nowLocal, string? activeRunDir = null)
    {
        if (!Directory.Exists(logsRoot))
            return 0;

        // The run directory this process is writing into is chosen ONCE, by the
        // first Logging.Initialize (later calls are no-ops), and never changes.
        // This method runs on every --continuous cycle, so after
        // LOG_RETENTION_DAYS of uptime the active directory ages past the cutoff
        // and the pruner starts targeting its own live logs on every cycle.
        //
        // The two platforms fail differently and the quiet one is worse. On
        // Windows the recursive delete removes the directory's other entries
        // first and only then throws on the locked connector.log, so live
        // artifacts are destroyed every cycle behind a warning that repeats
        // forever. On macOS and Linux the unlink simply SUCCEEDS: the open handle
        // keeps writing into an unlinked inode and every log line for the
        // remaining life of the service goes nowhere, silently.
        //
        // Same guard as Connector.Chassis.LogPruner, which fixed this for the one
        // connector that consumes the chassis pruner. This copy is why that was
        // not enough.
        var active = activeRunDir ?? Logging.RunDirectory;
        var activeFull = active is null ? null : Path.GetFullPath(active);

        var cutoff = nowLocal.AddDays(-retentionDays);
        var removed = 0;
        foreach (var dir in Directory.GetDirectories(logsRoot))
        {
            if (activeFull is not null
                && string.Equals(Path.GetFullPath(dir), activeFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;  // never prune the run we are writing into
            }

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
