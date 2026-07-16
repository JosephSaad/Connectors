// Improvement round 4: OpenTelemetry distributed tracing + correlation ids.
// Spans + parent/child + tags via an in-memory ActivityListener (no network),
// correlation id on logs/dead-letter/reports/spans, the NO-PII-on-spans proof,
// disabled-path inertness, OTEL env parsing, and collector-unreachable safety.

using System.Diagnostics;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

/// <summary>
/// Captures every Activity from the connector's ActivitySource for the lifetime
/// of the scope (in-memory; no exporter / no network). Records all spans always
/// (so tests see them without an OTLP endpoint).
/// </summary>
public sealed class SpanCapture : IDisposable
{
    private readonly ActivityListener _listener;
    public List<Activity> Spans { get; } = new();

    public SpanCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == Telemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (Spans) Spans.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Activity? ByName(string name)
    {
        lock (Spans) return Spans.FirstOrDefault(s => s.OperationName == name);
    }

    /// <summary>Tag value as string (tags may be int/bool/string).</summary>
    public static string? Tag(Activity span, string key) => span.GetTagItem(key)?.ToString();

    public IEnumerable<string> AllTagValues()
    {
        lock (Spans)
            return Spans.SelectMany(s => s.Tags).Select(t => t.Value ?? "").ToList();
    }

    public void Dispose() => _listener.Dispose();
}

public class TelemetryEnvTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", null);
    }

    [Fact]
    public void DisabledWhenNoEndpoint()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        Assert.False(Telemetry.TracingEnabled);
        Assert.Null(Telemetry.OtlpEndpoint);
        Assert.Contains("tracing=off", Telemetry.StatusLine("AltrataWealth"));
    }

    [Fact]
    public void EndpointEnablesTracing()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317");
        Assert.True(Telemetry.TracingEnabled);
        Assert.Equal("http://collector:4317", Telemetry.OtlpEndpoint);
        Assert.Contains("endpoint=http://collector:4317", Telemetry.StatusLine("AltrataWealth"));
    }

    [Fact]
    public void ServiceNameDefaultsToConnectorNameAndHonoursOverride()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", null);
        Assert.Equal("AltrataWealth", Telemetry.ServiceName("AltrataWealth"));
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "custom-svc");
        Assert.Equal("custom-svc", Telemetry.ServiceName("AltrataWealth"));
    }

    [Fact]
    public void UnreachableCollectorNeverThrowsAndProviderDisposesCleanly()
    {
        // A bogus endpoint must not fail/stall — the OTLP exporter batches in the
        // background; StartIfConfigured and Dispose complete without throwing.
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1/no-collector");
        var provider = Telemetry.StartIfConfigured("AltrataWealth");
        Assert.NotNull(provider);

        using (var span = Telemetry.Span("crawl"))
            Telemetry.SetTag(span, "altrata.crawl.kind", "full");  // exported in background, drops silently

        var ex = Record.Exception(() => provider!.Dispose());
        Assert.Null(ex);
    }
}

public class TelemetryTagAllowlistTests
{
    [Fact]
    public void RejectsKeysOutsideTheAllowlist()
    {
        using var capture = new SpanCapture();
        using (var span = Telemetry.Span("crawl"))
        {
            Telemetry.SetTag(span, "altrata.person.name", "Ada Lovelace");   // NOT allowlisted
            Telemetry.SetTag(span, "altrata.person.email", "ada@x.com");     // NOT allowlisted
            Telemetry.SetTag(span, "altrata.dataset", "PersonProfile");      // allowlisted
        }
        var crawl = capture.ByName("crawl")!;
        Assert.DoesNotContain(crawl.Tags, t => t.Key == "altrata.person.name");
        Assert.DoesNotContain(crawl.Tags, t => t.Key == "altrata.person.email");
        Assert.Contains(crawl.Tags, t => t.Key == "altrata.dataset");
    }

    [Fact]
    public void EveryAllowlistedKeyIsAnIdCountHashOrEnum()
    {
        // Guard against a future PII-shaped key sneaking onto the allowlist.
        foreach (var key in Telemetry.AllowedTagKeys)
        {
            var lower = key.ToLowerInvariant();
            Assert.DoesNotContain("name", lower);
            Assert.DoesNotContain("email", lower);
            Assert.DoesNotContain("wealth", lower);
            Assert.DoesNotContain("networth", lower);
            Assert.DoesNotContain("net_worth", lower);
            Assert.DoesNotContain("phone", lower);
            Assert.DoesNotContain("address", lower);
        }
    }
}

public class CorrelationContextTests
{
    [Fact]
    public void CurrentIsNullOutsideAScope()
    {
        Assert.Null(CorrelationContext.Current);
    }

    [Fact]
    public void ScopeSetsAndRestores()
    {
        Assert.Null(CorrelationContext.Current);
        using (CorrelationContext.Begin("abc123"))
        {
            Assert.Equal("abc123", CorrelationContext.Current);
            using (CorrelationContext.Begin())
                Assert.NotEqual("abc123", CorrelationContext.Current);  // nested fresh id
            Assert.Equal("abc123", CorrelationContext.Current);         // restored
        }
        Assert.Null(CorrelationContext.Current);
    }

    [Fact]
    public void SpanTagsTheCurrentCorrelationId()
    {
        using var capture = new SpanCapture();
        using (CorrelationContext.Begin("corr-xyz"))
        using (var span = Telemetry.Span("crawl")) { }
        Assert.Equal("corr-xyz",
            capture.ByName("crawl")!.Tags.First(t => t.Key == "altrata.correlation_id").Value);
    }
}

public class DisabledTracingInertTests
{
    [Fact]
    public void NoListenerMeansStartActivityReturnsNullAndCostsNothing()
    {
        // With no ActivityListener registered (the default, OTLP off) Span(...)
        // returns null and SetTag is a no-op — the "overhead when off" guarantee.
        Assert.Null(Telemetry.Span("crawl"));
        Telemetry.SetTag(null, "altrata.dataset", "PersonProfile");  // must not throw
        Telemetry.InjectTraceContext(new HttpRequestMessage());       // no Activity → no-op
    }
}

// ---- crawl spans: parent/child + tags ------------------------------------------

public class CrawlTracingTests
{
    [Fact]
    public async Task CrawlEmitsExpectedSpanTreeWithSafeTags()
    {
        using var capture = new SpanCapture();
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "Ada", null, null), ("P2", "Bob", null, null)), 2));

        await harness.Engine.RunAsync(CrawlKind.Full);

        // (graph.batch + HTTP spans live in the real GraphClient — the harness
        // uses a fake, so they are exercised by GraphBatchTracingTests instead.)
        var crawl = capture.ByName("crawl");
        var seat = capture.ByName("seat.sync");
        var delivery = capture.ByName("delivery");
        var manifest = capture.ByName("manifest.validate");
        var dataset = capture.ByName("dataset.ingest");
        Assert.NotNull(crawl);
        Assert.NotNull(seat);
        Assert.NotNull(delivery);
        Assert.NotNull(manifest);
        Assert.NotNull(dataset);

        // Parent/child: everything descends from the crawl root's trace.
        Assert.Equal(crawl!.TraceId, delivery!.TraceId);
        Assert.Equal(crawl.SpanId, seat!.ParentSpanId);
        Assert.Equal(crawl.SpanId, delivery.ParentSpanId);
        Assert.Equal(delivery.SpanId, manifest!.ParentSpanId);
        Assert.Equal(delivery.SpanId, dataset!.ParentSpanId);

        // Safe tags present (values may be int/bool/string → compare as strings).
        Assert.Equal("full", SpanCapture.Tag(crawl, "altrata.crawl.kind"));
        Assert.Equal("PersonProfile", SpanCapture.Tag(dataset, "altrata.dataset"));
        Assert.Equal("2", SpanCapture.Tag(dataset, "altrata.records.ingested"));
        Assert.Equal("reconciled", SpanCapture.Tag(delivery, "altrata.delivery.status"));
    }

    [Fact]
    public async Task NoPersonalDataAppearsInAnySpanNameOrTag()
    {
        using var capture = new SpanCapture();
        // A record loaded with PII: name, email, wealth, plus a CRM contact.
        var crmDir = TestFixtures.NewTempDir("pii_crm");
        var crmPath = Path.Combine(crmDir, "contacts.json");
        File.WriteAllText(crmPath, """[{"id":"C1","email":"ada@contoso.com","name":"Ada Lovelace"}]""");

        using var harness = new CrawlHarness(crmContactsPath: crmPath,
            configure: c => c with { EntityFuzzyMatching = true, RelationshipPaths = true });
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, """
                [{"id":"P1","person_name":"Ada Lovelace","email":"ada@contoso.com","net_worth_usd":"9000000","employer":"Analytical Engines"}]
                """, 1),
            ("wealth.json", Datasets.WealthIndicator,
                """[{"id":"W1","person_id":"P1","net_worth_usd":"9000000"}]""", 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        // Assert none of the sensitive literals appear in ANY span name or tag value.
        string[] pii = { "Ada Lovelace", "Ada", "ada@contoso.com", "9000000", "Analytical Engines" };
        var haystack = new List<string>();
        foreach (var span in capture.Spans)
        {
            haystack.Add(span.OperationName);
            haystack.Add(span.DisplayName);
            haystack.AddRange(span.Tags.Select(t => t.Value?.ToString() ?? ""));
        }
        var blob = string.Join("", haystack);
        foreach (var secret in pii)
            Assert.DoesNotContain(secret, blob);

        // Sanity: the pipeline really did run and ingest (so the assertion is meaningful).
        Assert.Contains(harness.Graph.PutItems, i => i.Id == "PersonProfile-P1");
        Assert.NotEmpty(capture.Spans);
    }

    [Fact]
    public async Task PathIndexBuildIsSpannedWhenEnabled()
    {
        using var capture = new SpanCapture();
        using var harness = new CrawlHarness(configure: c => c with { RelationshipPaths = true });
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "Ada", null, null)), 1),
            ("rels.json", Datasets.RelationshipPath,
                """[{"id":"R1","from_person_id":"P1","to_person_id":"P2","intermediary_count":"0"}]""", 1));

        await harness.Engine.RunAsync(CrawlKind.Full);
        var path = capture.ByName("path.index.build");
        Assert.NotNull(path);
        Assert.Equal("1", SpanCapture.Tag(path!, "altrata.path.edges"));
    }

    [Fact]
    public async Task SeatInvariantUnaffectedByTracing()
    {
        using var capture = new SpanCapture();
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "Ada", null, null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        // Items are still seat-only; tracing never touched the ACL.
        foreach (var item in harness.Graph.PutItems)
            Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");
    }
}

// ---- graph.batch span (real GraphClient) ----------------------------------------

public class GraphBatchTracingTests
{
    private static ExternalItem Item(string id) => new()
    {
        Id = id,
        Acl = new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } },
        Properties = new Dictionary<string, object?> { ["title"] = id },
    };

    [Fact]
    public async Task GraphBatchIsSpannedAsAChildWithSafeTags()
    {
        using var capture = new SpanCapture();
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, """{"responses":[{"id":"0","status":201},{"id":"1","status":201}]}""");
        var client = new GraphClient(TestFixtures.NewConfig(), handler, (_, _) => Task.CompletedTask);

        using (var parent = Telemetry.Span("crawl"))
        {
            await client.PutItemsBatchAsync(new[] { Item("P1"), Item("P2") });
        }

        var graph = capture.ByName("graph.batch");
        var crawl = capture.ByName("crawl");
        Assert.NotNull(graph);
        Assert.Equal(crawl!.SpanId, graph!.ParentSpanId);          // child of the crawl
        Assert.Equal("put", SpanCapture.Tag(graph, "altrata.graph.op"));
        Assert.Equal("2", SpanCapture.Tag(graph, "altrata.graph.batch.size"));
        Assert.Equal("2", SpanCapture.Tag(graph, "altrata.graph.batch.ok"));
        Assert.Equal("0", SpanCapture.Tag(graph, "altrata.graph.batch.failed"));
    }

    [Fact]
    public async Task OutboundGraphRequestCarriesTheTraceparentHeader()
    {
        using var capture = new SpanCapture();
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, "{}");
        var client = new GraphClient(TestFixtures.NewConfig(), handler, (_, _) => Task.CompletedTask);

        using (Telemetry.Span("crawl"))
            await client.DeleteItemAsync("x1");

        // The item DELETE (2nd request) carries a W3C traceparent.
        var itemRequest = handler.Requests[^1];
        Assert.True(itemRequest.Headers.Contains("traceparent"));
    }
}

// ---- correlation id on logs / dead-letter / reports -----------------------------

public class CorrelationStampingTests
{
    [Fact]
    public void JsonLogsCarryTheCorrelationId()
    {
        var logs = TestFixtures.NewTempDir("corrlogs");
        Environment.SetEnvironmentVariable("LOGS_DIR", logs);
        Environment.SetEnvironmentVariable("LOG_FORMAT", "json");
        Logging.StartRun("corr");
        try
        {
            using (CorrelationContext.Begin("corr-42"))
                Logging.GetLogger("test").Warning("with-correlation");
            Logging.GetLogger("test").Warning("without-correlation");
        }
        finally
        {
            Logging.EndRun();
            Environment.SetEnvironmentVariable("LOG_FORMAT", null);
            Environment.SetEnvironmentVariable("LOGS_DIR", null);
        }

        var logFile = Directory.EnumerateFiles(logs, "connector.log", SearchOption.AllDirectories).Single();
        var lines = File.ReadAllLines(logFile).Where(l => l.Contains("correlation")).ToArray();
        var withLine = lines.Single(l => l.Contains("with-correlation"));
        Assert.Contains("\"correlationId\":\"corr-42\"", withLine);
        var withoutLine = lines.Single(l => l.Contains("without-correlation"));
        Assert.Contains("\"correlationId\":null", withoutLine);
    }

    [Fact]
    public async Task DeadLetterRecordsAndReportsShareTheCrawlCorrelationId()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "A", null, null), ("P2", "B", null, null)), 2));
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");

        await harness.Engine.RunAsync(CrawlKind.Full);

        var deadLetters = harness.State.ReadDeadLetters();
        var dl = Assert.Single(deadLetters);
        Assert.False(string.IsNullOrEmpty(dl.CorrelationId));

        // The reconciliation report for the delivery carries the same id.
        var reportPath = Reconciliation.ReportPath(harness.Config.ConnectorId, "d1",
            Path.Combine(harness.Root, "logs"));
        var reportText = File.ReadAllText(reportPath);
        Assert.Contains(dl.CorrelationId!, reportText);
    }

    [Fact]
    public async Task ErasureLedgerEntryCarriesCorrelationIdAndStillVerifies()
    {
        var root = TestFixtures.NewTempDir("corr_erase");
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        runtime.Identity.RecordIngestedItem(new IngestedItem("PersonProfile-P1", Datasets.PersonProfile, "h", DateTime.UtcNow));
        runtime.Identity.RecordItemSubjects("PersonProfile-P1", new[] { "P1" });

        await CommandRegistry.ForgetSubjectAsync(runtime, "P1", null, "joseph", confirm: true);

        var entry = Assert.Single(runtime.Erasure.ReadAll());
        Assert.False(string.IsNullOrEmpty(entry.CorrelationId));
        Assert.True(runtime.Erasure.Verify(out _));  // correlation id is inside the hash chain
    }
}

// ---- W3C propagation ------------------------------------------------------------

public class TraceContextInjectionTests
{
    [Fact]
    public void TraceparentIsInjectedWhenAnActivityIsCurrent()
    {
        using var capture = new SpanCapture();
        using var activity = Telemetry.Span("crawl");
        Assert.NotNull(activity);  // capture registers a listener, so a span exists
        var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/x");
        Telemetry.InjectTraceContext(request);
        Assert.True(request.Headers.Contains("traceparent"));
        Assert.Contains(activity!.TraceId.ToHexString(),
            request.Headers.GetValues("traceparent").First());
    }
}
