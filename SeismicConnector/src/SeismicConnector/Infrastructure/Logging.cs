// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SeismicConnector.Infrastructure;

/// <summary>
/// Application logger contract shared by every ported module.
///
/// Mirrors Python <c>logging</c>: console shows WARNING+ by default, everything with
/// <c>--verbose</c>; a log file always captures all levels.
/// </summary>
public interface IAppLogger
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
    void Error(string message, Exception ex);

    /// <summary>
    /// Whether a record at <paramref name="level"/> would be processed
    /// (Python <c>Logger.isEnabledFor</c>). Callers use it to skip building
    /// expensive log messages that would be dropped by the level check.
    /// </summary>
    bool IsEnabledFor(int level);
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
            var correlationId = Tracing.CurrentCorrelationId;
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

    public void Debug(string message) => Log(LogLevels.Debug, message);

    public void Info(string message) => Log(LogLevels.Info, message);

    public void Warning(string message) => Log(LogLevels.Warning, message);

    public void Error(string message) => Log(LogLevels.Error, message);

    public void Error(string message, Exception ex) => Log(LogLevels.Error, message, ex);
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
}
