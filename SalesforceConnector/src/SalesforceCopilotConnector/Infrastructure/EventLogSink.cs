// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/EventLogSink.cs
// ------------------------------
// Windows Event Log mirroring for enterprise monitoring (SCOM/SIEM pickup).
//
// When EVENTLOG_ENABLED=true AND the process is running on Windows, a sink is
// attached to the root logger that mirrors WARNING/ERROR records — plus the
// service lifecycle events logged by ServiceHost — to the Windows Application
// event log (source "SalesforceConnector"). INFO records are NOT mirrored by
// default; EVENTLOG_LEVEL=info opts them in.
//
// Design constraints:
//   * Logging must never kill the process: the sink never throws, no matter
//     what the OS event-log API does (missing source, ACL denial, full log).
//   * Non-Windows is a strict no-op — AttachIfEnabled() returns without doing
//     anything, so Linux/macOS behavior is byte-identical with the flag set.
//   * The actual OS write goes through the IEventLogWriter seam so tests can
//     assert dispatch and level mapping without a Windows event log.
//
// The event source is created by scripts/install-windows-service.ps1 (admin,
// idempotent). If the source does not exist at runtime the first write attempt
// fails and is swallowed — create the source rather than running the service
// as an administrator. Event ids and levels are documented in docs/SIEM.md.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>Severity of a mirrored event-log entry (platform-neutral).</summary>
public enum EventLogEntrySeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// Seam between <see cref="EventLogSink"/> and the OS event log, so tests can
/// assert dispatch (and level mapping) without Windows.
/// </summary>
public interface IEventLogWriter
{
    /// <summary>Write one entry. Implementations may throw; the sink swallows.</summary>
    void Write(EventLogEntrySeverity severity, string message, int eventId);
}

/// <summary>
/// Log handler that mirrors WARNING/ERROR (and service lifecycle) records to
/// the Windows Application event log. Attach via <see cref="AttachIfEnabled"/>;
/// never throws from <see cref="Emit"/>.
/// </summary>
public sealed class EventLogSink : LogHandler
{
    /// <summary>Event source name registered in the Application log.</summary>
    public const string SourceName = "SalesforceConnector";

    /// <summary>Windows log the source is registered under.</summary>
    public const string LogName = "Application";

    /// <summary>Logger whose INFO records are always mirrored (service start/stop).</summary>
    public const string ServiceLoggerName = "salesforce_connector.service";

    // Stable event ids (documented in docs/SIEM.md — SIEM rules key on these).
    public const int EventIdInformation = 1000;
    public const int EventIdWarning = 2000;
    public const int EventIdError = 3000;

    // Windows caps event-log message strings at 31,839 characters.
    internal const int MaxMessageLength = 31_000;

    private readonly IEventLogWriter _writer;
    private readonly bool _mirrorInfo;

    /// <summary>The sink currently attached to the root logger (null when detached).</summary>
    private static EventLogSink? _attached;

    internal EventLogSink(IEventLogWriter writer, bool mirrorInfo)
    {
        _writer = writer;
        _mirrorInfo = mirrorInfo;
    }

    /// <summary><c>EVENTLOG_ENABLED</c> — mirror to the Windows Event Log. Default false.</summary>
    public static bool Enabled => EnvFlags.IsTrue("EVENTLOG_ENABLED");

    /// <summary><c>EVENTLOG_LEVEL=info</c> — also mirror INFO records. Default warning+.</summary>
    internal static bool MirrorInfoFromEnv =>
        string.Equals(
            Environment.GetEnvironmentVariable("EVENTLOG_LEVEL"),
            "info",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attach the sink to the root logger when <c>EVENTLOG_ENABLED=true</c> and the
    /// OS is Windows; a strict no-op otherwise. Idempotent — safe to call from every
    /// <c>SetupLogging</c> (including each --continuous cycle) and from ServiceHost.
    /// </summary>
    public static void AttachIfEnabled()
    {
        if (!Enabled || !OperatingSystem.IsWindows())
            return;
        try
        {
            _attached ??= new EventLogSink(new WindowsEventLogWriter(), MirrorInfoFromEnv);
            Logging.Root.AddHandler(_attached);  // AddHandler dedupes by reference
        }
        catch
        {
            // Never let event-log wiring take down logging (or the process).
        }
    }

    /// <summary>Test seam: detach and forget the attached sink.</summary>
    internal static void DetachForTests()
    {
        if (_attached != null)
        {
            Logging.Root.RemoveHandler(_attached);
            _attached = null;
        }
    }

    /// <summary>Map the project's Python-style numeric level to an entry severity.</summary>
    internal static EventLogEntrySeverity SeverityFor(int level) => level switch
    {
        >= LogLevels.Error => EventLogEntrySeverity.Error,
        >= LogLevels.Warning => EventLogEntrySeverity.Warning,
        _ => EventLogEntrySeverity.Information,
    };

    /// <summary>Stable event id per severity (see docs/SIEM.md).</summary>
    internal static int EventIdFor(EventLogEntrySeverity severity) => severity switch
    {
        EventLogEntrySeverity.Error => EventIdError,
        EventLogEntrySeverity.Warning => EventIdWarning,
        _ => EventIdInformation,
    };

    protected override void Emit(LogRecord record)
    {
        try
        {
            // WARNING+ always; INFO only for the service lifecycle logger or
            // with the EVENTLOG_LEVEL=info opt-in. DEBUG is never mirrored.
            var mirror = record.Level >= LogLevels.Warning
                || (record.Level >= LogLevels.Info
                    && (_mirrorInfo || record.Name == ServiceLoggerName));
            if (!mirror)
                return;

            var severity = SeverityFor(record.Level);
            var message = Format(record);
            if (message.Length > MaxMessageLength)
                message = message[..MaxMessageLength] + " …[truncated]";
            _writer.Write(severity, message, EventIdFor(severity));
        }
        catch
        {
            // A failed mirror write must never surface to the code that logged.
        }
    }
}

/// <summary>
/// Real OS writer: <see cref="EventLog.WriteEntry(string, string, EventLogEntryType, int)"/>
/// against the Application log. Only constructed behind an
/// <see cref="OperatingSystem.IsWindows"/> guard.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsEventLogWriter : IEventLogWriter
{
    public void Write(EventLogEntrySeverity severity, string message, int eventId)
    {
        var entryType = severity switch
        {
            EventLogEntrySeverity.Error => EventLogEntryType.Error,
            EventLogEntrySeverity.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        };
        // Throws when the source is missing/inaccessible — EventLogSink.Emit
        // swallows it (the sink's contract is "never throw").
        EventLog.WriteEntry(EventLogSink.SourceName, message, entryType, eventId);
    }
}
