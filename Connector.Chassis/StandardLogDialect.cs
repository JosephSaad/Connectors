// Connector.Chassis/StandardLogDialect.cs
// ---------------------------------------
// Host-selectable output policy for Standard-mode logging.
//
// The Standard sink in Logging.cs owns the PLUMBING every connector shares: the
// per-run log directory, owner-only hardening, the file writer and its lock, the
// TestSink/RenderedSink seams, and the console fan-out. What it cannot own is the
// FORMAT and GATING policy, because those are observable contracts that predate
// the chassis and differ between connectors — the Clarizen/Hadoop lineage stamps
// `correlation_id` and treats LOG_LEVEL as a floor that only ever raises, while
// the Altrata lineage stamps `correlationId` (always present, null outside a
// scope), carries the exception in its own field, and treats LOG_LEVEL=debug as
// the single switch that admits DEBUG at all.
//
// Those are not cosmetic differences: they are what an operator's log queries and
// alert rules are written against. Rather than pick a winner and silently change
// one connector's diagnosability, the sink consults a dialect. The base class IS
// the Clarizen/Hadoop behaviour, so a host that wires nothing behaves exactly as
// it did before this type existed; a host with its own contract overrides only
// the members that differ.

namespace Connector.Chassis;

/// <summary>
/// One Standard-mode record as the sink sees it, before rendering. The exception
/// is carried separately from the message so a dialect can place it in its own
/// structured field rather than folding it into the message text.
/// </summary>
public sealed record StandardLogEvent(
    LogLevel Level,
    string Logger,
    string Message,
    Exception? Exception,
    string? CorrelationId);

/// <summary>
/// Format and gating policy for the Standard log sink. The defaults reproduce the
/// chassis's historical Standard behaviour exactly; override only what differs.
/// </summary>
public class StandardLogDialect
{
    /// <summary>
    /// The level the hot-path gate (<see cref="IAppLogger.IsEnabledFor(LogLevel)"/>)
    /// advertises, so a caller can skip expensive message formatting for a record
    /// that would be dropped.
    /// </summary>
    public virtual LogLevel EffectiveLevel() => Logging.DefaultEffectiveLevel();

    /// <summary>
    /// The lowest level the sink accepts. Records below it are dropped before any
    /// seam, mirror, render or write — this is the gate, <see cref="EffectiveLevel"/>
    /// is only its advertisement, and the two are separate because the historical
    /// default advertises a higher level than it actually enforces.
    /// </summary>
    public virtual LogLevel SinkFloor() =>
        Logging.ParseLevel(Environment.GetEnvironmentVariable("LOG_LEVEL"));

    /// <summary>
    /// Combine message and exception into the text every downstream consumer
    /// (test seam, operator mirror, renderer) sees as "the message". The default
    /// folds the full exception onto a second line, as the chassis always has;
    /// a dialect that renders the exception itself returns the message unchanged.
    /// </summary>
    public virtual string ComposeMessage(string message, Exception? exception) =>
        exception is null ? message : $"{message}\n{exception}";

    /// <summary>
    /// Mirror a record to the external operator channel (the Windows Event Log).
    /// The default forwards every record to <see cref="Logging.EventLogMirrorHook"/>
    /// and leaves any severity filtering to the host.
    /// </summary>
    public virtual void Mirror(StandardLogEvent e) =>
        Logging.EventLogMirrorHook?.Invoke(e.Level, e.Logger, e.Message);

    /// <summary>Render the record as the single line written to the file and console sinks.</summary>
    public virtual string Render(StandardLogEvent e) => Logging.DefaultRender(e);

    /// <summary>Whether this record reaches the console at all.</summary>
    public virtual bool WritesToConsole(StandardLogEvent e) => e.Level >= Logging.ConsoleLevel;

    /// <summary>Whether a console-bound record goes to stderr rather than stdout.</summary>
    public virtual bool WritesToStandardError(StandardLogEvent e) => e.Level >= LogLevel.Warning;
}
