// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/ServiceHost.cs
// -----------------------------
// Windows-service execution mode, shared by all five connectors.
//
// When the process is started by the Windows Service Control Manager,
// Program.Main routes here instead of running the command directly. The
// generic host provides the SCM start/stop handshake; the requested CLI
// command (typically ``full-deployment --continuous ...``) runs inside a
// BackgroundService.
//
// Lifecycle
// ---------
//   SCM start  → host starts → CommandWorker runs the CLI command.
//   SCM stop   → stoppingToken fires → ServiceStop.Request() → the ingest
//                pipeline stops at the next chunk boundary (pending Graph batch
//                flushed, checkpoint saved, same as the dashboard's Ctrl+X) and
//                the command returns.
//   Command completes on its own (non-continuous command) → the service stops
//                itself; the command's exit code becomes the process exit code.
//
// Working directory
// -----------------
// Services start in %WINDIR%\System32, but the connectors resolve config/,
// env/, logs/ and data/ against the current directory. The variable named by
// Chassis.Identity.HomeEnvVar (or, failing that, the executable's directory) is
// used as the working directory.
//
// Why the Event Log calls are a seam
// ----------------------------------
// The four connector copies this replaces were identical in MECHANISM and
// differed only in what they told the Windows Event Log:
//
//   Salesforce, Hadoop  nothing
//   Altrata             "Service command starting: {args}" / "...finished with exit code N"
//   Clarizen            its own EventLogSink.ServiceLifecycle(message, starting:)
//   Seismic             the chassis EventLogSink.Lifecycle(...), different wording again
//
// Three of them also own a local EventLogSink, so calling the chassis sink from
// here would route their service lifecycle events through a different sink than
// the rest of the connector uses. The wording and the sink are connector
// vocabulary; the SCM handshake, the working directory and the graceful-stop
// wiring are mechanism. So the mechanism moved and the vocabulary stayed, via
// the hooks below — each host assigns what it already emitted, and a host that
// emitted nothing assigns nothing.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Connector.Chassis;

public static class ServiceHost
{
    /// <summary>How long the host waits for the graceful chunk-boundary stop
    /// before the process is torn down. Checkpoints make a hard kill safe;
    /// this bound just gives the pipeline a fair chance to flush.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Called once the worker starts, with the command-line arguments and the
    /// resolved working directory. Hosts use it to mirror service start to the
    /// Windows Event Log in their own wording, through their own sink.
    /// </summary>
    /// <remarks>
    /// Null by default: Salesforce and Hadoop deliberately emit no Event Log
    /// entry here, and a chassis that emitted one on their behalf would be
    /// adding behaviour rather than sharing it.
    /// </remarks>
    public static Action<string[], string>? OnStarting;

    /// <summary>Called when the SCM requests a stop, before <see cref="ServiceStop.Request"/>.</summary>
    public static Action? OnStopRequested;

    /// <summary>Called with the exit code after the command returns normally.</summary>
    /// <remarks>
    /// Deliberately NOT called when the command threw. Clarizen and Altrata both
    /// emitted their "finished with exit code N" event inside the try, so an
    /// unhandled exception produced no such entry — reporting one would claim a
    /// clean finish that did not happen. Use <see cref="OnStopped"/> for an
    /// entry that must appear on every path.
    /// </remarks>
    public static Action<int>? OnFinished;

    /// <summary>
    /// Called from the worker's <c>finally</c>, on every path including an
    /// unhandled exception, with the resulting exit code.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OnFinished"/> because the copies genuinely
    /// disagreed: Seismic's "Service stopped (exit code N)." was emitted in the
    /// finally and so survived a crash, while Clarizen's and Altrata's
    /// "finished" events were not. Collapsing the two would have silently
    /// changed one connector's Event Log on its failure path — the path an
    /// operator is most likely to be reading.
    /// </remarks>
    public static Action<int>? OnStopped;

    /// <summary>Test seam: clear the hooks so one test's wiring cannot leak into another.</summary>
    internal static void ResetHooksForTests()
    {
        OnStarting = null;
        OnStopRequested = null;
        OnFinished = null;
        OnStopped = null;
    }

    public static async Task<int> RunAsync(
        string[] args, Func<string[], Task<int>> executeCommand)
    {
        var home = Environment.GetEnvironmentVariable(Chassis.Identity.HomeEnvVar);
        Directory.SetCurrentDirectory(
            !string.IsNullOrWhiteSpace(home) ? home : AppContext.BaseDirectory);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(
            options => options.ServiceName = Chassis.Identity.ServiceName);
        builder.Services.Configure<HostOptions>(
            options => options.ShutdownTimeout = ShutdownTimeout);
        builder.Services.AddHostedService(
            sp => new CommandWorker(
                args, executeCommand,
                sp.GetRequiredService<IHostApplicationLifetime>()));

        await builder.Build().RunAsync();
        return Environment.ExitCode;
    }

    private sealed class CommandWorker : BackgroundService
    {
        private readonly string[] _args;
        private readonly Func<string[], Task<int>> _executeCommand;
        private readonly IHostApplicationLifetime _lifetime;

        public CommandWorker(
            string[] args,
            Func<string[], Task<int>> executeCommand,
            IHostApplicationLifetime lifetime)
        {
            _args = args;
            _executeCommand = executeCommand;
            _lifetime = lifetime;
        }

        // Resolved per call rather than cached in a static field: the host's
        // LoggerFactory seam may not be assigned at type-init time, and a logger
        // captured then would pin the chassis's own Logging for a connector that
        // supplies its own (Salesforce's CPython-style stack, via
        // Chassis.LoggerFactory).
        private static IAppLogger Logger => Chassis.GetLogger(Chassis.Identity.ServiceLoggerName);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // SCM stop → the same graceful stop as the dashboard's Ctrl+X.
            using var stopRegistration = stoppingToken.Register(() =>
            {
                Logger.Warning(
                    "Service stop requested — finishing the current chunk and saving the checkpoint...");
                OnStopRequested?.Invoke();
                ServiceStop.Request();
            });

            var workingDirectory = Directory.GetCurrentDirectory();
            Logger.Info($"Running as a Windows service: {string.Join(" ", _args)} " +
                        $"(working directory: {workingDirectory})");
            OnStarting?.Invoke(_args, workingDirectory);
            try
            {
                Environment.ExitCode = await _executeCommand(_args);
                Logger.Info($"Command finished with exit code {Environment.ExitCode}");
                OnFinished?.Invoke(Environment.ExitCode);
            }
            catch (Exception ex)
            {
                Logger.Error("Service command failed with an unhandled exception", ex);
                Environment.ExitCode = 1;
            }
            finally
            {
                OnStopped?.Invoke(Environment.ExitCode);
                _lifetime.StopApplication();
            }
        }
    }
}
