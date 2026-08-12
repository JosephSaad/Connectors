// Infrastructure/ChassisLoggerAdapter.cs
// --------------------------------------
// Lets shared Connector.Chassis components log through THIS connector's logging
// rather than the chassis's own.
//
// This connector's logging is a handler-based port of CPython's `logging` module
// (LogRecord, LogHandler, StreamHandler, LineRotatingFileHandler, Propagate,
// AddHandler). The chassis has fixed sinks behind a mode switch. They are
// different designs, not different formats, so this connector cannot simply
// adopt chassis logging the way Clarizen and Hadoop did.
//
// Chassis.LoggerFactory exists for exactly this case: chassis components resolve
// their loggers through it, so pointing it at the adapter below keeps every
// chassis log line inside this connector's handler pipeline — same run
// directory, same rotation, same JSON/text formatting, same LOG_LEVEL floor.
// Without it, adopting any chassis component would send its lines into a logging
// system this connector never reads.

using ChassisLogLevel = Connector.Chassis.LogLevel;
using IChassisLogger = Connector.Chassis.IAppLogger;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>
/// Adapts a connector <see cref="IAppLogger"/> to the chassis
/// <see cref="Connector.Chassis.IAppLogger"/> contract.
/// </summary>
/// <remarks>
/// The two interfaces are all but identical — the connector's simply predates
/// the chassis and carries no <c>Name</c>. Level values line up exactly
/// (Debug=10, Info=20, Warning=30, Error=40 on both), so
/// <see cref="IsEnabledFor(ChassisLogLevel)"/> is a straight numeric cast rather
/// than a mapping table that could drift.
/// </remarks>
internal sealed class ChassisLoggerAdapter : IChassisLogger
{
    private readonly IAppLogger _inner;

    internal ChassisLoggerAdapter(string name, IAppLogger inner)
    {
        Name = name;
        _inner = inner;
    }

    /// <summary>Logger name. The chassis exposes this; the connector's logger does not carry one.</summary>
    public string Name { get; }

    public void Debug(string message) => _inner.Debug(message);

    public void Info(string message) => _inner.Info(message);

    public void Warning(string message) => _inner.Warning(message);

    public void Error(string message) => _inner.Error(message);

    /// <summary>
    /// The chassis contract allows a null exception; this connector's overload
    /// does not, so a null routes to the message-only form rather than being
    /// passed through as a null reference.
    /// </summary>
    public void Error(string message, Exception? ex)
    {
        if (ex is null)
            _inner.Error(message);
        else
            _inner.Error(message, ex);
    }

    public bool IsEnabledFor(int level) => _inner.IsEnabledFor(level);

    public bool IsEnabledFor(ChassisLogLevel level) => _inner.IsEnabledFor((int)level);
}
