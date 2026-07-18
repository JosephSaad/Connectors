// Infrastructure/ServiceHost.cs
// -----------------------------
// Windows-service execution mode (SCM-aware host).
//
//   SCM start  → host starts → CommandWorker runs the CLI command.
//   SCM stop   → stoppingToken fires → ServiceStop.Request() → the crawl
//                engine stops at the next chunk boundary (pending Graph batch
//                flushed, checkpoint saved) and the command returns.
//   Command completes on its own → the service stops itself; the command's
//                exit code becomes the process exit code.
//
// Services start in %WINDIR%\System32; ALTRATA_CONNECTOR_HOME (or the
// executable's directory) is used as the working directory so config/, env/,
// logs/ and data/ resolve correctly.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AltrataConnector.Infrastructure;

public static class ServiceHost
{
    /// <summary>Grace period for the chunk-boundary stop before hard teardown.
    /// Checkpoints make a hard kill safe; this just gives the pipeline a fair
    /// chance to flush.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(string[] args, Func<string[], Task<int>> executeCommand)
    {
        var home = Environment.GetEnvironmentVariable("ALTRATA_CONNECTOR_HOME");
        Directory.SetCurrentDirectory(
            !string.IsNullOrWhiteSpace(home) ? home : AppContext.BaseDirectory);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(options => options.ServiceName = "AltrataConnector");
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);
        builder.Services.AddHostedService(sp => new CommandWorker(
            args, executeCommand, sp.GetRequiredService<IHostApplicationLifetime>()));

        await builder.Build().RunAsync();
        return Environment.ExitCode;
    }

    private sealed class CommandWorker : BackgroundService
    {
        private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.service");

        private readonly string[] _args;
        private readonly Func<string[], Task<int>> _executeCommand;
        private readonly IHostApplicationLifetime _lifetime;

        public CommandWorker(string[] args, Func<string[], Task<int>> executeCommand,
            IHostApplicationLifetime lifetime)
        {
            _args = args;
            _executeCommand = executeCommand;
            _lifetime = lifetime;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var stopRegistration = stoppingToken.Register(() =>
            {
                Logger.Warning("Service stop requested — finishing the current chunk and saving the checkpoint...");
                ServiceStop.Request();
            });

            Logger.Info($"Running as a Windows service: {string.Join(" ", _args)} " +
                        $"(working directory: {Directory.GetCurrentDirectory()})");
            EventLogSink.Lifecycle($"Service command starting: {string.Join(" ", _args)}");
            try
            {
                Environment.ExitCode = await _executeCommand(_args);
                Logger.Info($"Command finished with exit code {Environment.ExitCode}");
                EventLogSink.Lifecycle($"Service command finished with exit code {Environment.ExitCode}");
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
