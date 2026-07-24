// Connector.Chassis/Chassis.cs
// ----------------------------
// Identity-parameterisation seam for the shared connector chassis.
//
// The chassis holds the pure infrastructure (logging, secrets, circuit breaker,
// decision ledger, Windows-service host, ...) that is identical across every
// connector in the fleet. The only per-connector variation those files need is
// a handful of identity strings — the connector id, the Windows Event Log
// source, and the base logger name from which the derived logger names hang.
// Each host sets them ONCE at startup via <see cref="Chassis.Init"/> before any
// logging / event-log / ledger use, so the chassis references no connector type
// yet emits byte-identical output to the pre-extraction connector.

namespace Connector.Chassis;

/// <summary>
/// Per-connector identity supplied to the chassis at startup. The three stored
/// values fully determine every identity string the moved infrastructure emits;
/// derived logger names hang off <see cref="BaseLoggerName"/>.
/// </summary>
public sealed record ChassisIdentity(string ConnectorId, string EventLogSource, string BaseLoggerName)
{
    /// <summary>Logger name for the Windows-service host worker.</summary>
    public string ServiceLoggerName => $"{BaseLoggerName}.service";

    /// <summary>Logger name for the tamper-evident decision ledger.</summary>
    public string LedgerLoggerName => $"{BaseLoggerName}.ledger";
}

/// <summary>
/// Process-wide chassis configuration. <see cref="Init"/> installs the host's
/// <see cref="ChassisIdentity"/>; <see cref="CorrelationIdProvider"/> lets the
/// host inject its distributed-tracing correlation id into structured logs
/// without the chassis depending on the host's tracing implementation.
/// </summary>
public static class Chassis
{
    /// <summary>The active identity. Defaults to a neutral placeholder until <see cref="Init"/> runs.</summary>
    public static ChassisIdentity Identity { get; private set; } = new("connector", "Connector", "connector");

    /// <summary>Install the host identity. Call once at startup, before any logging/event-log/ledger use.</summary>
    public static void Init(ChassisIdentity id) => Identity = id;

    /// <summary>
    /// Optional host-supplied correlation-id source. When set, structured
    /// (<c>LOG_FORMAT=json</c>) log lines are stamped with its current value;
    /// a <c>null</c> return (or an unset provider) omits the field, exactly as
    /// the pre-extraction code did.
    /// </summary>
    public static Func<string?>? CorrelationIdProvider;
}
