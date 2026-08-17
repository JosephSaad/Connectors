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

    /// <summary>
    /// The Windows service name registered with the SCM. Defaults to
    /// <see cref="EventLogSource"/>, which is what every connector that does not
    /// set it explicitly already uses.
    /// </summary>
    /// <remarks>
    /// Init-only with a default so the positional constructor stays
    /// source-compatible: a host that does not care keeps writing
    /// <c>new ChassisIdentity(id, source, baseName)</c>.
    /// </remarks>
    public string ServiceName { get; init; } = EventLogSource;

    /// <summary>
    /// Environment variable naming the connector's home directory, used by
    /// <see cref="ServiceHost"/> to set the working directory. Services start in
    /// <c>%WINDIR%\System32</c>, but the connectors resolve <c>config/</c>,
    /// <c>env/</c>, <c>logs/</c> and <c>data/</c> against the current directory.
    /// </summary>
    /// <remarks>
    /// No fleet-wide default is possible: the five connectors each shipped their
    /// own spelling (<c>SFCONNECTOR_HOME</c>, <c>CLARIZEN_CONNECTOR_HOME</c>,
    /// <c>HADOOP_CONNECTOR_HOME</c>, <c>ALTRATA_CONNECTOR_HOME</c>,
    /// <c>SEISMIC_CONNECTOR_HOME</c>) and those names are in deployed service
    /// definitions and operator runbooks. Renaming them would be a breaking
    /// change to every existing installation, so the chassis takes the name as
    /// identity rather than imposing one.
    /// </remarks>
    public string HomeEnvVar { get; init; } = "CONNECTOR_HOME";
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

    /// <summary>
    /// How chassis components obtain a logger.
    /// </summary>
    /// <remarks>
    /// Defaults to the chassis's own <see cref="Logging"/>, so a host that adopts
    /// chassis logging needs no wiring and behaves exactly as before.
    ///
    /// A host with its OWN logging framework assigns this instead, and chassis
    /// components then log through that. Without this seam, adopting any chassis
    /// component obliges a host to adopt chassis <see cref="Logging"/> as well —
    /// otherwise the component's log lines vanish into a logging system the host
    /// does not read. That coupling is what kept the Salesforce connector, whose
    /// logging is a handler-based port of CPython's <c>logging</c> module rather
    /// than a variant of this one, from sharing anything at all.
    ///
    /// Assign before the first chassis type is used, alongside <see cref="Init"/>.
    /// That is the same ordering <see cref="Identity"/> already requires, since
    /// component loggers are named from it at type-load.
    /// </remarks>
    public static Func<string, IAppLogger> LoggerFactory { get; set; } = Logging.GetLogger;

    /// <summary>Resolve a logger for a chassis component via <see cref="LoggerFactory"/>.</summary>
    internal static IAppLogger GetLogger(string name) => LoggerFactory(name);
}
