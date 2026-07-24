// Infrastructure/SeismicBreakerNames.cs
// -------------------------------------
// Connector-specific circuit-breaker dependency names. The circuit-breaker
// registry itself is generic and lives in Connector.Chassis; these two stable
// names ("seismic", "graph") are Seismic's own dependency keys, kept here so the
// chassis stays connector-neutral. Referenced by the Seismic and Graph clients
// when they register their breakers, and matched by the /metrics and /health
// observability surface.

namespace SeismicConnector.Infrastructure;

/// <summary>Stable circuit-breaker dependency names for this connector.</summary>
public static class SeismicBreakerNames
{
    /// <summary>The Seismic content API dependency.</summary>
    public const string Source = "seismic";

    /// <summary>The Microsoft Graph dependency.</summary>
    public const string Graph = "graph";
}
