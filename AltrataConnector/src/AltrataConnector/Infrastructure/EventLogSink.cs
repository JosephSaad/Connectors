// Infrastructure/EventLogSink.cs
// ------------------------------
// Windows Event Log mirroring for enterprise fleets whose monitoring is built
// on the Event Log / Windows Event Forwarding rather than on file tails.
//
//   * EVENTLOG_ENABLED=true on Windows → every Logger.Error/Warning line plus
//     lifecycle markers (run/service start & finish) are mirrored to the
//     Application log under source "AltrataConnector".
//   * Non-Windows: a permanent no-op regardless of the flag.
//   * NEVER throws — a missing source, a full log or an ACL problem must not
//     take ingestion down. The install script creates the source idempotently
//     (scripts/install-windows-service.ps1); without it the mirror silently
//     drops (the file/console sinks are unaffected).
//
// PII CAUTION: the Event Log is one more log surface, so the connector's
// PII discipline applies unchanged — mirrored messages are the SAME already
// PII-safe lines the file logger receives (ids / counts / hashes / enums;
// never names, emails or wealth figures). Enforced by the no-PII tests.
//
// Event ids are STABLE and documented in docs/SIEM.md — SIEM rules key on them:
//   1000 lifecycle, 2000 warning, 3000 error.

namespace AltrataConnector.Infrastructure;

/// <summary>Mirror destination abstraction. The real sink writes to the Windows
/// Application log; tests install a capturing fake via <see cref="EventLogSink.Override"/>.</summary>
public interface IEventLogMirror
{
    void Write(EventLogSeverity severity, int eventId, string message);
}

public enum EventLogSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

public static class EventLogSink
{
    public const string EnvVar = "EVENTLOG_ENABLED";

    /// <summary>Registered by scripts/install-windows-service.ps1 (idempotent).</summary>
    public const string SourceName = "AltrataConnector";
    public const string LogName = "Application";

    // Stable event ids — keep docs/SIEM.md and ops/azure-monitor-alerts.kql in sync.
    public const int EventIdLifecycle = 1000;
    public const int EventIdWarning = 2000;
    public const int EventIdError = 3000;

    /// <summary>The Event Log rejects messages beyond ~31 KB; truncate well below.</summary>
    internal const int MaxMessageLength = 30_000;

    /// <summary>Test seam: a non-null mirror replaces the real Windows sink (and
    /// bypasses the Windows platform gate so the dispatch path is testable anywhere).</summary>
    internal static IEventLogMirror? Override;

    /// <summary>Warn exactly once per process when mirroring fails (missing
    /// source, ACL, full log) — never a firehose, never an exception.</summary>
    private static int _writeFailureWarned;

    public static bool Enabled =>
        EnvFlags.IsTrue(EnvVar) && (Override != null || OperatingSystem.IsWindows());

    /// <summary>Mirror one WARNING/ERROR log line. Called from Logging.Emit for
    /// every WARNING/ERROR regardless of sink formatting; a cheap no-op unless
    /// EVENTLOG_ENABLED (and Windows / test override). Never throws.</summary>
    public static void Mirror(string level, string logger, string message, Exception? exception = null)
    {
        if (!Enabled)
            return;
        var severity = level == "ERROR" ? EventLogSeverity.Error : EventLogSeverity.Warning;
        var eventId = level == "ERROR" ? EventIdError : EventIdWarning;
        var text = exception == null
            ? $"{logger}: {message}"
            : $"{logger}: {message} [{exception.GetType().Name}: {exception.Message}]";
        Write(severity, eventId, text);
    }

    /// <summary>Mirror a lifecycle marker (run started / service command started
    /// or finished) as an Information entry with the stable lifecycle id.</summary>
    public static void Lifecycle(string message)
    {
        if (!Enabled)
            return;
        Write(EventLogSeverity.Information, EventIdLifecycle, message);
    }

    private static void Write(EventLogSeverity severity, int eventId, string message)
    {
        if (message.Length > MaxMessageLength)
            message = message[..MaxMessageLength] + "…[truncated]";
        try
        {
            var mirror = Override;
            if (mirror != null)
            {
                mirror.Write(severity, eventId, message);
                return;
            }
            if (OperatingSystem.IsWindows())
                WriteWindows(severity, eventId, message);
        }
        catch (Exception exc)
        {
            // Never throw, never spam: one warning per process, console-only via
            // stderr (going through Logging here could recurse into Mirror).
            if (Interlocked.Exchange(ref _writeFailureWarned, 1) == 0)
            {
                Console.Error.WriteLine(
                    $"WARNING: Windows Event Log mirroring failed ({exc.GetType().Name}: {exc.Message}) — " +
                    $"file/console logging unaffected. Create the event source '{SourceName}' " +
                    "(scripts/install-windows-service.ps1 does this) or unset EVENTLOG_ENABLED.");
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WriteWindows(EventLogSeverity severity, int eventId, string message)
    {
        var type = severity switch
        {
            EventLogSeverity.Error => System.Diagnostics.EventLogEntryType.Error,
            EventLogSeverity.Warning => System.Diagnostics.EventLogEntryType.Warning,
            _ => System.Diagnostics.EventLogEntryType.Information,
        };
        System.Diagnostics.EventLog.WriteEntry(SourceName, message, type, eventId);
    }
}
