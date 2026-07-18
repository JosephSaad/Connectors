// Infrastructure/EventLogSink.cs
// ------------------------------
// Windows Event Log mirroring for enterprise SIEM pipelines (docs/SIEM.md).
//
// With EVENTLOG_ENABLED=true on Windows, every Logger.Error / Logger.Warning
// line is mirrored to the Application event log under source "HadoopConnector"
// (EVENTLOG_LEVEL=info additionally mirrors Info), alongside two lifecycle
// events (start/stop) so agent collectors can alert on the connector dying.
// The file/console/JSON logging pipeline is unchanged — the event log is a
// mirror, never a replacement.
//
// Guarantees:
//   • OFF by default; a strict no-op unless EVENTLOG_ENABLED is truthy.
//   • No-op on non-Windows platforms (the env var may stay set in a shared
//     .env used across Linux dev and Windows Server prod).
//   • NEVER throws: a broken/missing event source, a full log, or a writer
//     crash is swallowed (with a one-time stderr note) — logging must never
//     take the connector down, and the primary log pipeline still has the line.
//   • Registration of the event source itself is done at INSTALL time
//     (scripts/install-windows-service.ps1 — requires elevation); at runtime
//     the sink only writes.
//
// Stable event ids (documented in docs/SIEM.md; keep in sync):
//   1000 lifecycle start   1001 lifecycle stop
//   1100 info (EVENTLOG_LEVEL=info only)
//   2000 warning           3000 error

using System.Diagnostics;
using System.Runtime.Versioning;

namespace HadoopConnector.Infrastructure;

/// <summary>Entry severity as written to the event log (mirrors
/// <see cref="EventLogEntryType"/> without binding callers to System.Diagnostics).</summary>
public enum EventLogEntryLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>Writer seam so dispatch is testable offline (and off-Windows).</summary>
public interface IEventLogWriter : IDisposable
{
    void WriteEntry(string message, EventLogEntryLevel level, int eventId);
}

/// <summary>Production writer: System.Diagnostics.EventLog → Application log.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsEventLogWriter : IEventLogWriter
{
    private readonly EventLog _log;

    public WindowsEventLogWriter(string source, string logName)
    {
        _log = new EventLog(logName) { Source = source };
    }

    public void WriteEntry(string message, EventLogEntryLevel level, int eventId)
    {
        // The Event Log API caps one entry at 31,839 chars; trim defensively.
        if (message.Length > 31_000)
            message = message[..31_000] + " …(truncated)";
        _log.WriteEntry(message, level switch
        {
            EventLogEntryLevel.Error => EventLogEntryType.Error,
            EventLogEntryLevel.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        }, eventId);
    }

    public void Dispose() => _log.Dispose();
}

public static class EventLogSink
{
    public const string SourceName = "HadoopConnector";
    public const string LogName = "Application";

    // Stable event ids — documented in docs/SIEM.md.
    public const int EventIdLifecycleStart = 1000;
    public const int EventIdLifecycleStop = 1001;
    public const int EventIdInfo = 1100;
    public const int EventIdWarning = 2000;
    public const int EventIdError = 3000;

    private static readonly object Sync = new();
    private static IEventLogWriter? _writer;
    private static bool _warnedBroken;

    /// <summary>Test seam: injected writer makes the sink active regardless of
    /// OS (EVENTLOG_ENABLED must still be truthy).</summary>
    internal static IEventLogWriter? OverrideWriter { get; set; }

    /// <summary>True once Initialize() activated the sink.</summary>
    public static bool Enabled
    {
        get
        {
            lock (Sync)
            {
                return _writer is not null;
            }
        }
    }

    /// <summary>EVENTLOG_LEVEL=info additionally mirrors Info lines (default: warnings + errors only).</summary>
    internal static bool MirrorInfo =>
        string.Equals(Environment.GetEnvironmentVariable("EVENTLOG_LEVEL"), "info",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Activate the sink when EVENTLOG_ENABLED is truthy and the platform is
    /// Windows (or a test writer is injected). Writes the lifecycle-start
    /// event. Idempotent; never throws.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            if (!EnvFlags.IsTrue("EVENTLOG_ENABLED"))
                return;
            lock (Sync)
            {
                if (_writer is not null)
                    return;
                if (OverrideWriter is not null)
                {
                    _writer = OverrideWriter;
                }
                else if (OperatingSystem.IsWindows())
                {
                    _writer = new WindowsEventLogWriter(SourceName, LogName);
                }
                else
                {
                    return;  // non-Windows: documented no-op
                }
            }
            Write(EventLogEntryLevel.Information, EventIdLifecycleStart,
                $"HadoopConnector starting (pid {Environment.ProcessId}).");
        }
        catch (Exception exc)
        {
            // Initialization failed (e.g. the event source was never registered
            // — run scripts/install-windows-service.ps1 elevated). Disable the
            // sink; the primary logging pipeline is unaffected.
            lock (Sync)
            {
                _writer = null;
            }
            WarnOnce(exc);
        }
    }

    /// <summary>Write the lifecycle-stop event and release the writer. Never throws.</summary>
    public static void Shutdown()
    {
        IEventLogWriter? writer;
        lock (Sync)
        {
            writer = _writer;
            _writer = null;
        }
        if (writer is null)
            return;
        try
        {
            writer.WriteEntry(
                $"HadoopConnector stopping (pid {Environment.ProcessId}).",
                EventLogEntryLevel.Information, EventIdLifecycleStop);
        }
        catch (Exception exc)
        {
            WarnOnce(exc);
        }
        try
        {
            // The test seam owns (and asserts on) an injected writer; only a
            // writer this sink created is disposed here.
            if (!ReferenceEquals(writer, OverrideWriter))
                writer.Dispose();
        }
        catch
        {
            // never throws
        }
    }

    /// <summary>
    /// Mirror one log line (called from <see cref="Logging.Write"/> for every
    /// record). Dispatch: Error→Error, Warning→Warning, Info→Information only
    /// when EVENTLOG_LEVEL=info, Debug never. Never throws.
    /// </summary>
    internal static void Mirror(LogLevel level, string loggerName, string message)
    {
        if (_writer is null)
            return;
        var (entryLevel, eventId) = level switch
        {
            LogLevel.Error => (EventLogEntryLevel.Error, EventIdError),
            LogLevel.Warning => (EventLogEntryLevel.Warning, EventIdWarning),
            LogLevel.Info when MirrorInfo => (EventLogEntryLevel.Information, EventIdInfo),
            _ => ((EventLogEntryLevel)(-1), 0),
        };
        if (eventId == 0)
            return;
        Write(entryLevel, eventId, $"{loggerName}: {message}");
    }

    private static void Write(EventLogEntryLevel level, int eventId, string message)
    {
        IEventLogWriter? writer;
        lock (Sync)
        {
            writer = _writer;
        }
        if (writer is null)
            return;
        try
        {
            writer.WriteEntry(message, level, eventId);
        }
        catch (Exception exc)
        {
            WarnOnce(exc);
        }
    }

    private static void WarnOnce(Exception exc)
    {
        lock (Sync)
        {
            if (_warnedBroken)
                return;
            _warnedBroken = true;
        }
        try
        {
            // Console.Error directly — routing through Logging here would
            // re-enter Mirror() on the very path that just failed.
            Console.Error.WriteLine(
                "WARNING: Windows Event Log mirroring failed "
                + $"({exc.GetType().Name}: {exc.Message}); continuing without it. "
                + "Register the event source with scripts/install-windows-service.ps1 (elevated).");
        }
        catch
        {
            // never throws
        }
    }

    /// <summary>Test seam: deactivate and forget state.</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _writer = null;
            _warnedBroken = false;
        }
        OverrideWriter = null;
    }
}
