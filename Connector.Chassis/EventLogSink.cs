// Infrastructure/EventLogSink.cs
// ------------------------------
// Windows Event Log mirror for the operational log stream (docs/SIEM.md).
//
// Gated on EVENTLOG_ENABLED=true (truthy semantics shared with EnvFlags) AND
// running on Windows: when active, a LogHandler attached to the root logger
// mirrors every Logger.Error as an Error entry and every Logger.Warning as a
// Warning entry to the Application log under source "SeismicConnector".
// EVENTLOG_LEVEL=info additionally mirrors Info records. Lifecycle events
// (service/command start-stop) are written as Information entries with a
// dedicated event id regardless of EVENTLOG_LEVEL, so a SIEM always sees
// start/stop even at the default level.
//
// Event ids (stable — referenced by docs/SIEM.md and ops/ alert rules):
//   1000  lifecycle (start/stop/install)     Information
//   1100  mirrored INFO record               Information
//   2000  mirrored WARNING record            Warning
//   3000  mirrored ERROR record              Error
//
// Non-Windows: the sink is a no-op (CreateIfConfigured returns null) unless a
// test writer is injected. The sink NEVER throws into the logging pipeline —
// a broken Event Log must not take down a crawl — and never logs its own
// failures through the root logger (that would recurse straight back into
// this handler).
//
// The event source is created idempotently by scripts/install-windows-service.ps1
// (source creation needs admin; the service account only needs write).

using System.Diagnostics;

namespace Connector.Chassis;

/// <summary>Severity of one Event Log entry (platform-neutral for tests).</summary>
public enum EventLogEntryKind
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// Writer seam behind <see cref="EventLogSink"/>: production uses
/// <see cref="WindowsEventLogWriter"/>; tests inject a fake to observe entries
/// on any platform.
/// </summary>
public interface IEventLogWriter
{
    /// <summary>Write one entry. Implementations may throw; the sink swallows.</summary>
    void WriteEntry(string message, EventLogEntryKind kind, int eventId);
}

/// <summary>Real Windows Event Log writer (Application log, source "SeismicConnector").</summary>
public sealed class WindowsEventLogWriter : IEventLogWriter
{
    public void WriteEntry(string message, EventLogEntryKind kind, int eventId)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var type = kind switch
        {
            EventLogEntryKind.Error => EventLogEntryType.Error,
            EventLogEntryKind.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        };
        EventLog.WriteEntry(EventLogSink.Source, message, type, eventId);
    }
}

/// <summary>
/// Root-logger handler that mirrors log records into the Windows Event Log.
/// Attach via <see cref="InstallOnRoot"/>; write lifecycle marks via
/// <see cref="Lifecycle"/>. Never throws.
/// </summary>
public sealed class EventLogSink : LogHandler
{
    public const string EnabledEnvVar = "EVENTLOG_ENABLED";

    /// <summary>"info" mirrors INFO records too; anything else mirrors WARNING+.</summary>
    public const string LevelEnvVar = "EVENTLOG_LEVEL";

    /// <summary>Windows Event Log source (per-connector; supplied via <see cref="Chassis.Identity"/>).</summary>
    public static string Source => Chassis.Identity.EventLogSource;
    public const string LogName = "Application";

    // Stable event ids — docs/SIEM.md is the reference.
    public const int LifecycleEventId = 1000;
    public const int InfoEventId = 1100;
    public const int WarningEventId = 2000;
    public const int ErrorEventId = 3000;

    /// <summary>Windows event log entries are capped near 31,839 chars; stay under it.</summary>
    internal const int MaxMessageChars = 31_000;

    /// <summary>Test seam: non-null replaces the platform writer on ANY OS.</summary>
    internal static IEventLogWriter? OverrideWriter;

    /// <summary>Consecutive writer failures observed (diagnostics/tests; never thrown).</summary>
    internal int WriteFailures;

    private readonly IEventLogWriter _writer;

    internal EventLogSink(IEventLogWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Build the sink when EVENTLOG_ENABLED is truthy and the platform (or an
    /// injected test writer) supports it; null otherwise. Handler level is
    /// WARNING by default, INFO with EVENTLOG_LEVEL=info.
    /// </summary>
    public static EventLogSink? CreateIfConfigured()
    {
        if (!IsEnvTrue(EnabledEnvVar))
            return null;
        var writer = OverrideWriter;
        if (writer is null)
        {
            if (!OperatingSystem.IsWindows())
                return null;  // non-Windows: silent no-op by contract
            writer = new WindowsEventLogWriter();
        }
        var mirrorInfo = string.Equals(
            Environment.GetEnvironmentVariable(LevelEnvVar), "info", StringComparison.OrdinalIgnoreCase);
        return new EventLogSink(writer)
        {
            Level = mirrorInfo ? LogLevels.Info : LogLevels.Warning,
        };
    }

    /// <summary>
    /// Truthy env-var read (<c>true</c>/<c>1</c>/<c>yes</c>, case-insensitive;
    /// unset → false). Kept local so the chassis stays free of the connector's
    /// Settings-coupled flag helper while preserving identical semantics.
    /// </summary>
    private static bool IsEnvTrue(string name)
    {
        var value = (Environment.GetEnvironmentVariable(name) ?? "false").ToLowerInvariant();
        return value is "true" or "1" or "yes";
    }

    /// <summary>
    /// Attach a configured sink to the root logger (idempotent per SetupLogging
    /// cycle — the caller clears root handlers first). Returns the sink or null.
    /// </summary>
    public static EventLogSink? InstallOnRoot()
    {
        var sink = CreateIfConfigured();
        if (sink is not null)
            Logging.Root.AddHandler(sink);
        return sink;
    }

    /// <summary>
    /// Write a lifecycle Information entry (event id 1000) when the sink is
    /// configured — independent of EVENTLOG_LEVEL, so start/stop marks always
    /// reach the SIEM. Never throws.
    /// </summary>
    public static void Lifecycle(string message)
    {
        var sink = CreateIfConfigured();
        sink?.WriteSafe(message, EventLogEntryKind.Information, LifecycleEventId);
    }

    protected override void Emit(LogRecord record)
    {
        var (kind, eventId) = record.Level switch
        {
            >= LogLevels.Error => (EventLogEntryKind.Error, ErrorEventId),
            >= LogLevels.Warning => (EventLogEntryKind.Warning, WarningEventId),
            _ => (EventLogEntryKind.Information, InfoEventId),
        };
        var message = record.Exception is null
            ? record.Message
            : record.Message + "\n" + record.Exception;
        WriteSafe($"[{record.Name}] {message}", kind, eventId);
    }

    /// <summary>Truncate + write, swallowing every failure (missing source,
    /// permission denied, broken log service). Never logs through the root
    /// logger — that would recurse into this handler.</summary>
    private void WriteSafe(string message, EventLogEntryKind kind, int eventId)
    {
        try
        {
            if (message.Length > MaxMessageChars)
                message = message[..MaxMessageChars] + "…[truncated]";
            _writer.WriteEntry(message, kind, eventId);
        }
        catch
        {
            Interlocked.Increment(ref WriteFailures);
        }
    }
}
