// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/ServiceHost.cs
// -----------------------------
// Windows-service execution mode.
//
// When the process is started by the Windows Service Control Manager,
// Program.Main routes here instead of running the command directly.  The
// generic host provides the SCM start/stop handshake; the requested CLI
// command (typically ``full-deployment --continuous ...``) runs inside a
// BackgroundService.
//
// Lifecycle
// ---------
//   SCM start  → host starts → CommandWorker runs the CLI command.
//   SCM stop   → stoppingToken fires → ServiceStop.Request() → the ingest
//                pipeline stops at the next chunk boundary (checkpoint saved,
//                same as the dashboard's Ctrl+X) and the command returns.
//   Command completes on its own (non-continuous command) → the service stops
//                itself; the command's exit code becomes the process exit code.
//
// Working directory
// -----------------
// Services start in %WINDIR%\System32, but the connector resolves config/,
// env/, logs/ and data/ against the current directory.  SFCONNECTOR_HOME (or,
// failing that, the executable's directory) is used as the working directory.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SalesforceCopilotConnector.Infrastructure;

public static class ServiceHost
{
    /// <summary>How long the host waits for the graceful chunk-boundary stop
    /// before the process is torn down. Checkpoints make a hard kill safe;
    /// this bound just gives the pipeline a fair chance to flush.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(
        string[] args, Func<string[], Task<int>> executeCommand)
    {
        var home = Environment.GetEnvironmentVariable("SFCONNECTOR_HOME");
        Directory.SetCurrentDirectory(
            !string.IsNullOrWhiteSpace(home) ? home : AppContext.BaseDirectory);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(
            options => options.ServiceName = "SalesforceCopilotConnector");
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
        private static readonly IAppLogger Logger =
            Logging.GetLogger("salesforce_connector.service");

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

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // SCM stop → the same graceful stop as the dashboard's Ctrl+X.
            using var stopRegistration = stoppingToken.Register(() =>
            {
                Logger.Warning(
                    "Service stop requested — finishing the current chunk and saving the checkpoint...");
                ServiceStop.Request();
            });

            Logger.Info($"Running as a Windows service: {string.Join(" ", _args)} " +
                        $"(working directory: {Directory.GetCurrentDirectory()})");
            try
            {
                Environment.ExitCode = await _executeCommand(_args);
                Logger.Info($"Command finished with exit code {Environment.ExitCode}");
            }
            catch (Exception ex)
            {
                Logger.Error("Service command failed with an unhandled exception", ex);
                Environment.ExitCode = 1;
            }
            finally
            {
                _lifetime.StopApplication();
            }
        }
    }
}
