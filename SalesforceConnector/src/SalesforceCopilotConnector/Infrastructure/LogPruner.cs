// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/LogPruner.cs
// ---------------------------
// Retention pruning for the ``logs/`` directory (and, in SQL Server mode, the
// server-side history tables) driven by the LOG_RETENTION_DAYS environment
// variable — see the env table in docs/SQL_CONTRACT.md (binding contract).
//
// Behaviour:
// * LOG_RETENTION_DAYS unset or ``0`` → pruning disabled (default; no output).
// * Invalid / negative values        → pruning disabled with a warning.
// * N > 0 → delete run directories ``logs/{prefix}_{yyyyMMdd_HHmmss}/`` whose
//   embedded timestamp is older than N days. Only directories matching the
//   run-dir naming pattern are considered — files that live directly in the
//   logs root (``sync_state.json``, ``checkpoint_*.json``,
//   ``failed_records_*.jsonl``) and non-matching directories are never touched.
// * When the SQL Server state backend is active, additionally calls the
//   contract's ``usp_PruneHistory @RetentionDays`` stored procedure.
//
// Per-directory IO errors (e.g. a file locked by a log viewer) are swallowed
// with a warning — pruning must never break the run.
//
// Called from CommandRegistry.SetupLogging, so every command — and every
// --continuous cycle, which re-enters SetupLogging — prunes once at start.

using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using SalesforceCopilotConnector.Config;

namespace SalesforceCopilotConnector.Infrastructure;

public static class LogPruner
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>Run directory name: {prefix}_{yyyyMMdd_HHmmss} (prefix may itself contain underscores).</summary>
    private static readonly Regex RunDirPattern = new(@"_(\d{8}_\d{6})$", RegexOptions.Compiled);

    /// <summary>Test seam — redirects the logs root. Null in production (resolves ``logs/`` against the cwd).</summary>
    internal static string? LogsDirOverride;

    private static string LogsDir => LogsDirOverride ?? Path.GetFullPath("logs");

    /// <summary>
    /// Prune run directories (and SQL history in SQL Server mode) older than
    /// LOG_RETENTION_DAYS days. No-op when the variable is unset or ``0``.
    /// </summary>
    /// <param name="activeRunDir">
    /// The run directory this process is writing into, which is never pruned.
    /// </param>
    /// <remarks>
    /// This connector is NOT exposed to the defect the parameter guards against,
    /// and the guard is here so that stays true by construction rather than by
    /// coincidence. Prune is reached only from SetupLogging, which creates a
    /// fresh run directory immediately before calling it and — via ResetLogging —
    /// closes the previous cycle's handlers first. The directory being pruned
    /// into is therefore always stamped "now" and can never be older than the
    /// cutoff, however long a --continuous process runs.
    ///
    /// Clarizen and Hadoop had the same pruner shape with the opposite ordering:
    /// one run directory for the life of the process, pruned every cycle. After
    /// LOG_RETENTION_DAYS of uptime they deleted their own live logs. Nothing but
    /// call ordering separated the two designs, and nothing recorded that the
    /// ordering was load-bearing.
    /// </remarks>
    public static void Prune(IAppLogger? logger = null, string? activeRunDir = null)
    {
        logger ??= Logger;
        var activeFull = activeRunDir is null ? null : Path.GetFullPath(activeRunDir);

        var raw = Environment.GetEnvironmentVariable("LOG_RETENTION_DAYS");
        if (string.IsNullOrWhiteSpace(raw))
            return;  // disabled (default)

        if (!int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var days))
        {
            logger.Warning($"LOG_RETENTION_DAYS is not a valid integer ('{raw}') — log pruning disabled");
            return;
        }
        if (days < 0)
        {
            logger.Warning($"LOG_RETENTION_DAYS is negative ({days}) — log pruning disabled");
            return;
        }
        if (days == 0)
            return;  // disabled

        var logsDir = LogsDir;
        var pruned = 0;
        if (Directory.Exists(logsDir))
        {
            // Run-dir timestamps come from DateTime.Now in SetupLogging, so the
            // cutoff is computed on the same (local) clock.
            var cutoff = DateTime.Now.AddDays(-days);
            string[] runDirs;
            try
            {
                runDirs = Directory.GetDirectories(logsDir);
            }
            catch (Exception exc)
            {
                logger.Warning($"Log pruning: cannot list {logsDir}: {exc.Message}");
                runDirs = Array.Empty<string>();
            }

            foreach (var dir in runDirs)
            {
                if (activeFull is not null
                    && string.Equals(Path.GetFullPath(dir), activeFull, StringComparison.OrdinalIgnoreCase))
                {
                    continue;  // never prune the run we are writing into
                }

                var name = Path.GetFileName(dir);
                var match = RunDirPattern.Match(name);
                if (!match.Success)
                    continue;  // not a run directory — never touch
                if (!DateTime.TryParseExact(
                        match.Groups[1].Value, "yyyyMMdd_HHmmss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
                    continue;  // timestamp-shaped but not a valid date — leave it
                if (stamp >= cutoff)
                    continue;  // newer than the retention window

                try
                {
                    Directory.Delete(dir, recursive: true);
                    pruned += 1;
                }
                catch (Exception exc)
                {
                    // A locked file must not break the run — warn and move on.
                    logger.Warning($"Log pruning: could not delete {name}: {exc.Message}");
                }
            }
        }

        logger.Info($"Log pruning: pruned {pruned} run dir(s) older than {days} day(s)");

        // SQL Server mode: also prune server-side history per the contract.
        // Routed through SqlExecutor (like every other SQL caller) so the
        // connection is hardened — Encrypt forced on, and with
        // SQL_USE_MANAGED_IDENTITY=true the Entra (ActiveDirectoryDefault)
        // auth is injected (a raw SqlConnection would fail login every run,
        // since the connection string legitimately carries no credentials) —
        // and transient faults are retried.
        if (SyncState.UseSqlServer)
        {
            try
            {
                SqlExecutor.Execute(SqlStateStore.ConnectionString, connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = "dbo.usp_PruneHistory";
                    command.Parameters.AddWithValue("@RetentionDays", days);
                    command.ExecuteNonQuery();
                });
                logger.Info($"Log pruning: usp_PruneHistory executed (@RetentionDays={days})");
            }
            catch (Exception exc)
            {
                logger.Warning($"Log pruning: usp_PruneHistory failed: {exc.Message}");
            }
        }
    }
}
