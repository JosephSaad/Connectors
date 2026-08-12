// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Connector.Chassis;

/// <summary>
/// Application logger contract shared by every ported module.
///
/// Mirrors Python <c>logging</c>: console shows WARNING+ by default, everything with
/// <c>--verbose</c>; a log file always captures all levels.
/// </summary>
public interface IAppLogger
{
    /// <summary>
    /// Logger name. Standard call sites read this (e.g. structured backstop
    /// assertions); Seismic call sites never touch it. Additive — every
    /// implementation in this assembly already carries a name.
    /// </summary>
    string Name { get; }

    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);

    /// <summary>
    /// Error with an optional attached exception. Nullable second parameter so
    /// Standard call sites (whose original contract was
    /// <c>Error(string, Exception? = null)</c>) bind here without a nullability
    /// warning; Seismic call sites pass a non-null exception, which is assignable.
    /// </summary>
    void Error(string message, Exception? ex);

    /// <summary>
    /// Whether a record at <paramref name="level"/> would be processed
    /// (Python <c>Logger.isEnabledFor</c>). Callers use it to skip building
    /// expensive log messages that would be dropped by the level check.
    /// </summary>
    bool IsEnabledFor(int level);

    /// <summary>
    /// Standard-mode overload: whether a record at <paramref name="level"/>
    /// (a <see cref="LogLevel"/>) would reach at least one sink under the active
    /// Standard console/file thresholds and the <c>LOG_LEVEL</c> floor. Distinct
    /// from <see cref="IsEnabledFor(int)"/> by parameter type — no ambiguity,
    /// since <c>int</c> and <see cref="LogLevel"/> have no implicit conversion.
    /// </summary>
    bool IsEnabledFor(LogLevel level);
}

/// <summary>Numeric log levels matching the Python <c>logging</c> module constants.</summary>
public static class LogLevels
{
    public const int NotSet = 0;
    public const int Debug = 10;
    public const int Info = 20;
    public const int Warning = 30;
    public const int Error = 40;
    public const int Critical = 50;

    /// <summary>Level name as rendered by Python's <c>%(levelname)s</c>.</summary>
    public static string Name(int level) => level switch
    {
        Debug => "DEBUG",
        Info => "INFO",
        Warning => "WARNING",
        Error => "ERROR",
        Critical => "CRITICAL",
        _ => $"Level {level}",
    };
}

/// <summary>
/// Strongly-typed log levels used by the Standard surface. The underlying
/// values match the <see cref="LogLevels"/> int constants (Debug=10 … Error=40),
/// so casting between the two is a value-preserving numeric conversion.
/// </summary>
public enum LogLevel
{
    Debug = 10,
    Info = 20,
    Warning = 30,
    Error = 40,
}

/// <summary>
/// Which connector's logging behaviour the shared <see cref="Logging"/> entry
/// point currently emulates. The last configuring entry point wins:
/// <see cref="Logging.Configure"/> selects <see cref="Seismic"/> (the default,
/// handler/propagation based, chassis text/json format); <see cref="Logging.Initialize"/>
/// (or <see cref="Logging.UseStandardMode"/>) selects <see cref="Standard"/>
/// (run-dir file sink, ConsoleLevel/EffectiveLevel gating, TestSink/RenderedSink,
/// correlation-in-text, Standard text/json format). Loggers dispatch on this at
/// log time, so a logger cached before the mode was set still behaves correctly.
/// </summary>
public enum LoggingMode
{
    Seismic,
    Standard,
}

/// <summary>A single log event (mirrors Python <c>logging.LogRecord</c>).</summary>
public sealed class LogRecord
{
    public required string Name { get; init; }
    public required int Level { get; init; }
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

/// <summary>Base class for log handlers (mirrors Python <c>logging.Handler</c>).</summary>
public abstract class LogHandler
{
    private readonly object _emitLock = new();

    /// <summary>Minimum level this handler emits. 0 (NotSet) emits everything.</summary>
    public int Level { get; set; } = LogLevels.NotSet;

    /// <summary>
    /// When true, format records as message-only (Python <c>"%(message)s"</c>);
    /// otherwise <c>"%(asctime)s - %(name)s - %(levelname)s - %(message)s"</c>.
    /// </summary>
    public bool MessageOnly { get; set; }

    public void Handle(LogRecord record)
    {
        if (record.Level < Level)
            return;
        lock (_emitLock)
            Emit(record);
    }

    protected string Format(LogRecord record)
    {
        var message = record.Message;
        if (record.Exception != null)
            message = message + "\n" + record.Exception;
        // MessageOnly handlers (e.g. the progress console) always emit the bare
        // message in BOTH text and json modes — the structured switch never
        // rewrites progress-console output.
        if (MessageOnly)
            return message;
        // Structured logs (#10): LOG_FORMAT=json → one JSON object per record.
        // Default (text) is byte-identical to the historical formatter.
        if (Logging.JsonFormat)
            return FormatJson(record);
        var asctime = record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture);
        return $"{asctime} - {record.Name} - {LogLevels.Name(record.Level)} - {message}";
    }

    /// <summary>
    /// Render <paramref name="record"/> as a single-line JSON object
    /// (<c>LOG_FORMAT=json</c>). Keys: <c>timestamp</c> (same
    /// <c>yyyy-MM-dd HH:mm:ss,fff</c> string the text format uses), <c>level</c>,
    /// <c>logger</c>, <c>message</c>; when an exception is attached, an
    /// <c>exception</c> object carries its <c>type</c> and <c>message</c>.
    /// </summary>
    private static string FormatJson(LogRecord record)
    {
        var asctime = record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, JsonWriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", asctime);
            writer.WriteString("level", LogLevels.Name(record.Level));
            writer.WriteString("logger", record.Name);
            writer.WriteString("message", record.Message);
            // Distributed-tracing correlation: stamp the current crawl-cycle id
            // on every structured line so a crawl is greppable end-to-end.
            var correlationId = Chassis.CorrelationIdProvider?.Invoke();
            if (correlationId is not null)
                writer.WriteString("correlation_id", correlationId);
            if (record.Exception != null)
            {
                writer.WriteStartObject("exception");
                writer.WriteString("type", record.Exception.GetType().FullName);
                writer.WriteString("message", record.Exception.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        // Match the rest of the project's escaping posture without HTML-escaping
        // characters like < > & that legitimately appear in log messages.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    protected abstract void Emit(LogRecord record);

    public virtual void Close()
    {
    }
}

/// <summary>Console (stream) handler, equivalent to <c>logging.StreamHandler(sys.stdout)</c>.</summary>
public sealed class StreamHandler : LogHandler
{
    private readonly TextWriter _writer;

    public StreamHandler(TextWriter writer)
    {
        _writer = writer;
    }

    protected override void Emit(LogRecord record)
    {
        try
        {
            _writer.Write(Format(record) + "\n");
            _writer.Flush();
        }
        catch
        {
            // best-effort, like Python's handleError
        }
    }
}

/// <summary>
/// File handler that rotates to a new file after <c>maxLines</c> lines.
///
/// New files are named <c>&lt;stem&gt;_2.log</c>, <c>&lt;stem&gt;_3.log</c>, etc.
/// (Port of <c>commands._LineRotatingFileHandler</c>.)
/// </summary>
public sealed class LineRotatingFileHandler : LogHandler
{
    public const int MaxLinesPerLog = 100_000;  // 1 lakh lines

    private readonly string _basePath;
    private readonly int _maxLines;
    private int _lineCount;
    private int _fileIndex = 1;
    private StreamWriter _stream;

    /// <summary>Path of the file currently being written (mirrors Python <c>baseFilename</c>).</summary>
    public string BaseFilename { get; private set; }

    public LineRotatingFileHandler(string filename, int maxLines = MaxLinesPerLog)
    {
        _basePath = filename;
        _maxLines = maxLines;
        BaseFilename = filename;
        var dir = Path.GetDirectoryName(Path.GetFullPath(filename));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _stream = new StreamWriter(filename, append: true, new UTF8Encoding(false));
    }

    protected override void Emit(LogRecord record)
    {
        try
        {
            _stream.Write(Format(record) + "\n");
            _stream.Flush();
        }
        catch
        {
            // The file handler cannot log its own IO failure (it IS the log
            // sink); dropping the record beats crashing the caller. Mirrors
            // Python logging's handleError posture.
            return;
        }
        _lineCount++;
        if (_lineCount >= _maxLines)
            Rotate();
    }

    /// <summary>Close the current file and open the next numbered file.</summary>
    private void Rotate()
    {
        _fileIndex++;
        var stem = Path.GetFileNameWithoutExtension(_basePath);
        var suffix = Path.GetExtension(_basePath);
        var newName = $"{stem}_{_fileIndex}{suffix}";
        var newPath = Path.Combine(Path.GetDirectoryName(_basePath) ?? "", newName);
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Best-effort close of the full log file before rotating on; a
            // dispose failure must not stop the new file from opening.
        }
        BaseFilename = newPath;
        _lineCount = 0;
        _stream = new StreamWriter(newPath, append: true, new UTF8Encoding(false));
    }

    public override void Close()
    {
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Best-effort teardown of the log sink itself — nowhere to report.
        }
    }
}

/// <summary>
/// Named logger with handler list, level, and propagation to ancestors
/// (mirrors Python <c>logging.Logger</c> closely enough for this project).
/// </summary>
public sealed class LoggerObject : IAppLogger
{
    private readonly List<LogHandler> _handlers = new();

    public string Name { get; }

    /// <summary>0 (NotSet) inherits the effective level from the nearest ancestor.</summary>
    public int Level { get; set; } = LogLevels.NotSet;

    public bool Propagate { get; set; } = true;

    internal LoggerObject(string name)
    {
        Name = name;
    }

    /// <summary>Snapshot of the handlers attached to this logger.</summary>
    public IReadOnlyList<LogHandler> Handlers
    {
        get
        {
            lock (_handlers)
                return _handlers.ToArray();
        }
    }

    public void AddHandler(LogHandler handler)
    {
        lock (_handlers)
        {
            if (!_handlers.Contains(handler))
                _handlers.Add(handler);
        }
    }

    public void RemoveHandler(LogHandler handler)
    {
        lock (_handlers)
            _handlers.Remove(handler);
    }

    private int EffectiveLevel()
    {
        var logger = (LoggerObject?)this;
        while (logger != null)
        {
            if (logger.Level != LogLevels.NotSet)
                return logger.Level;
            logger = Logging.Parent(logger);
        }
        return LogLevels.Warning;  // Python root logger default
    }

    /// <summary>Python <c>Logger.isEnabledFor</c>: true when <paramref name="level"/> ≥ the effective level.</summary>
    public bool IsEnabledFor(int level) => level >= EffectiveLevel();

    /// <summary>
    /// Standard-mode <c>isEnabledFor</c>: gates on the process-wide Standard
    /// console/file thresholds and the <c>LOG_LEVEL</c> floor (see
    /// <see cref="Logging.EffectiveLevel"/>). In Seismic mode this is never
    /// called by Seismic call sites; it falls back to the handler-based
    /// effective level so the contract still holds.
    /// </summary>
    public bool IsEnabledFor(LogLevel level) =>
        Logging.Mode == LoggingMode.Standard
            ? level >= Logging.EffectiveLevel()
            : (int)level >= EffectiveLevel();

    public void Log(int level, string message, Exception? ex = null)
    {
        if (level < EffectiveLevel())
            return;
        var record = new LogRecord { Name = Name, Level = level, Message = message, Exception = ex };
        var handled = false;
        var logger = (LoggerObject?)this;
        while (logger != null)
        {
            foreach (var handler in logger.Handlers)
            {
                handled = true;
                handler.Handle(record);
            }
            if (!logger.Propagate)
                break;
            logger = Logging.Parent(logger);
        }
        // Python logging "lastResort" handler: WARNING+ to stderr when no handlers exist.
        if (!handled && level >= LogLevels.Warning)
        {
            try
            {
                Console.Error.Write(message + "\n");
            }
            catch
            {
                // stderr itself is unwritable (closed/redirected away) — the
                // last-resort handler has no further fallback by definition.
            }
        }
    }

    public void Debug(string message) => Dispatch(LogLevels.Debug, message);

    public void Info(string message) => Dispatch(LogLevels.Info, message);

    public void Warning(string message) => Dispatch(LogLevels.Warning, message);

    public void Error(string message) => Dispatch(LogLevels.Error, message);

    public void Error(string message, Exception? ex) => Dispatch(LogLevels.Error, message, ex);

    /// <summary>
    /// Route a record to the active mode's sink. Seismic mode (the default) goes
    /// through the unchanged handler/propagation path (<see cref="Log"/>),
    /// keeping Seismic output byte-identical. Standard mode routes to
    /// <see cref="Logging.Write"/>, which reproduces Standard's exact text/json
    /// format, run-dir file sink, TestSink/RenderedSink, Event-Log mirror hook
    /// and correlation stamping. The decision is made per call (not cached at
    /// <c>GetLogger</c> time), so a logger obtained before the mode was set still
    /// emits in the mode that is active when it actually logs.
    /// </summary>
    private void Dispatch(int level, string message, Exception? ex = null)
    {
        if (Logging.Mode == LoggingMode.Standard)
        {
            // The exception travels alongside the message rather than folded into
            // it: the active dialect decides whether it belongs on a second line
            // or in a structured field of its own.
            Logging.Write((LogLevel)level, Name, message, ex);
            return;
        }
        Log(level, message, ex);
    }
}

/// <summary>
/// Shared logging entry point. Mirrors how the Python project configures the
/// <c>logging</c> module: console shows WARNING+ by default, all levels with
/// <c>--verbose</c>; the log file always captures everything.
/// </summary>
public static class Logging
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, LoggerObject> Loggers = new();

    /// <summary>The root logger (Python <c>logging.getLogger()</c>). Default level WARNING.</summary>
    public static LoggerObject Root { get; } = new LoggerObject("") { Level = LogLevels.Warning };

    private static bool? _jsonFormat;

    /// <summary>
    /// When true, full-format handlers (everything except <c>MessageOnly</c>
    /// progress-console output) render each record as one JSON object instead of
    /// the <c>"%(asctime)s - %(name)s - %(levelname)s - %(message)s"</c> text line.
    ///
    /// Defaults lazily from <c>LOG_FORMAT=json</c> (case-insensitive) the first
    /// time it is read, so the production logging setup in
    /// <c>CommandRegistry.SetupLogging</c> — which wires handlers directly rather
    /// than through <see cref="Configure"/> — still honours the env var. Explicit
    /// assignment (e.g. from tests) overrides the env; set to <c>null</c> to
    /// restore lazy env resolution. Default resolution keeps output byte-identical
    /// to the historical text format.
    /// </summary>
    public static bool JsonFormat
    {
        get => _jsonFormat ??= ReadJsonFormatFromEnv();
        set => _jsonFormat = value;
    }

    /// <summary>Test seam: re-read <c>LOG_FORMAT</c> on the next <see cref="JsonFormat"/> access.</summary>
    internal static void ResetJsonFormatCache() => _jsonFormat = null;

    /// <summary>Read <c>LOG_FORMAT</c> from the environment (case-insensitive <c>json</c>).</summary>
    private static bool ReadJsonFormatFromEnv() =>
        string.Equals(
            Environment.GetEnvironmentVariable("LOG_FORMAT"),
            "json",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Return the logger named <paramref name="name"/> (Python <c>__name__</c> equivalent).</summary>
    public static IAppLogger GetLogger(string name) => GetLoggerObject(name);

    /// <summary>Same as <see cref="GetLogger"/> but typed for handler/level manipulation.</summary>
    public static LoggerObject GetLoggerObject(string name)
    {
        if (string.IsNullOrEmpty(name))
            return Root;
        lock (SyncRoot)
        {
            if (!Loggers.TryGetValue(name, out var logger))
            {
                logger = new LoggerObject(name);
                Loggers[name] = logger;
            }
            return logger;
        }
    }

    /// <summary>Nearest existing ancestor logger by dotted name, ending at the root.</summary>
    internal static LoggerObject? Parent(LoggerObject logger)
    {
        if (ReferenceEquals(logger, Root))
            return null;
        var name = logger.Name;
        lock (SyncRoot)
        {
            while (true)
            {
                var dot = name.LastIndexOf('.');
                if (dot < 0)
                    return Root;
                name = name[..dot];
                if (Loggers.TryGetValue(name, out var parent))
                    return parent;
            }
        }
    }

    /// <summary>
    /// Configure root logging. Console shows WARNING+ by default and INFO+ with
    /// <paramref name="verbose"/>; when <paramref name="logFilePath"/> is given a
    /// line-rotating file handler captures everything the root logger processes
    /// (INFO+, matching the Python file handler level).
    /// </summary>
    public static void Configure(bool verbose, string? logFilePath = null)
    {
        // Configure is the Seismic entry point — select Seismic mode (the default)
        // so GetLogger loggers use the handler/propagation path and chassis format.
        Mode = LoggingMode.Seismic;
        foreach (var handler in Root.Handlers)
        {
            handler.Close();
            Root.RemoveHandler(handler);
        }
        Root.Level = LogLevels.Info;
        Root.AddHandler(new StreamHandler(Console.Out)
        {
            Level = verbose ? LogLevels.Info : LogLevels.Warning,
        });
        if (logFilePath != null)
        {
            Root.AddHandler(new LineRotatingFileHandler(logFilePath)
            {
                Level = LogLevels.Info,
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Standard surface (additive superset).
    //
    // Reproduces the Standard connector's original Infrastructure.Logging exactly
    // — text format "ts [LEVEL  ] [corr8] logger: msg", json with ISO-8601 'o' UTC
    // timestamp, a logs/{prefix}_{timestamp}/connector.log run directory,
    // ConsoleLevel/EffectiveLevel gating, TestSink/RenderedSink test seams — while
    // keeping the chassis free of any StandardConnector type. The three
    // side-effects the chassis cannot reference (owner-only directory hardening,
    // the Windows Event Log mirror, and the ambient correlation id) are reached
    // through settable hooks the host wires at startup; unset hooks are skipped,
    // so the Seismic path is completely unaffected.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The active logging mode. Defaults to <see cref="LoggingMode.Seismic"/> so
    /// the Seismic connector (which never calls <see cref="Initialize"/> or
    /// <see cref="UseStandardMode"/>) is byte-identical to the pre-merge chassis.
    /// </summary>
    public static LoggingMode Mode { get; private set; } = LoggingMode.Seismic;

    /// <summary>
    /// Select Standard mode without opening a run directory. The Standard host
    /// calls this (and wires the hooks) at startup before the first log line, and
    /// the Standard test assembly calls it from a <c>[ModuleInitializer]</c> so
    /// the whole suite runs in Standard mode even for tests that never call
    /// <see cref="Initialize"/>.
    /// </summary>
    public static void UseStandardMode() => Mode = LoggingMode.Standard;

    /// <summary>
    /// Host hook to harden (create + restrict to owner) a directory. The Standard
    /// host wires this to its <c>DirectoryHardening.EnsureOwnerOnly</c>; when
    /// unset the directory is not created here (Seismic never uses this path).
    /// </summary>
    public static Action<string>? HardenDirectoryHook;

    /// <summary>
    /// Host hook to mirror a record to an external operator channel (the Windows
    /// Event Log). The Standard host wires this to its <c>EventLogSink.Mirror</c>;
    /// when unset the mirror is skipped. Invoked for every Standard-mode record
    /// that passes the <c>LOG_LEVEL</c> floor, matching the original ordering.
    /// </summary>
    public static Action<LogLevel, string, string>? EventLogMirrorHook;

    private static readonly object StandardSync = new();
    private static readonly UTF8Encoding StandardUtf8NoBom = new(false);
    private static StreamWriter? _clarizenFileWriter;

    /// <summary>Standard console threshold: WARNING by default, DEBUG with --verbose.</summary>
    public static LogLevel ConsoleLevel { get; set; } = LogLevel.Warning;

    /// <summary>Current Standard run directory (logs/{prefix}_{timestamp}), or null before Initialize.</summary>
    public static string? RunDirectory { get; private set; }

    /// <summary>Standard root logs directory; settable so tests can redirect.</summary>
    public static string LogsRoot { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "logs");

    /// <summary>Test seam: capture emitted Standard records instead of (in addition to) the sinks.</summary>
    internal static Action<LogLevel, string, string>? TestSink;

    /// <summary>Test seam: receives the fully rendered Standard line (JSON or text) as written to the sinks.</summary>
    internal static Action<string>? RenderedSink;

    /// <summary>True when LOG_FORMAT=json (read at write time so tests can flip it).</summary>
    private static bool StandardJsonFormat =>
        string.Equals(Environment.GetEnvironmentVariable("LOG_FORMAT"), "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The level the hot-path gate advertises, delegated to the active
    /// <see cref="Dialect"/>. See <see cref="DefaultEffectiveLevel"/> for the
    /// chassis default.
    /// </summary>
    public static LogLevel EffectiveLevel() => Dialect.EffectiveLevel();

    /// <summary>
    /// Format and gating policy for the Standard sink. Defaults to the chassis's
    /// historical behaviour; a host with its own log contract assigns a subclass
    /// at startup, alongside <see cref="Chassis.Init"/> and the other hooks.
    /// </summary>
    public static StandardLogDialect Dialect { get; set; } = new();

    /// <summary>
    /// The lowest level any Standard sink currently accepts: the file sink
    /// captures DEBUG+ whenever a run directory is open; otherwise only the
    /// console threshold applies. LOG_LEVEL (DEBUG|INFO|WARNING|ERROR) raises the
    /// floor for both sinks — the hot-path gate honours it.
    /// </summary>
    internal static LogLevel DefaultEffectiveLevel()
    {
        var floor = ParseLevel(Environment.GetEnvironmentVariable("LOG_LEVEL"));
        LogLevel sinks;
        lock (StandardSync)
        {
            sinks = _clarizenFileWriter is not null
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

    /// <summary>
    /// Select Standard mode, create the per-run log directory and open
    /// connector.log. Idempotent — the first caller wins; later calls are no-ops.
    /// The log root and per-run dir are hardened owner-only through
    /// <see cref="HardenDirectoryHook"/> (which also creates them); when the hook
    /// is unset the StreamWriter open fails and logging falls back to console-only.
    /// </summary>
    public static void Initialize(string runPrefix, bool verbose)
    {
        Mode = LoggingMode.Standard;
        ConsoleLevel = verbose ? LogLevel.Debug : LogLevel.Warning;
        lock (StandardSync)
        {
            if (_clarizenFileWriter != null)
                return;
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                // Owner-only (POSIX 0700 / Windows owner+admins) on the log root
                // and per-run dir: connector.log holds ids/errors at rest. The
                // hook both creates and hardens the directory.
                HardenDirectoryHook?.Invoke(LogsRoot);
                var dir = Path.Combine(LogsRoot, $"{runPrefix}_{stamp}");
                HardenDirectoryHook?.Invoke(dir);
                RunDirectory = dir;
                _clarizenFileWriter = new StreamWriter(
                    Path.Combine(dir, "connector.log"), append: true, StandardUtf8NoBom)
                {
                    AutoFlush = true,
                };
            }
            catch (Exception exc)
            {
                // Console-only logging when the log directory cannot be created.
                _clarizenFileWriter = null;
                Console.Error.WriteLine(
                    $"WARNING: could not create log directory under '{LogsRoot}' "
                    + $"({exc.GetType().Name}: {exc.Message}); continuing with console-only logging.");
            }
        }
    }

    /// <summary>Test seam: close the file writer and forget Standard run state.
    /// Leaves the mode, hooks and correlation provider intact so a test that
    /// resets between cases stays in Standard mode with its wiring.</summary>
    internal static void ResetForTests()
    {
        lock (StandardSync)
        {
            _clarizenFileWriter?.Dispose();
            _clarizenFileWriter = null;
            RunDirectory = null;
            TestSink = null;
            RenderedSink = null;
            ConsoleLevel = LogLevel.Warning;
        }
    }

    private static readonly JsonSerializerOptions StandardJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Standard-mode record renderer: reproduces the Standard connector's exact
    /// text and json line formats and sink fan-out. Called only in Standard mode
    /// (via <see cref="LoggerObject"/> dispatch). The ambient correlation id comes
    /// from <see cref="Chassis.CorrelationIdProvider"/> and the Event-Log mirror
    /// from <see cref="EventLogMirrorHook"/>, so the chassis needs no Standard type.
    /// </summary>
    internal static void Write(LogLevel level, string loggerName, string rawMessage, Exception? exception = null)
    {
        // Read once: a dialect swapped mid-write would otherwise gate with one
        // policy and render with another.
        var dialect = Dialect;
        var message = dialect.ComposeMessage(rawMessage, exception);

        if (level < dialect.SinkFloor())
            return;

        TestSink?.Invoke(level, loggerName, message);

        // Stamp the ambient correlation id (per crawl cycle / webhook event) so a
        // single run is followable end-to-end. Null when outside a scope / unset.
        var e = new StandardLogEvent(
            level, loggerName, message, exception, Chassis.CorrelationIdProvider?.Invoke());

        // Optional Windows Event Log mirror (host-wired) — skipped when unset.
        dialect.Mirror(e);

        var line = dialect.Render(e);

        RenderedSink?.Invoke(line);

        lock (StandardSync)
        {
            try
            {
                _clarizenFileWriter?.WriteLine(line);
            }
            catch
            {
                // Logging must never take the process down.
            }
        }

        if (dialect.WritesToConsole(e))
        {
            if (dialect.WritesToStandardError(e))
                Console.Error.WriteLine(line);
            else
                Console.WriteLine(line);
        }
    }

    /// <summary>
    /// The chassis's historical Standard line format: text
    /// <c>"ts [LEVEL  ] [corr8] logger: msg"</c>, or one JSON object per record
    /// under <c>LOG_FORMAT=json</c> with the correlation id stamped as
    /// <c>correlation_id</c> and omitted outside a scope.
    /// </summary>
    internal static string DefaultRender(StandardLogEvent e)
    {
        if (StandardJsonFormat)
        {
            var fields = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["level"] = e.Level.ToString().ToUpperInvariant(),
                ["logger"] = e.Logger,
                ["message"] = e.Message,
            };
            if (e.CorrelationId is not null)
                fields["correlation_id"] = e.CorrelationId;
            return JsonSerializer.Serialize(fields, StandardJsonOptions);
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var prefix = e.CorrelationId is not null ? $"[{e.CorrelationId[..8]}] " : string.Empty;
        return $"{stamp} [{e.Level.ToString().ToUpperInvariant(),-7}] {prefix}{e.Logger}: {e.Message}";
    }
}
