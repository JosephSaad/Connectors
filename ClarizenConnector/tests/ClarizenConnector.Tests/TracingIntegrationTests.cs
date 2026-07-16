// Correlation-id propagation to logs + dead-letter, OTEL env-var config
// parsing, the tracing_enabled metric, and the collector-unreachable guard.

using System.Diagnostics;
using System.Text.Json.Nodes;
using ClarizenConnector.Config;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

public class CorrelationLoggingTests : IDisposable
{
    public CorrelationLoggingTests() => Logging.ResetForTests();

    public void Dispose() => Logging.ResetForTests();

    [Fact]
    public void JsonLog_CarriesCorrelationId_WhenInScope()
    {
        using var scope = new EnvScope(("LOG_FORMAT", "json"), ("LOG_LEVEL", null));
        using (CorrelationContext.Begin("corr-123"))
        {
            var line = EmitAndCaptureJson("hello");
            Assert.Equal("corr-123", line["correlation_id"]!.GetValue<string>());
            Assert.Equal("hello", line["message"]!.GetValue<string>());
        }
    }

    [Fact]
    public void JsonLog_OmitsCorrelationId_WhenOutsideScope()
    {
        using var scope = new EnvScope(("LOG_FORMAT", "json"), ("LOG_LEVEL", null));
        var line = EmitAndCaptureJson("no-scope");
        Assert.Null(line["correlation_id"]);
    }

    [Fact]
    public void CorrelationId_StableWithinCycle_AcrossLogLines()
    {
        using var scope = new EnvScope(("LOG_FORMAT", "json"), ("LOG_LEVEL", null));
        using (CorrelationContext.Begin())
        {
            var a = EmitAndCaptureJson("first")["correlation_id"]!.GetValue<string>();
            var b = EmitAndCaptureJson("second")["correlation_id"]!.GetValue<string>();
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void TextLog_PrefixesCorrelationId()
    {
        using var scope = new EnvScope(("LOG_FORMAT", null), ("LOG_LEVEL", null));
        using (CorrelationContext.Begin("abcdef0123456789abcdef0123456789"))
        {
            string? rendered = null;
            Logging.RenderedSink = l => rendered = l;
            Logging.GetLogger("clarizen_connector.test").Warning("hi");
            Logging.RenderedSink = null;
            Assert.NotNull(rendered);
            Assert.Contains("[abcdef01]", rendered);  // first 8 chars of the id
        }
    }

    /// <summary>Capture the REAL rendered JSON line the logger writes to its sinks.</summary>
    private static JsonObject EmitAndCaptureJson(string message)
    {
        string? rendered = null;
        Logging.RenderedSink = line => rendered = line;
        Logging.GetLogger("clarizen_connector.test").Warning(message);
        Logging.RenderedSink = null;
        Assert.NotNull(rendered);
        return JsonNode.Parse(rendered!)!.AsObject();
    }
}

public class CorrelationDeadLetterTests
{
    [Fact]
    public void DeadLetterRecord_CarriesCorrelationId_WhenInScope()
    {
        using var scope = new SyncStateScope();
        using (CorrelationContext.Begin("cycle-xyz"))
        {
            SyncState.AppendFailedRecords(
                "Conn", new List<(string, string)> { ("Task_1", "boom") }, "Task");
        }
        var entry = Assert.Single(SyncState.ReadFailedRecords("Conn"));
        Assert.Equal("cycle-xyz", entry["correlation_id"]!.GetValue<string>());
    }

    [Fact]
    public void DeadLetterRecord_OmitsCorrelationId_WhenOutsideScope()
    {
        using var scope = new SyncStateScope();
        SyncState.AppendFailedRecords(
            "Conn", new List<(string, string)> { ("Task_2", "boom") }, "Task");
        var entry = Assert.Single(SyncState.ReadFailedRecords("Conn"));
        Assert.Null(entry["correlation_id"]);
    }

    [Fact]
    public void DeadLetter_SameCorrelationId_AcrossAppendsInCycle()
    {
        using var scope = new SyncStateScope();
        using (CorrelationContext.Begin("one-cycle"))
        {
            SyncState.AppendFailedRecords("Conn", new List<(string, string)> { ("A_1", "e") }, "A");
            SyncState.AppendFailedRecords("Conn", new List<(string, string)> { ("A_2", "e") }, "A");
        }
        var entries = SyncState.ReadFailedRecords("Conn");
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("one-cycle", e["correlation_id"]!.GetValue<string>()));
    }
}

public class TracingConfigTests
{
    [Fact]
    public void Initialize_WithEndpoint_EnablesExporter_NoThrow()
    {
        Tracing.ResetForTests();
        // An unreachable collector must NOT throw or stall — the exporter is
        // batched/fire-and-forget; building the provider succeeds regardless.
        using var scope = new EnvScope(
            (Tracing.EndpointEnvVar, "http://127.0.0.1:59999"),   // nothing listening
            (Tracing.ProtocolEnvVar, "grpc"),
            (Tracing.ServiceNameEnvVar, "clarizen-test-svc"));

        IDisposable? handle = null;
        var exception = Record.Exception(() => handle = Tracing.Initialize("Clarizen AdaptiveWork"));
        Assert.Null(exception);
        Assert.NotNull(handle);
        Assert.True(Tracing.Enabled);
        Assert.Equal("http://127.0.0.1:59999", Tracing.Endpoint);
        Assert.Equal("clarizen-test-svc", Tracing.ServiceName);

        // Emitting a span against the (dead) collector must never throw.
        var emit = Record.Exception(() =>
        {
            using (Tracing.StartCrawlCycle("full", "Conn")) { }
        });
        Assert.Null(emit);

        // Dispose flushes (bounded) without throwing even though nothing listens.
        var dispose = Record.Exception(() => handle!.Dispose());
        Assert.Null(dispose);
        Tracing.ResetForTests();
    }

    [Fact]
    public void TracingEnabledMetric_ReflectsState()
    {
        Tracing.ResetForTests();
        using (new EnvScope((Tracing.EndpointEnvVar, null)))
        {
            Tracing.Initialize("svc");
            Assert.Contains("clarizen_connector_tracing_enabled 0", Metrics.RenderPrometheus());
        }

        using (new EnvScope((Tracing.EndpointEnvVar, "http://127.0.0.1:59998")))
        {
            using var handle = Tracing.Initialize("svc");
            Assert.Contains("clarizen_connector_tracing_enabled 1", Metrics.RenderPrometheus());
        }
        Tracing.ResetForTests();
    }
}
