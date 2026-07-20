// Infrastructure/LogPruner.cs
// ---------------------------
// LOG_RETENTION_DAYS > 0 prunes logs/{prefix}_{timestamp}/ run directories
// older than N days at the start of every command and each --continuous
// cycle. Root state files (sync_state.json, checkpoint_*.json,
// failed_records_*.jsonl) are NEVER touched. 0 / unset keeps everything.
//
// RETENTION NEVER DELETES A DECISION LEDGER. The current ledger lives at the
// logs ROOT (logs/decision_ledger_{id}.jsonl), so it is already outside the
// blast radius. Legacy per-run ledgers — written inside run directories by
// releases before that move — are protected explicitly: a run directory that
// still holds one is KEPT and named in a warning, so compliance evidence is
// never destroyed by a retention setting. Archive it off-box, then delete the
// directory by hand.

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

    /// <summary>
    /// Decision ledgers (current or legacy) anywhere under a run directory, or
    /// <c>null</c> when the directory could not be scanned — which is treated as
    /// "may hold a ledger". Retention is a convenience; audit evidence is not,
    /// so anything we cannot prove ledger-free is kept.
    /// </summary>
    private static IReadOnlyList<string>? LedgersUnder(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "decision_ledger_*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"Log pruning: could not scan {dir} for decision ledgers ({exc.Message}) — keeping the "
                + "directory rather than risk deleting audit records.");
            return null;
        }
    }

    /// <summary>Prune old run dirs under <paramref name="logsDir"/>. Never throws.</summary>
    public static int Prune(string logsDir, DateTime? nowLocal = null)
    {
        var retentionDays = RetentionDays;
        if (retentionDays <= 0)
            return 0;
        var now = nowLocal ?? DateTime.Now;
        // A retention window longer than the calendar can express (LOG_RETENTION_DAYS
        // = 1000000, int.MaxValue, ...) means "keep everything", not "crash":
        // now.AddDays(-huge) throws ArgumentOutOfRangeException, and this runs at the
        // start of EVERY command and every --continuous cycle, so one typo'd env var
        // would take the connector down before it did any work. Clamp to
        // DateTime.MinValue — nothing can be older than that, so nothing is pruned,
        // which is also the safe direction (keep more, never delete more).
        var cutoff = retentionDays >= (now - DateTime.MinValue).TotalDays
            ? DateTime.MinValue
            : now.AddDays(-retentionDays);
        if (cutoff == DateTime.MinValue)
        {
            Logger.Warning(
                $"{RetentionEnvVar}={retentionDays} is a longer window than any log can be old — "
                + "nothing will ever be pruned. Set a realistic number of days (e.g. 30) if you "
                + "intended retention to actually run.");
        }
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
                var ledgers = LedgersUnder(dir);
                if (ledgers is null)
                    continue;   // unscannable — already warned; keep it
                if (ledgers.Count > 0)
                {
                    Logger.Warning(
                        $"Log pruning: KEPT {dir} — it still holds {ledgers.Count} decision ledger file(s) "
                        + $"({string.Join(", ", ledgers.Select(Path.GetFileName))}). Retention never deletes "
                        + "tamper-evident audit records; archive them off-box and remove the directory by "
                        + "hand (docs/OBSERVABILITY.md).");
                    continue;
                }
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
