// Commands/Runtime.cs
// -------------------
// Shared command bootstrap: loads AppConfig, constructs the client stack and
// disposes it cleanly. Every command funnels through this so wiring stays in
// one place (and tests can construct the pieces directly instead).

using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Commands;

public sealed class Runtime : IDisposable
{
    public required AppConfig Config { get; init; }
    public required SeismicClient Seismic { get; init; }
    public required GraphClient Graph { get; init; }
    public required IIdentityStore Store { get; init; }
    public required IngestPipeline Pipeline { get; init; }
    public required ConnectionManager Connection { get; init; }

    private IDisposable? _healthEndpoint;

    public static Runtime Create()
    {
        var config = AppConfig.Load();
        Alerting.ConnectorId = config.Connector.Id;
        // Register OTLP export when OTEL_EXPORTER_OTLP_ENDPOINT is set; inert otherwise.
        Tracing.Initialize(config);
        var seismic = new SeismicClient(config.Seismic);
        var graph = new GraphClient(config.Graph);
        var store = IdentityStoreFactory.Open(config.Connector.Id);
        var runtime = new Runtime
        {
            Config = config,
            Seismic = seismic,
            Graph = graph,
            Store = store,
            Pipeline = new IngestPipeline(config, seismic, graph, store),
            Connection = new ConnectionManager(graph, config),
        };
        runtime._healthEndpoint = HealthEndpoint.StartIfConfigured(config.Connector.Id, config);
        return runtime;
    }

    /// <summary>Open a fresh reconciliation report next to the run's logs.</summary>
    public ReconciliationReport OpenReport(string runLogFile)
    {
        var dir = Path.GetDirectoryName(runLogFile) ?? CommandRegistry.LogsDir;
        var path = Path.Combine(
            dir, $"reconciliation_{Config.Connector.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        var report = new ReconciliationReport(path);
        Pipeline.Report = report;
        return report;
    }

    /// <summary>
    /// Open the classification manifest for the run and attach it to the
    /// pipeline. File-backed (Purview-aligned export) when
    /// CLASSIFICATION_MANIFEST=true; otherwise a no-op in-memory manifest, so
    /// the caller's <c>using</c> works either way.
    /// </summary>
    public ClassificationManifest OpenManifest(string runLogFile)
    {
        ClassificationManifest manifest;
        if (Config.Seismic.ClassificationManifest)
        {
            var dir = Path.GetDirectoryName(runLogFile) ?? CommandRegistry.LogsDir;
            var path = Path.Combine(
                dir, $"classification_manifest_{Config.Connector.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            manifest = new ClassificationManifest(path);
        }
        else
        {
            manifest = new ClassificationManifest();
        }
        Pipeline.Manifest = manifest;
        return manifest;
    }

    public void Dispose()
    {
        _healthEndpoint?.Dispose();
        Store.Dispose();
        // Flush any batched spans to the collector before exit.
        Tracing.Shutdown();
    }
}
