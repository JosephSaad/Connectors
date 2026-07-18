// Infrastructure/Logging.cs
// -------------------------
// Process-wide logging for the BDH Hadoop connector.
//
// Every command creates a run directory logs/{prefix}_{yyyyMMdd_HHmmss}/ and
// writes connector.log inside it; the console shows WARNING+ by default and
// everything with --verbose. LOG_FORMAT=json switches the file (and console)
// lines to one JSON object per line: {timestamp, level, logger, message}.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HadoopConnector.Infrastructure;

public interface IAppLogger
{
    string Name { get; }
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);

    /// <summary>
    /// Python's <c>Logger.isEnabledFor</c>: true when a record at
    /// <paramref name="level"/> would reach at least one sink. Callers use it
    /// to skip building expensive per-item log strings in the ingest hot path.
    /// </summary>
    bool IsEnabledFor(LogLevel level);
}

public enum LogLevel
{
    Debug = 10,
    Info = 20,
    Warning = 30,
    Error = 40,
}

public static class Logging
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, IAppLogger> Loggers = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static StreamWriter? _fileWriter;

    /// <summary>Console threshold: WARNING by default, DEBUG with --verbose.</summary>
    public static LogLevel ConsoleLevel { get; set; } = LogLevel.Warning;

    /// <summary>Current run directory (logs/{prefix}_{timestamp}), or null before Initialize.</summary>
    public static string? RunDirectory { get; private set; }

    /// <summary>True when LOG_FORMAT=json (read at write time so tests can flip it).</summary>
    internal static bool JsonFormat =>
        string.Equals(Environment.GetEnvironmentVariable("LOG_FORMAT"), "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Test seam: capture emitted lines instead of (in addition to) the sinks.</summary>
    internal static Action<LogLevel, string, string>? TestSink;

    /// <summary>Test seam: receives the fully rendered line (JSON or text) as written to the sinks.</summary>
    internal static Action<string>? RenderedSink;

    /// <summary>
    /// The lowest level any sink currently accepts: the file sink captures
    /// DEBUG+ whenever a run directory is open; otherwise only the console
    /// threshold applies. LOG_LEVEL (DEBUG|INFO|WARNING|ERROR) raises the
    /// floor for both sinks — the hot-path gate honours it.
    /// </summary>
    public static LogLevel EffectiveLevel()
    {
        var floor = ParseLevel(Environment.GetEnvironmentVariable("LOG_LEVEL"));
        LogLevel sinks;
        lock (Sync)
        {
            sinks = _fileWriter is not null
                ? (LogLevel)Math.Min((int)LogLevel.Debug, (int)ConsoleLevel)
                : ConsoleLevel;
        }
        return (LogLevel)Math.Max((int)floor, (int)sinks);
    }

    internal static LogLevel ParseLevel(string? raw) => raw?.ToUpperInvariant() switch
    {
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Info,
        "WARNING" or "WARN" => LogLevel.Warning,
        "ERROR" => LogLevel.Error,
        _ => LogLevel.Debug,  // unset/unknown → no extra floor
    };

    /// <summary>Root logs directory; settable so tests can redirect.</summary>
    public static string LogsRoot { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "logs");

    /// <summary>
    /// Create the per-run log directory and open connector.log. Idempotent —
    /// the first caller wins; later calls are no-ops.
    /// </summary>
    public static void Initialize(string runPrefix, bool verbose)
    {
        ConsoleLevel = verbose ? LogLevel.Debug : LogLevel.Warning;
        lock (Sync)
        {
            if (_fileWriter != null)
                return;
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var dir = Path.Combine(LogsRoot, $"{runPrefix}_{stamp}");
                Directory.CreateDirectory(dir);
                RunDirectory = dir;
                _fileWriter = new StreamWriter(
                    Path.Combine(dir, "connector.log"), append: true, Utf8NoBom)
                {
                    AutoFlush = true,
                };
            }
            catch (Exception exc)
            {
                // Console-only logging when the log directory cannot be created.
                // The Logger itself is what just failed, so stderr is the only
                // sink left — without this line an operator sees no connector.log
                // and has no way to know it was a permissions/path problem.
                _fileWriter = null;
                Console.Error.WriteLine(
                    $"WARNING: could not create log directory under '{LogsRoot}' "
                    + $"({exc.GetType().Name}: {exc.Message}); continuing with console-only logging.");
            }
        }
    }

    /// <summary>Test seam: close the file writer and forget run state.</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _fileWriter?.Dispose();
            _fileWriter = null;
            RunDirectory = null;
            TestSink = null;
            RenderedSink = null;
            ConsoleLevel = LogLevel.Warning;
        }
    }

    public static IAppLogger GetLogger(string name)
    {
        lock (Sync)
        {
            if (!Loggers.TryGetValue(name, out var logger))
            {
                logger = new AppLogger(name);
                Loggers[name] = logger;
            }
            return logger;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void Write(LogLevel level, string loggerName, string message)
    {
        // LOG_LEVEL raises the floor for every sink (matches EffectiveLevel()).
        if (level < ParseLevel(Environment.GetEnvironmentVariable("LOG_LEVEL")))
            return;

        TestSink?.Invoke(level, loggerName, message);

        // Mirror Warning/Error (and Info with EVENTLOG_LEVEL=info) to the
        // Windows Event Log when EVENTLOG_ENABLED=true (docs/SIEM.md). Inert
        // until EventLogSink.Initialize() activates it; never throws.
        EventLogSink.Mirror(level, loggerName, message);

        // Stamp the ambient correlation id (per crawl cycle / webhook event) so
        // a single run is followable end-to-end. Null when outside a scope.
        var correlationId = CorrelationContext.Current;

        string line;
        if (JsonFormat)
        {
            var fields = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["level"] = level.ToString().ToUpperInvariant(),
                ["logger"] = loggerName,
                ["message"] = message,
            };
            if (correlationId is not null)
                fields["correlation_id"] = correlationId;
            line = JsonSerializer.Serialize(fields, JsonOptions);
        }
        else
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var prefix = correlationId is not null ? $"[{correlationId[..8]}] " : string.Empty;
            line = $"{stamp} [{level.ToString().ToUpperInvariant(),-7}] {prefix}{loggerName}: {message}";
        }

        RenderedSink?.Invoke(line);

        lock (Sync)
        {
            try
            {
                _fileWriter?.WriteLine(line);
            }
            catch
            {
                // Logging must never take the process down.
            }
        }

        if (level >= ConsoleLevel)
        {
            if (level >= LogLevel.Warning)
                Console.Error.WriteLine(line);
            else
                Console.WriteLine(line);
        }
    }

    private sealed class AppLogger : IAppLogger
    {
        public AppLogger(string name) => Name = name;

        public string Name { get; }

        public void Debug(string message) => Write(LogLevel.Debug, Name, message);

        public void Info(string message) => Write(LogLevel.Info, Name, message);

        public void Warning(string message) => Write(LogLevel.Warning, Name, message);

        public void Error(string message, Exception? exception = null) =>
            Write(LogLevel.Error, Name, exception is null ? message : $"{message}\n{exception}");

        public bool IsEnabledFor(LogLevel level) => level >= EffectiveLevel();
    }
}
