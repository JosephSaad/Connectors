// Infrastructure/EventLogSink.cs
// ------------------------------
// Optional Windows Event Log mirror for the connector's own logger.
//
// Gated on EVENTLOG_ENABLED=true AND OperatingSystem.IsWindows(): everywhere
// else (and by default) the sink is a strict no-op. When enabled it mirrors
//   • Logger.Error   → Event Log Error   (event id 3000)
//   • Logger.Warning → Event Log Warning (event id 2000)
//   • Logger.Info    → Event Log Information (event id 1000) ONLY with
//     EVENTLOG_LEVEL=info (Debug is never mirrored — the Event Log is an
//     operator channel, not a trace sink)
//   • service lifecycle (SCM start/stop/finish, via ServiceLifecycle()) →
//     Information with dedicated ids 1001/1002, mirrored regardless of
//     EVENTLOG_LEVEL so SIEMs always see the service state transitions.
//
// Writes go to source "ClarizenConnector" in the "Application" log. The source
// is created idempotently by scripts/install-windows-service.ps1 (creation
// needs elevation; WriteEntry does not once the source exists). Event ids and
// levels are the SIEM contract — documented in docs/SIEM.md.
//
// The sink must NEVER throw into the logging path or take the process down:
// every dispatch is wrapped, and the first write failure disables the sink for
// the process lifetime (one line on stderr — not through the logger, which
// would recurse).
//
// IEventLogWriter is the test seam: tests inject a fake writer and assert the
// dispatch rules without a real Windows Event Log.

using System.Runtime.Versioning;

namespace ClarizenConnector.Infrastructure;

/// <summary>Event Log entry severity, platform-neutral (test-assertable off Windows).</summary>
public enum EventLogEntryKind
{
    Information,
    Warning,
    Error,
}

/// <summary>Destination abstraction for Event Log writes (fake in tests, real on Windows).</summary>
public interface IEventLogWriter
{
    void Write(string message, EventLogEntryKind kind, int eventId);
}

public static class EventLogSink
{
    public const string EnabledEnvVar = "EVENTLOG_ENABLED";
    public const string LevelEnvVar = "EVENTLOG_LEVEL";

    /// <summary>Event source name registered in the "Application" log.</summary>
    public const string Source = "ClarizenConnector";
    public const string LogName = "Application";

    // Stable event ids — the SIEM contract (docs/SIEM.md). Do not renumber.
    public const int EventIdInfo = 1000;
    public const int EventIdServiceStart = 1001;
    public const int EventIdServiceStop = 1002;
    public const int EventIdWarning = 2000;
    public const int EventIdError = 3000;

    /// <summary>Event Log entries cap out near 31839 chars; truncate below that.</summary>
    private const int MaxMessageChars = 31_000;

    private static readonly object Sync = new();
    private static IEventLogWriter? _writer;
    private static bool _writerResolved;
    private static volatile bool _faulted;

    /// <summary>Test seam: route dispatches to a fake writer (bypasses the Windows gate).</summary>
    internal static void OverrideWriter(IEventLogWriter? writer)
    {
        lock (Sync)
        {
            _writer = writer;
            _writerResolved = writer is not null;
            _faulted = false;
        }
    }

    internal static void ResetForTests() => OverrideWriter(null);

    /// <summary>True when EVENTLOG_ENABLED is truthy (read per dispatch, like LOG_LEVEL).</summary>
    internal static bool Enabled => EnvFlags.IsTrue(EnabledEnvVar);

    /// <summary>True when EVENTLOG_LEVEL=info opts Info records into the mirror.</summary>
    internal static bool InfoEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(LevelEnvVar), "info", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mirror one logger record. Called by Logging.Write for every record that
    /// passed the LOG_LEVEL floor; all gating (enabled flag, level rules,
    /// platform) happens here. Never throws.
    /// </summary>
    public static void Mirror(LogLevel level, string loggerName, string message)
    {
        try
        {
            if (_faulted || !Enabled)
                return;
            if (level < LogLevel.Info)
                return;  // Debug is never mirrored
            if (level < LogLevel.Warning && !InfoEnabled)
                return;  // Info only with EVENTLOG_LEVEL=info

            var (kind, eventId) = level >= LogLevel.Error
                ? (EventLogEntryKind.Error, EventIdError)
                : level >= LogLevel.Warning
                    ? (EventLogEntryKind.Warning, EventIdWarning)
                    : (EventLogEntryKind.Information, EventIdInfo);

            Dispatch($"{loggerName}: {message}", kind, eventId);
        }
        catch
        {
            // The mirror must never break the logging path.
        }
    }

    /// <summary>
    /// Mirror a service lifecycle transition (SCM start/stop/finish) as an
    /// Information entry with a dedicated event id — always mirrored when the
    /// sink is enabled, regardless of EVENTLOG_LEVEL. Never throws.
    /// </summary>
    public static void ServiceLifecycle(string message, bool starting)
    {
        try
        {
            if (_faulted || !Enabled)
                return;
            Dispatch(
                message,
                EventLogEntryKind.Information,
                starting ? EventIdServiceStart : EventIdServiceStop);
        }
        catch
        {
            // Never throw into the service lifecycle.
        }
    }

    private static void Dispatch(string message, EventLogEntryKind kind, int eventId)
    {
        var writer = ResolveWriter();
        if (writer is null)
            return;  // non-Windows (or construction failed) — strict no-op

        if (message.Length > MaxMessageChars)
            message = message[..MaxMessageChars] + "…[truncated]";

        try
        {
            writer.Write(message, kind, eventId);
        }
        catch (Exception exc)
        {
            // First failure disables the sink for the process lifetime. Report
            // on stderr directly — going through the logger would recurse back
            // into this sink.
            _faulted = true;
            Console.Error.WriteLine(
                $"WARNING: Windows Event Log sink disabled after a write failure "
                + $"({exc.GetType().Name}: {exc.Message}). File/console logging is unaffected.");
        }
    }

    private static IEventLogWriter? ResolveWriter()
    {
        if (_writerResolved)
            return _writer;
        lock (Sync)
        {
            if (_writerResolved)
                return _writer;
            _writer = CreateDefaultWriter();
            _writerResolved = true;
            return _writer;
        }
    }

    private static IEventLogWriter? CreateDefaultWriter()
    {
        if (!OperatingSystem.IsWindows())
            return null;  // documented no-op off Windows
        try
        {
            return new WindowsEventLogWriter();
        }
        catch
        {
            return null;  // constructing the writer must never throw upward
        }
    }

    /// <summary>Real Windows writer. The source must already exist (created
    /// idempotently by scripts/install-windows-service.ps1); WriteEntry on a
    /// missing source would need admin rights to auto-create it and throws
    /// otherwise — that failure path trips the sink's disable latch.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class WindowsEventLogWriter : IEventLogWriter
    {
        public void Write(string message, EventLogEntryKind kind, int eventId)
        {
            using var log = new System.Diagnostics.EventLog(LogName) { Source = Source };
            log.WriteEntry(
                message,
                kind switch
                {
                    EventLogEntryKind.Error => System.Diagnostics.EventLogEntryType.Error,
                    EventLogEntryKind.Warning => System.Diagnostics.EventLogEntryType.Warning,
                    _ => System.Diagnostics.EventLogEntryType.Information,
                },
                eventId);
        }
    }
}
