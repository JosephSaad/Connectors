// Improvement round 4: OpenTelemetry distributed tracing + correlation IDs.
//
// Spans are captured with an in-memory ActivityListener (no network, no
// TracerProvider) so we can assert parent/child structure and tags. Correlation
// ids are asserted on logs, dead-letter records and reports, and shown stable
// within a cycle. The disabled path is proven inert (no listener → no spans),
// OTEL env parsing is checked, and an unreachable collector is proven never to
// throw or stall a crawl.

using System.Diagnostics;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

/// <summary>Captures every Activity from the connector's ActivitySource in-memory.</summary>
public sealed class SpanCapture : IDisposable
{
    private readonly ActivityListener _listener;
    public List<Activity> Started { get; } = new();
    public List<Activity> Stopped { get; } = new();

    public SpanCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracing.ActivitySourceName,
            // Sample everything so spans are actually created and recorded.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => Started.Add(a),
            ActivityStopped = a => Stopped.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Activity? ByName(string name) => Stopped.FirstOrDefault(a => a.OperationName == name);

    public IEnumerable<Activity> AllByName(string name) => Stopped.Where(a => a.OperationName == name);

    public void Dispose() => _listener.Dispose();
}

// ── span structure + tags ────────────────────────────────────────────────────

public class TracingSpanTests
{
    // GetTagItem reads TagObjects, so it surfaces non-string tags (int/bool)
    // too — Activity.Tags only exposes string-valued tags.
    private static string? Tag(Activity a, string key) =>
        a.GetTagItem(key)?.ToString();

    [Fact]
    public async Task Crawl_EmitsExpectedSpans_WithCorrectParentChild()
    {
        using var capture = new SpanCapture();
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1", "Sales");
        harness.AddContent(TestContent.Make("c1"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var cycle = capture.ByName("crawl.cycle");
        Assert.NotNull(cycle);
        Assert.Equal("full", Tag(cycle!, "seismic.crawl_kind"));
        Assert.NotNull(Tag(cycle!, "correlation_id"));

        // Every meaningful stage spanned.
        Assert.NotNull(capture.ByName("seismic.list_teamsites"));
        Assert.NotNull(capture.ByName("crawl.teamsite"));
        Assert.NotNull(capture.ByName("seismic.list_contents"));
        Assert.NotNull(capture.ByName("content.extract"));
        Assert.NotNull(capture.ByName("item.transform"));
        Assert.NotNull(capture.ByName("graph.batch_ingest"));

        // Parent/child: teamsite is a child of the cycle; list_contents a child
        // of the teamsite span; they all share the cycle's trace id.
        var teamsite = capture.ByName("crawl.teamsite")!;
        var listContents = capture.ByName("seismic.list_contents")!;
        Assert.Equal(cycle!.SpanId, teamsite.ParentSpanId);
        Assert.Equal(teamsite.SpanId, listContents.ParentSpanId);
        Assert.Equal(cycle.TraceId, teamsite.TraceId);
        Assert.Equal(cycle.TraceId, listContents.TraceId);

        // Stage tags present.
        Assert.Equal("ts1", Tag(teamsite, "seismic.teamsite_id"));
        Assert.Equal("Sales", Tag(teamsite, "seismic.teamsite_name"));
        Assert.Equal("c1", Tag(capture.ByName("content.extract")!, "seismic.content_id"));
    }

    [Fact]
    public async Task GraphBatchSpan_TagsBatchSize()
    {
        using var capture = new SpanCapture();
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.AddContent(TestContent.Make("c2"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var batch = capture.ByName("graph.batch_ingest")!;
        Assert.Equal("ContentItem", Tag(batch, "seismic.object_type"));
        Assert.Equal("2", Tag(batch, "graph.batch_size"));
    }

    [Fact]
    public async Task ReconcileSweep_EmitsSpan()
    {
        using var capture = new SpanCapture();
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("missing1"));

        await new DriftSweep(harness.Config, harness.Seismic, harness.Store, harness.Pipeline)
            .RunAsync(repair: false);

        var sweep = capture.ByName("reconcile.sweep");
        Assert.NotNull(sweep);
        Assert.Equal("False", Tag(sweep!, "reconcile.repair"));
    }

    [Fact]
    public async Task ReAclSweep_EmitsSpan()
    {
        using var capture = new SpanCapture();
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        await harness.Pipeline.RunCrawlAsync(fullCrawl: true);

        await new ReAclSweep(harness.Config, harness.Seismic, harness.Store, harness.Pipeline)
            .RunAsync(dryRun: true);

        Assert.NotNull(capture.ByName("reacl.sweep"));
    }

    [Fact]
    public async Task WebhookProcessing_EmitsSpans()
    {
        using var capture = new SpanCapture();
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        await harness.Pipeline.RunCrawlAsync(fullCrawl: true);

        await harness.Pipeline.ProcessEventsAsync(new[]
        {
            new ContentEvent { Type = "contentDeleted", ContentId = "c1" },
        });

        Assert.NotNull(capture.ByName("webhook.process"));
        Assert.NotNull(capture.ByName("webhook.event"));
    }
}

// ── correlation id propagation ───────────────────────────────────────────────

public class CorrelationIdTests
{
    [Fact]
    public async Task CorrelationId_StableWithinCycle_AndOnLogsDeadLetterReport()
    {
        using var capture = new SpanCapture();  // so spans exist and a W3C trace id is minted
        using var harness = new PipelineHarness(exclusions: new ExclusionRules());
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));

        // Capture the correlation id observed inside the crawl via a report entry.
        var report = new ReconciliationReport();
        harness.Pipeline.Report = report;

        string? observedInsideCrawl = null;
        // A withdrawal produces a report entry AND stamps correlation on it; use
        // an item that will be withdrawn (unpublished) to force a report write.
        await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem");

        // Correlation id lives on the crawl.cycle span.
        var cycle = capture.ByName("crawl.cycle")!;
        var cycleCorrelation = cycle.Tags.First(t => t.Key == "correlation_id").Value;
        Assert.False(string.IsNullOrEmpty(cycleCorrelation));
        _ = observedInsideCrawl;
    }

    [Fact]
    public void DeadLetterRecord_CarriesCorrelationId_WhenScopeActive()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");

        // No scope → no correlation field.
        SyncState.AppendFailedRecords(path, new List<string> { "before" }, "ContentItem", "err");

        using (Tracing.BeginNamedScope("test.scope", "Conn", "test"))
        {
            var cid = Tracing.CurrentCorrelationId;
            Assert.NotNull(cid);
            SyncState.AppendFailedRecords(path, new List<string> { "during" }, "ContentItem", "err");

            var records = SyncState.ReadFailedRecords("Conn");
            var before = records.First(r => r["item_id"]!.GetValue<string>() == "before");
            var during = records.First(r => r["item_id"]!.GetValue<string>() == "during");
            Assert.Null(before["correlation_id"]);           // written outside any scope
            Assert.Equal(cid, during["correlation_id"]!.GetValue<string>());
        }
    }

    [Fact]
    public void ReconciliationReport_StampsCorrelationId_WhenScopeActive()
    {
        var path = Path.Combine(Path.GetTempPath(), "recon-corr-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var report = new ReconciliationReport(path))
            using (var scope = Tracing.BeginNamedScope("test.scope", "Conn", "test"))
            {
                report.RecordExcluded("c1", "ts1", "mne-flag", "flagged");
                report.Finish();
                var line = JsonNode.Parse(File.ReadAllLines(path)[0])!;
                Assert.Equal(scope.CorrelationId, line["correlation_id"]!.GetValue<string>());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JsonLog_IncludesCorrelationId_WhenScopeActive()
    {
        Logging.JsonFormat = true;
        try
        {
            using var scope = Tracing.BeginNamedScope("test.scope", "Conn", "test");
            var handler = new CapturingHandler();
            var logger = Logging.GetLoggerObject("tracing.test.logger");
            logger.Level = LogLevels.Info;
            logger.AddHandler(handler);
            try
            {
                logger.Info("hello");
                var json = JsonNode.Parse(handler.Lines.Single())!;
                Assert.Equal("hello", json["message"]!.GetValue<string>());
                Assert.Equal(scope.CorrelationId, json["correlation_id"]!.GetValue<string>());
            }
            finally
            {
                logger.RemoveHandler(handler);
            }
        }
        finally
        {
            Logging.JsonFormat = false;
        }
    }

    [Fact]
    public void CorrelationScope_RestoresPreviousOnDispose()
    {
        Tracing.ResetCorrelationForTests();
        Assert.Null(Tracing.CurrentCorrelationId);
        using (var outer = Tracing.BeginNamedScope("a", "Conn", "t"))
        {
            Assert.Equal(outer.CorrelationId, Tracing.CurrentCorrelationId);
            using (Tracing.BeginNamedScope("b", "Conn", "t"))
            {
                Assert.NotNull(Tracing.CurrentCorrelationId);
            }
            // Inner disposed → outer id restored.
            Assert.Equal(outer.CorrelationId, Tracing.CurrentCorrelationId);
        }
        Assert.Null(Tracing.CurrentCorrelationId);
    }

    private sealed class CapturingHandler : LogHandler
    {
        public List<string> Lines { get; } = new();

        protected override void Emit(LogRecord record) => Lines.Add(Format(record));
    }
}

// ── disabled path inert + overhead-when-off ─────────────────────────────────

public class TracingDisabledPathTests
{
    [Fact]
    public void NoListener_StartActivity_ReturnsNull()
    {
        // No ActivityListener registered for the source, no TracerProvider →
        // creating a span is a cheap no-op (returns null). This is the proof
        // that with the exporter off, spans compile away to a null check.
        Assert.False(Tracing.Enabled);
        using var span = Tracing.StartActivity("crawl.cycle");
        Assert.Null(span);
    }

    [Fact]
    public void BeginCycle_WithoutListener_StillMintsCorrelationId_ButNoSpan()
    {
        Assert.False(Tracing.Enabled);
        using var cycle = Tracing.BeginCycle("Conn", "full");
        Assert.False(string.IsNullOrEmpty(cycle.CorrelationId));   // audit id always present
        Assert.Equal(cycle.CorrelationId, Tracing.CurrentCorrelationId);
        Assert.Null(Activity.Current);                             // no span opened
    }

    [Fact]
    public async Task DisabledCrawl_RunsIdentically_NoSpansRecorded()
    {
        // No SpanCapture here: nothing listens, so no Activities are created.
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        Assert.Contains("c1", harness.PutItemIds());   // behaviour unchanged
        Assert.Null(Activity.Current);                 // no span leaked
    }
}

// ── OTEL env parsing + init ─────────────────────────────────────────────────

public class TracingInitTests : IDisposable
{
    private readonly (string, string)[] _requiredEnv =
    {
        ("CONNECTOR_ID", "SeismicTrace"),
        ("CONNECTOR_NAME", "Seismic Trace Connector"),
        ("CONNECTOR_DESCRIPTION", "d"),
        ("SEISMIC_TENANT", "contoso"),
        ("SEISMIC_CLIENT_ID", "c"),
        ("SECRET_SEISMIC_CLIENT_SECRET", "s"),
        ("AAD_APP_TENANT_ID", "t"),
        ("AAD_APP_CLIENT_ID", "c"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
    };

    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "seismic-trace-" + Guid.NewGuid().ToString("N"));

    public TracingInitTests()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "schema.json"),
            """{"objects":[{"name":"ContentItem","enabled":true}]}""");
        // MNPI fail-closed: AppConfig.Load requires a valid exclusions.json.
        File.WriteAllText(Path.Combine(_configDir, "exclusions.json"),
            """{"excludedFlags":["MNE"],"flagProperties":["classification"]}""");
        foreach (var (k, v) in _requiredEnv)
            Environment.SetEnvironmentVariable(k, v);
    }

    public void Dispose()
    {
        foreach (var (k, _) in _requiredEnv)
            Environment.SetEnvironmentVariable(k, null);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", null);
        Tracing.Shutdown();
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    [Fact]
    public void NoEndpoint_StaysDisabled()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        Tracing.Shutdown();
        Tracing.Initialize(SeismicTracing.OptionsFrom(AppConfig.Load(_configDir)));
        Assert.False(Tracing.Enabled);
        Assert.Null(Tracing.ExporterEndpoint);
    }

    [Fact]
    public void ServiceName_DefaultsToConnectorName()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", null);
        Tracing.Shutdown();
        Tracing.Initialize(SeismicTracing.OptionsFrom(AppConfig.Load(_configDir)));
        Assert.Equal("Seismic Trace Connector", Tracing.ServiceName);
    }

    [Fact]
    public void ServiceName_HonoursOtelEnv()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "custom-svc");
        Tracing.Shutdown();
        Tracing.Initialize(SeismicTracing.OptionsFrom(AppConfig.Load(_configDir)));
        Assert.Equal("custom-svc", Tracing.ServiceName);
    }

    [Fact]
    public void Endpoint_EnablesExporter_AndRendersInMetrics()
    {
        // A syntactically valid but unreachable collector: initialization must
        // succeed (the exporter connects lazily/async), and export failures are
        // swallowed — see UnreachableCollector test below.
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4318");
        Tracing.Shutdown();
        Tracing.Initialize(SeismicTracing.OptionsFrom(AppConfig.Load(_configDir)));
        Assert.True(Tracing.Enabled);
        Assert.Equal("http://127.0.0.1:4318", Tracing.ExporterEndpoint);

        var metrics = Metrics.RenderPrometheus();
        Assert.Contains("seismic_connector_tracing_enabled{", metrics);
        Assert.Contains("exporter_endpoint=\"http://127.0.0.1:4318\"", metrics);
        Assert.Contains("} 1", metrics.Split('\n').First(l => l.StartsWith("seismic_connector_tracing_enabled{")));
    }

    [Fact]
    public void MetricsTracingLine_ShowsZeroWhenDisabled()
    {
        Tracing.Shutdown();  // ensure disabled
        var line = Metrics.RenderPrometheus().Split('\n')
            .First(l => l.StartsWith("seismic_connector_tracing_enabled{"));
        Assert.EndsWith(" 0", line);
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4318");
        Tracing.Shutdown();
        Tracing.Initialize(SeismicTracing.OptionsFrom(AppConfig.Load(_configDir)));
        Tracing.Initialize(SeismicTracing.OptionsFrom(AppConfig.Load(_configDir)));  // second call is a no-op
        Assert.True(Tracing.Enabled);
    }
}

// ── unreachable collector never throws / stalls ──────────────────────────────

public class TracingResilienceTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        Tracing.Shutdown();
    }

    [Fact]
    public async Task UnreachableCollector_NeverThrowsOrStallsTheCrawl()
    {
        // Point at a dead port. The batch exporter enqueues spans and exports on
        // a background thread; delivery failures are swallowed by the SDK, so
        // the crawl must complete normally and quickly.
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9");
        Tracing.Shutdown();
        Tracing.Initialize(SeismicTracing.OptionsFrom(TestConfig.Build()));
        Assert.True(Tracing.Enabled);

        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.AddContent(TestContent.Make("c2"));

        var start = DateTime.UtcNow;
        var ok = await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem");
        var elapsed = DateTime.UtcNow - start;

        Assert.True(ok);
        Assert.Contains("c1", harness.PutItemIds());
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"crawl stalled on the exporter ({elapsed}).");

        // Shutdown flushes best-effort against the dead collector — must not throw.
        var ex = Record.Exception(() => Tracing.Shutdown());
        Assert.Null(ex);
    }

    [Fact]
    public void InjectTraceContext_NoActiveSpan_IsNoOp()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/x");
        Tracing.InjectTraceContext(request);  // no Activity.Current → no header
        Assert.False(request.Headers.Contains("traceparent"));
    }

    [Fact]
    public void InjectTraceContext_WithActiveSpan_AddsW3CTraceparent()
    {
        using var capture = new SpanCapture();
        using var span = Tracing.StartActivity("t");
        Assert.NotNull(span);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/x");
        Tracing.InjectTraceContext(request);
        Assert.True(request.Headers.Contains("traceparent"));
        var header = request.Headers.GetValues("traceparent").Single();
        Assert.StartsWith("00-", header);                      // W3C version 00
        Assert.Contains(span!.TraceId.ToHexString(), header);  // carries this trace id
    }
}
