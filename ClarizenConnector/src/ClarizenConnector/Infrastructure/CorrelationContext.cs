// Infrastructure/CorrelationContext.cs
// -------------------------------------
// Per-crawl-cycle correlation id, flowed through async calls via AsyncLocal so
// a single crawl (or webhook event) can be followed end-to-end across logs,
// dead-letter records, and trace spans — even when OpenTelemetry export is off.
//
// The id is a 32-hex string. When a trace span is active for the cycle it is
// the span's W3C TraceId (so logs and traces share one identifier); otherwise
// it's a fresh random id. Begin() returns an IDisposable that restores the
// previous value, so nested scopes (e.g. a webhook event inside a running
// process) compose correctly.

using System.Diagnostics;

namespace ClarizenConnector.Infrastructure;

public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> Slot = new();

    /// <summary>The correlation id for the current async flow, or null when none is active.</summary>
    public static string? Current => Slot.Value;

    /// <summary>Generate a fresh 32-hex correlation id (W3C trace-id shaped).</summary>
    public static string NewId() => ActivityTraceId.CreateRandom().ToHexString();

    /// <summary>
    /// Begin a correlation scope. When <paramref name="id"/> is null a new id is
    /// generated (or the active span's TraceId is adopted when one exists, so
    /// logs and spans share the identifier). Dispose restores the previous id.
    /// </summary>
    public static IDisposable Begin(string? id = null)
    {
        var previous = Slot.Value;
        Slot.Value = id ?? Activity.Current?.TraceId.ToHexString() ?? NewId();
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Slot.Value = _previous;
        }
    }
}
