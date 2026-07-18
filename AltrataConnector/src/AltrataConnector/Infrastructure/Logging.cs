// Infrastructure/Logging.cs
// -------------------------
// Console + file logging for the Altrata connector.
//
//   * Console: WARNING/ERROR always; INFO too when --verbose.
//   * File:    everything, under logs/{prefix}_{yyyyMMdd_HHmmss}/connector.log.
//   * LOG_FORMAT=json switches both sinks to one-line structured JSON records.
//
// The logger is process-global (like the reference connector): commands call
// Logging.StartRun("ingest") once, then any component gets a named logger via
// Logging.GetLogger(name).

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AltrataConnector.Infrastructure;

public interface IAppLogger
{
    /// <summary>
    /// The isEnabledFor(DEBUG) gate from the reference connector: callers on
    /// the ingest hot path MUST check this before building expensive debug
    /// messages (e.g. serializing a full $batch response) so the formatting
    /// cost is only paid when DEBUG is actually enabled.
    /// </summary>
    bool IsDebugEnabled { get; }

    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}

public static class Logging
{
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, IAppLogger> Loggers = new();

    private static StreamWriter? _fileWriter;
    private static string? _runDirectory;

    /// <summary>True when --verbose: INFO lines also go to the console.</summary>
    public static bool Verbose { get; set; }

    /// <summary>
    /// DEBUG is opt-in via LOG_LEVEL=debug (case-insensitive). Off by default:
    /// Debug() calls become cheap no-ops and hot-path callers skip expensive
    /// message formatting via <see cref="IAppLogger.IsDebugEnabled"/>.
    /// </summary>
    public static bool DebugEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("LOG_LEVEL"), "debug",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True when LOG_FORMAT=json (case-insensitive).</summary>
    public static bool JsonFormat =>
        string.Equals(Environment.GetEnvironmentVariable("LOG_FORMAT"), "json",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Directory of the current run's log files, or null before StartRun.</summary>
    public static string? RunDirectory => _runDirectory;

    /// <summary>Root logs directory (./logs by default, LOGS_DIR override for tests).</summary>
    public static string LogsRoot =>
        Environment.GetEnvironmentVariable("LOGS_DIR")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");

    /// <summary>
    /// Open a per-run log directory: logs/{prefix}_{timestamp}/connector.log.
    /// Safe to call more than once — subsequent calls are no-ops for the run dir.
    /// </summary>
    public static void StartRun(string prefix)
    {
        lock (Sync)
        {
            if (_fileWriter != null)
                return;
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var dir = Path.Combine(LogsRoot, $"{prefix}_{stamp}");
                // Owner-only permissions on the logs root and this run's
                // directory (POSIX 0700 / best-effort Windows ACLs). Run logs
                // hold PII-safe-but-still-sensitive operational detail; never
                // fatal (see DirectoryHardening).
                DirectoryHardening.EnsureSecureDirectory(LogsRoot);
                DirectoryHardening.EnsureSecureDirectory(dir);
                _runDirectory = dir;
                _fileWriter = new StreamWriter(
                    new FileStream(Path.Combine(dir, "connector.log"),
                        FileMode.Append, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(false))
                { AutoFlush = true };
                // Lifecycle marker mirrored to the Windows Event Log when enabled.
                EventLogSink.Lifecycle($"Run started: {prefix} (logs: {dir})");
            }
            catch (Exception exc)
            {
                // Logging must never take the process down; fall back to
                // console-only — but SAY so on stderr (the file sink is the
                // thing that just failed), or the operator hunts for a run log
                // that was never written.
                _fileWriter = null;
                Console.Error.WriteLine(
                    $"WARNING: could not open a run log directory under '{LogsRoot}' " +
                    $"({exc.GetType().Name}: {exc.Message}) — file logging disabled for this run, console only.");
            }
        }
    }

    /// <summary>Close the run's log file (used by tests; the OS closes it otherwise).</summary>
    public static void EndRun()
    {
        lock (Sync)
        {
            try { _fileWriter?.Dispose(); } catch { /* ignore */ }
            _fileWriter = null;
            _runDirectory = null;
        }
    }

    public static IAppLogger GetLogger(string name) =>
        Loggers.GetOrAdd(name, n => new AppLogger(n));

    internal static void Emit(string logger, string level, string message, Exception? exception)
    {
        if (level == "DEBUG" && !DebugEnabled)
            return;  // DEBUG is filtered in normal runs (hot-path cost control)

        string line;
        if (JsonFormat)
        {
            // Correlation id stamped on every structured line so a whole cycle
            // is followable end-to-end (present whenever a crawl/erasure scope
            // is active; omitted otherwise).
            line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["level"] = level,
                ["logger"] = logger,
                ["correlationId"] = CorrelationContext.Current,
                ["message"] = message,
                ["exception"] = exception?.ToString(),
            });
        }
        else
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            line = $"{stamp} [{level,-7}] {logger}: {message}";
            if (exception != null)
                line += Environment.NewLine + exception;
        }

        lock (Sync)
        {
            try { _fileWriter?.WriteLine(line); } catch { /* ignore */ }
        }

        // Windows Event Log mirroring (EVENTLOG_ENABLED): WARNING/ERROR only,
        // the same PII-safe message text the file sink received. No-op unless
        // enabled; never throws (see EventLogSink).
        if (level is "WARNING" or "ERROR")
            EventLogSink.Mirror(level, logger, message, exception);

        var toConsole = level is "WARNING" or "ERROR" || Verbose;
        if (!toConsole)
            return;
        if (level == "ERROR")
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }

    private sealed class AppLogger : IAppLogger
    {
        private readonly string _name;
        public AppLogger(string name) => _name = name;

        public bool IsDebugEnabled => DebugEnabled;

        public void Debug(string message) => Emit(_name, "DEBUG", message, null);
        public void Info(string message) => Emit(_name, "INFO", message, null);
        public void Warning(string message) => Emit(_name, "WARNING", message, null);
        public void Error(string message, Exception? exception = null) =>
            Emit(_name, "ERROR", message, exception);
    }
}
