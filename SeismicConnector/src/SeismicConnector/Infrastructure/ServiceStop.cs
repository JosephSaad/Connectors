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

namespace SeismicConnector.Infrastructure;

public static class ServiceStop
{
    private static CancellationTokenSource _cts = new();

    /// <summary>True once a graceful shutdown has been requested.</summary>
    public static bool Requested => _cts.IsCancellationRequested;

    /// <summary>Cancelled when a graceful shutdown is requested.</summary>
    public static CancellationToken Token => _cts.Token;

    /// <summary>Request a graceful stop (idempotent).</summary>
    public static void Request() => _cts.Cancel();

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
