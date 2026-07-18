// OpenTelemetry tracing + correlation-id tests. An in-memory ActivityListener
// captures spans (no network, no exporter). Overhead-when-off is proven by
// asserting StartActivity returns null with no listener registered.

using System.Diagnostics;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

/// <summary>Registers a listener on the connector's ActivitySource and records
/// every started/stopped Activity for assertions. Dispose removes it.</summary>
public sealed class SpanCapture : IDisposable
{
    private readonly ActivityListener _listener;
    public List<Activity> Started { get; } = new();

    public SpanCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStarted = a => Started.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Activity? ByName(string name) => Started.FirstOrDefault(a => a.OperationName == name);

    public void Dispose() => _listener.Dispose();
}

public class TracingOverheadWhenOffTests
{
    [Fact]
    public void NoListener_StartActivityIsNull_CheapNoOp()
    {
        // With no ActivityListener on the source (tracing off / not initialised),
        // every span helper returns null — the O(1) HasListeners() short-circuit.
        Assert.Null(Tracing.Source.StartActivity("anything"));
        Assert.Null(Tracing.StartCrawlCycle("full", "Conn"));
        Assert.Null(Tracing.StartObjectCrawl("Task", "Conn"));
        Assert.Null(Tracing.StartSourceFetch("Task", "bdh"));
        Assert.Null(Tracing.StartTransform("Task", 10));
        Assert.Null(Tracing.StartGraphIngest("Task", 10));
        Assert.Null(Tracing.StartDeletionSweep("Task"));
    }

    [Fact]
    public void Initialize_NoEndpoint_RegistersNothing()
    {
        Tracing.ResetForTests();
        using var scope = new EnvScope((Tracing.EndpointEnvVar, null));
        var handle = Tracing.Initialize("BDH Hadoop Data Mart");
        Assert.Null(handle);           // nothing to dispose
        Assert.False(Tracing.Enabled); // no exporter
        Assert.Null(Tracing.Endpoint);
        // And no listener → still a no-op.
        Assert.Null(Tracing.Source.StartActivity("x"));
    }

    [Fact]
    public void ResolveServiceName_EnvOverridesDefault()
    {
        using (new EnvScope((Tracing.ServiceNameEnvVar, null)))
            Assert.Equal("BDH Hadoop Data Mart", Tracing.ResolveServiceName("BDH Hadoop Data Mart"));
        using (new EnvScope((Tracing.ServiceNameEnvVar, "custom-svc")))
            Assert.Equal("custom-svc", Tracing.ResolveServiceName("BDH Hadoop Data Mart"));
    }
}

public class TracingSpanTests
{
    [Fact]
    public void CrawlCycle_ParentsChildSpans()
    {
        using var capture = new SpanCapture();

        using (var cycle = Tracing.StartCrawlCycle("full", "Conn"))
        {
            using var correlation = CorrelationContext.Begin();
            cycle?.SetTag("bdh.correlation_id", CorrelationContext.Current);
            using (var obj = Tracing.StartObjectCrawl("Task", "Conn"))
            {
                using (Tracing.StartSourceFetch("Task", "bdh")) { }
                using (Tracing.StartTransform("Task", 3)) { }
                using (Tracing.StartGraphIngest("Task", 3)) { }
            }
        }

        var cycleSpan = capture.ByName("crawl.cycle");
        var objectSpan = capture.ByName("crawl.object");
        var fetchSpan = capture.ByName("source.fetch");
        Assert.NotNull(cycleSpan);
        Assert.NotNull(objectSpan);
        Assert.NotNull(fetchSpan);

        // Parent/child: object under cycle, fetch under object.
        Assert.Equal(cycleSpan!.SpanId, objectSpan!.ParentSpanId);
        Assert.Equal(objectSpan.SpanId, fetchSpan!.ParentSpanId);
        // All share one trace id.
        Assert.Equal(cycleSpan.TraceId, fetchSpan.TraceId);
    }

    [Fact]
    public void Spans_CarryExpectedTags()
    {
        using var capture = new SpanCapture();
        using (Tracing.StartSourceFetch("Contact", "bdh")) { }
        using (Tracing.StartTransform("Lead", 42)) { }
        using (Tracing.StartDeletionSweep("Case")) { }

        Assert.Equal("bdh", capture.ByName("source.fetch")!.GetTagItem("bdh.source"));
        Assert.Equal("Contact", capture.ByName("source.fetch")!.GetTagItem("bdh.object_type"));
        Assert.Equal(42, capture.ByName("transform.chunk")!.GetTagItem("bdh.record_count"));
        Assert.Equal("Case", capture.ByName("deletion.sweep")!.GetTagItem("bdh.object_type"));
    }

    [Fact]
    public void CorrelationIdTag_MatchesContextAndTraceId()
    {
        using var capture = new SpanCapture();
        string? tagged;
        string? contextId;
        ActivityTraceId traceId;

        using (var cycle = Tracing.StartCrawlCycle("incremental", "Conn"))
        {
            using var correlation = CorrelationContext.Begin();
            cycle!.SetTag("bdh.correlation_id", CorrelationContext.Current);
            tagged = cycle.GetTagItem("bdh.correlation_id") as string;
            contextId = CorrelationContext.Current;
            traceId = cycle.TraceId;
        }

        Assert.NotNull(tagged);
        Assert.Equal(contextId, tagged);
        // Begin() adopts the active span's TraceId, so they are the same string.
        Assert.Equal(traceId.ToHexString(), contextId);
    }

    [Fact]
    public void ChildSpanStamp_CarriesAmbientCorrelationId()
    {
        using var capture = new SpanCapture();
        using (Tracing.StartCrawlCycle("full", "Conn"))
        using (CorrelationContext.Begin())
        {
            var expected = CorrelationContext.Current;
            using (Tracing.StartObjectCrawl("Task", "Conn")) { }
            Assert.Equal(expected, capture.ByName("crawl.object")!.GetTagItem("bdh.correlation_id"));
        }
    }
}

public class CorrelationContextTests
{
    [Fact]
    public void Current_NullOutsideScope()
    {
        Assert.Null(CorrelationContext.Current);
    }

    [Fact]
    public void Begin_SetsAndRestores()
    {
        Assert.Null(CorrelationContext.Current);
        using (CorrelationContext.Begin("abc123"))
        {
            Assert.Equal("abc123", CorrelationContext.Current);
            using (CorrelationContext.Begin("nested"))
                Assert.Equal("nested", CorrelationContext.Current);
            Assert.Equal("abc123", CorrelationContext.Current);  // restored
        }
        Assert.Null(CorrelationContext.Current);  // restored
    }

    [Fact]
    public void Begin_GeneratesId_WhenNoneAndNoSpan()
    {
        using (CorrelationContext.Begin())
        {
            Assert.NotNull(CorrelationContext.Current);
            Assert.Equal(32, CorrelationContext.Current!.Length);  // W3C trace-id hex
        }
    }

    [Fact]
    public async Task Current_FlowsAcrossAwait()
    {
        using (CorrelationContext.Begin("flow-id"))
        {
            await Task.Yield();
            Assert.Equal("flow-id", CorrelationContext.Current);
            await Task.Run(() => Assert.Equal("flow-id", CorrelationContext.Current));
        }
    }

    [Fact]
    public void NewId_Is32HexAndUnique()
    {
        var a = CorrelationContext.NewId();
        var b = CorrelationContext.NewId();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
        Assert.All(a, c => Assert.True(Uri.IsHexDigit(c)));
    }
}
