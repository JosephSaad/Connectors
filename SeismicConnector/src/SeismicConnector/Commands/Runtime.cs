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
        // The chassis tracing layer is connector-neutral, so the OTEL_*/AppConfig
        // resolution happens here (SeismicTracing) and rides in as options.
        Tracing.Initialize(SeismicTracing.OptionsFrom(config));
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
    /// pipeline. File-backed (advisory classification export; Purview-aligned in
    /// naming only, not a Purview-enforced label) when CLASSIFICATION_MANIFEST=true;
    /// otherwise a no-op in-memory manifest, so the caller's <c>using</c> works
    /// either way.
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

    /// <summary>
    /// Open the decision audit ledger and attach it to the pipeline. File-backed,
    /// hash-chained JSONL when DECISION_LEDGER=true; otherwise a no-op in-memory
    /// ledger, so the caller's <c>using</c> works either way.
    /// <para>
    /// Unlike the report and the manifest this takes NO run log file: the ledger
    /// is deliberately not a per-run artifact. It lives at the stable logs-root
    /// path (<see cref="DecisionLedger.PathFor"/>) so it survives run-directory
    /// retention pruning and forms ONE continuous hash chain across runs.
    /// </para>
    /// <para>
    /// EVERY command that runs the pipeline must call this — the flag has to mean
    /// the same thing on every command, or an operator who turns the ledger on
    /// loses that run's exclusion and ACL-restriction decisions with no warning.
    /// </para>
    /// </summary>
    public DecisionLedger OpenLedger()
    {
        DecisionLedger ledger;
        if (EnvFlags.IsTrue(DecisionLedger.EnvVar))
        {
            ReportLegacyRunLedgers();
            ledger = new DecisionLedger(
                DecisionLedger.PathFor(CommandRegistry.LogsDir, Config.Connector.Id));
        }
        else
        {
            ledger = new DecisionLedger();
        }
        Pipeline.Ledger = ledger;
        return ledger;
    }

    /// <summary>
    /// Point the operator at any per-run ledgers written before the stable-path
    /// move. They are separate chains, each rooted at its own genesis, and the
    /// current ledger does NOT continue them — so they are named rather than
    /// left to be discovered (or pruned) by accident.
    /// </summary>
    private static void ReportLegacyRunLedgers()
    {
        var legacy = DecisionLedger.FindLegacyRunLedgers(CommandRegistry.LogsDir);
        if (legacy.Count == 0)
            return;
        Logging.GetLogger("seismic_connector").Warning(
            $"Decision ledger: found {legacy.Count} legacy per-run ledger file(s) from before the move to "
            + $"the stable logs-root path ({string.Join(", ", legacy)}). Each is a SEPARATE hash chain and "
            + "is not continued by the current ledger; archive them alongside it for a complete audit "
            + "history. Log retention will not delete them (LogPruner skips any run directory holding a "
            + "ledger), so remove them by hand once archived.");
    }

    public void Dispose()
    {
        _healthEndpoint?.Dispose();
        Store.Dispose();
        // Flush any batched spans to the collector before exit.
        Tracing.Shutdown();
    }
}
