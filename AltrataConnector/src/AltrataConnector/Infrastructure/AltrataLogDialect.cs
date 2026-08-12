// Infrastructure/AltrataLogDialect.cs
// -----------------------------------
// This connector's log contract, expressed against the shared chassis sink.
//
// The chassis owns the plumbing (run directory, owner-only hardening, the file
// writer and its lock, console fan-out); this type owns the four things an
// operator can actually see, and which this connector settled on long before the
// chassis existed:
//
//   * DEBUG is opt-in via LOG_LEVEL=debug and nothing else. Every other level
//     always reaches the sinks. The chassis default instead treats LOG_LEVEL as a
//     floor that only ever raises, which would put DEBUG in connector.log on
//     every ordinary run and defeat the IsEnabledFor hot-path skip.
//   * JSON lines stamp `correlationId`, present on EVERY line (null outside a
//     scope) so a consumer can index the field without a schema surprise. The
//     chassis default stamps `correlation_id` and omits it when null.
//   * The exception is its own `exception` field in JSON, and its own line in
//     text. The chassis default folds it into the message.
//   * WARNING goes to stdout and only ERROR to stderr; the Event Log mirror takes
//     WARNING and above with the exception type and message appended, never the
//     whole stack trace.
//
// Wired in Program.WireChassis, and in the test assembly's module initializer.

using System.Text.Json;
using Connector.Chassis;

namespace AltrataConnector.Infrastructure;

/// <summary>Standard-sink policy reproducing this connector's original log output.</summary>
internal sealed class AltrataLogDialect : StandardLogDialect
{
    /// <summary>
    /// DEBUG is opt-in via LOG_LEVEL=debug (case-insensitive). Off by default, so
    /// Debug() calls are cheap no-ops and hot-path callers skip expensive message
    /// formatting via <see cref="IAppLogger.IsEnabledFor(LogLevel)"/>.
    /// </summary>
    internal static bool DebugEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("LOG_LEVEL"), "debug",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True when LOG_FORMAT=json (case-insensitive), read per record so tests can flip it.</summary>
    private static bool JsonFormat =>
        string.Equals(Environment.GetEnvironmentVariable("LOG_FORMAT"), "json",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>DEBUG when enabled, otherwise INFO — the gate and its advertisement are the same here.</summary>
    public override LogLevel EffectiveLevel() => DebugEnabled ? LogLevel.Debug : LogLevel.Info;

    /// <inheritdoc cref="EffectiveLevel"/>
    public override LogLevel SinkFloor() => EffectiveLevel();

    /// <summary>The exception is rendered in its own field/line, so it stays out of the message.</summary>
    public override string ComposeMessage(string message, Exception? exception) => message;

    /// <summary>
    /// WARNING and above only, with the exception passed through so the entry
    /// carries a compact <c>[Type: message]</c> suffix rather than a stack trace.
    /// </summary>
    public override void Mirror(StandardLogEvent e)
    {
        if (e.Level >= LogLevel.Warning)
            EventLogSink.Mirror(
                e.Level.ToString().ToUpperInvariant(), e.Logger, e.Message, e.Exception);
    }

    public override string Render(StandardLogEvent e)
    {
        if (JsonFormat)
        {
            // Correlation id stamped on every structured line so a whole cycle is
            // followable end-to-end; null outside a crawl/erasure scope.
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["level"] = e.Level.ToString().ToUpperInvariant(),
                ["logger"] = e.Logger,
                ["correlationId"] = e.CorrelationId,
                ["message"] = e.Message,
                ["exception"] = e.Exception?.ToString(),
            });
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"{stamp} [{e.Level.ToString().ToUpperInvariant(),-7}] {e.Logger}: {e.Message}";
        return e.Exception is null ? line : line + Environment.NewLine + e.Exception;
    }

    /// <summary>WARNING/ERROR always; everything else only under --verbose.</summary>
    public override bool WritesToConsole(StandardLogEvent e) =>
        e.Level >= LogLevel.Warning || ConsoleVerbosity.Verbose;

    /// <summary>Only ERROR is stderr — a WARNING is not a failure of the command.</summary>
    public override bool WritesToStandardError(StandardLogEvent e) => e.Level == LogLevel.Error;
}
