// Infrastructure/RunLog.cs
// ------------------------
// Starting a run, on top of the shared chassis logging.
//
// Chassis Logging.Initialize already does everything this connector's own
// StartRun did — timestamped run directory under the logs root, owner-only
// permissions on both (via HardenDirectoryHook), and connector.log — with one
// exception: it does not emit the Windows Event Log lifecycle marker, because
// the chassis knows nothing about this connector's Event Log sink.
//
// That marker is how an operator finds which run directory a service start
// produced, so it is restored here rather than dropped in the migration.

namespace AltrataConnector.Infrastructure;

/// <summary>Run-scoped logging lifecycle for this connector.</summary>
public static class RunLog
{
    /// <summary>
    /// Open a run log and announce it. Idempotent, matching the chassis: a second
    /// call while a run is open is a no-op and emits no second marker.
    /// </summary>
    public static void StartRun(string prefix, bool verbose = false)
    {
        // This connector resolves its logs root from LOGS_DIR on every run; the
        // chassis holds it as a settable property fixed at type-load. Re-resolve
        // here so LOGS_DIR keeps working — operators set it per deployment, and
        // the tests set it per case.
        Logging.LogsRoot = Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");

        var before = Logging.RunDirectory;
        Logging.Initialize(prefix, verbose);
        var dir = Logging.RunDirectory;
        // Only announce when this call actually opened a run — Initialize returns
        // silently if one is already open, and the file sink may have failed.
        if (dir is not null && !string.Equals(before, dir, StringComparison.Ordinal))
            EventLogSink.Lifecycle($"Run started: {prefix} (logs: {dir})");
    }
}
