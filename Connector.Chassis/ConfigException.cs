// ConfigException.cs
// ------------------
// Shared configuration-error type. Thrown by chassis infrastructure and by
// connector-side configuration loaders/validators when a required setting is
// missing or invalid. Lives in the chassis so a single exception type spans
// both the shared transport (HttpTransport) and each connector's config layer,
// letting callers and tests catch one type regardless of where the fault
// surfaced. The connector's global `using Connector.Chassis` resolves the bare
// name `ConfigException` at every throw and catch site unchanged.

namespace Connector.Chassis;

/// <summary>Thrown when required configuration is missing or invalid.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }
}
