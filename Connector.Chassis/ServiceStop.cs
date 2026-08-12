// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/ServiceStop.cs
// -----------------------------
// Process-wide graceful-stop signal, set when the host (e.g. the Windows
// Service Control Manager via ServiceHost) asks the process to shut down.
//
// Semantics mirror the dashboard's Ctrl+X: the ingestion pipeline finishes the
// in-flight chunk, flushes the pending Graph batch, writes its checkpoint and
// returns, so the next run resumes exactly where this one stopped.  The
// continuous-mode schedulers observe Token so a stop request does not wait
// out a multi-hour Task.Delay.

namespace Connector.Chassis;

public static class ServiceStop
{
    private static CancellationTokenSource _cts = new();

    /// <summary>True once a graceful shutdown has been requested.</summary>
    public static bool Requested => _cts.IsCancellationRequested;

    /// <summary>Cancelled when a graceful shutdown is requested.</summary>
    public static CancellationToken Token => _cts.Token;

    /// <summary>Request a graceful stop (idempotent).</summary>
    /// <remarks>
    /// Never throws. <see cref="Reset"/> disposes the previous source, so a stop
    /// arriving just after a reset — an SCM stop racing teardown, or a test that
    /// re-arms while a stop is in flight — would otherwise surface
    /// <see cref="ObjectDisposedException"/> on the shutdown path, where there is
    /// nothing useful to do with it. An already-disposed source means the stop it
    /// signalled has been handled, so ignoring it is correct rather than merely safe.
    /// </remarks>
    public static void Request()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down — the stop this would have signalled is moot.
        }
    }

    /// <summary>Test seam: re-arm the signal between test cases.</summary>
    internal static void Reset()
    {
        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
    }
}
