// Infrastructure/ConsoleVerbosity.cs
// ----------------------------------
// Whether the operator asked for verbose console output (--verbose).
//
// This used to be Logging.Verbose on the connector's own logging class. The
// chassis takes verbosity as an argument to Logging.Initialize/Configure rather
// than exposing it as readable state, and deliberately so: how loud the log is
// belongs to logging, but whether to render the live dashboard is a UI decision
// that belongs here. Inferring it back out of the chassis (say, from
// EffectiveLevel) would couple this to the chassis's threshold semantics, which
// differ between its modes.
//
// Set wherever verbosity is decided, alongside the chassis call.

namespace AltrataConnector.Infrastructure;

/// <summary>Operator-requested console verbosity (<c>--verbose</c>).</summary>
public static class ConsoleVerbosity
{
    /// <summary>True when the operator asked for verbose console output.</summary>
    public static bool Verbose { get; set; }
}
