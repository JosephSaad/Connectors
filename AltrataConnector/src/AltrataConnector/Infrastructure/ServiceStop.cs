// Infrastructure/ServiceStop.cs
// -----------------------------
// Cooperative graceful-stop flag. The Windows service stop (and Ctrl+C in
// console mode) request a stop; the crawl engine checks the flag at chunk
// boundaries, flushes the pending Graph batch, saves the checkpoint and
// returns. A CancellationToken view is provided for async waits.

namespace AltrataConnector.Infrastructure;

public static class ServiceStop
{
    private static CancellationTokenSource _cts = new();

    /// <summary>True once a graceful stop has been requested.</summary>
    public static bool Requested => _cts.IsCancellationRequested;

    /// <summary>Token cancelled when a graceful stop is requested.</summary>
    public static CancellationToken Token => _cts.Token;

    /// <summary>Request a graceful stop (idempotent).</summary>
    public static void Request()
    {
        try { _cts.Cancel(); } catch { /* already disposed — ignore */ }
    }

    /// <summary>Reset for a fresh run (test seam / continuous-mode restarts).</summary>
    internal static void ResetForTests()
    {
        var old = _cts;
        _cts = new CancellationTokenSource();
        old.Dispose();
    }
}
