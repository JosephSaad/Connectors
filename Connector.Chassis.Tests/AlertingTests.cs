// AlertingTests.cs
// ----------------
// Tests for Connector.Chassis/Alerting.cs — the fleet's outbound webhook.
//
// Alerting exists so an operator finds out that something ELSE broke, which
// makes its contract almost entirely negative: it must not send when it is not
// configured, and it must not throw, ever, for any reason, into a crawl that
// was only trying to report bad news. A connector dying because its alert POST
// failed is the exact inversion of the module's purpose, so the "never throws"
// cases below are asserted hard and deliberately over-specified: bad status,
// dead transport, cancelled request, garbage URL, unserialisable payload.
//
// The rest is envelope shape (five connectors and their webhook receivers parse
// this JSON), the dead-letter threshold's off-by-one, and the HandlerFactory
// seam that lets four different transports share one alerting implementation.
//
// Everything here mutates process-global state — ALERT_* / PROXY_URL env vars,
// Alerting.HttpClient, Alerting.HandlerFactory, Alerting.ConnectorId — so every
// test class derives from AlertingGlobalStateGuard, which snapshots the lot in
// its constructor and puts it back in Dispose. Leaking a fake HttpMessageHandler
// into another agent's test class is how a merged suite starts failing in ways
// nobody can reproduce alone.

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Connector.Chassis.Tests;

/// <summary>
/// Snapshot/restore for every global <c>Alerting</c> touches, plus the fake
/// transport the tests inject. Not a test class (abstract, so xunit skips it).
/// </summary>
public abstract class AlertingGlobalStateGuard : IDisposable
{
    private readonly string? _webhook = Environment.GetEnvironmentVariable(Alerting.WebhookUrlEnvVar);
    private readonly string? _threshold = Environment.GetEnvironmentVariable(Alerting.DeadLetterThresholdEnvVar);
    private readonly string? _proxy = Environment.GetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar);
    private readonly string? _connectorId = Alerting.ConnectorId;
    private readonly Func<HttpMessageHandler>? _handlerFactory = Alerting.HandlerFactory;

    // Reading the property materialises the client if nothing has yet — which is
    // the point: whatever the assembly had (or would lazily build) is what gets
    // put back, so the next test class sees the transport it expected.
    private readonly HttpClient _client = Alerting.HttpClient;

    private readonly List<HttpClient> _created = new();

    public void Dispose()
    {
        // Statics first, then dispose the fakes — never leave the module pointing
        // at a client whose handler has already been torn down.
        Environment.SetEnvironmentVariable(Alerting.WebhookUrlEnvVar, _webhook);
        Environment.SetEnvironmentVariable(Alerting.DeadLetterThresholdEnvVar, _threshold);
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, _proxy);
        Alerting.ConnectorId = _connectorId;
        Alerting.HandlerFactory = _handlerFactory;
        Alerting.HttpClient = _client;
        foreach (var client in _created)
        {
            client.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>One captured outbound request.</summary>
    protected sealed record Captured(HttpMethod Method, string? Uri, string? Body, string? MediaType, string? Charset);

    /// <summary>
    /// Fake transport: records every request, then optionally blocks on
    /// <paramref name="gate"/>, throws, or answers with a chosen status.
    /// </summary>
    protected sealed class ProbeHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        Func<Exception>? fail = null,
        Task? gate = null) : HttpMessageHandler
    {
        private readonly List<Captured> _seen = new();

        public int Calls
        {
            get { lock (_seen) { return _seen.Count; } }
        }

        public Captured Last
        {
            get { lock (_seen) { return _seen[^1]; } }
        }

        public IReadOnlyList<Captured> Seen
        {
            get { lock (_seen) { return _seen.ToArray(); } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read the body before anything can fail: a request that the handler
            // then rejects still has to be inspectable, otherwise "it attempted
            // the POST" is unprovable on the failure paths.
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_seen)
            {
                _seen.Add(new Captured(
                    request.Method,
                    request.RequestUri?.ToString(),
                    body,
                    request.Content?.Headers.ContentType?.MediaType,
                    request.Content?.Headers.ContentType?.CharSet));
            }
            if (gate is not null)
            {
                await gate.ConfigureAwait(false);
            }
            if (fail is not null)
            {
                throw fail();
            }
            return new HttpResponseMessage(status);
        }
    }

    /// <summary>Install a probe as the alerting transport and hand it back.</summary>
    protected ProbeHandler Probe(
        HttpStatusCode status = HttpStatusCode.OK, Func<Exception>? fail = null, Task? gate = null)
    {
        var handler = new ProbeHandler(status, fail, gate);
        var client = new HttpClient(handler);
        _created.Add(client);
        Alerting.HttpClient = client;
        return handler;
    }

    /// <summary>
    /// Set/clear ALERT_WEBHOOK_URL. Callers that want "configured but blank" must
    /// pass whitespace, not "": Windows DELETES a variable set to the empty
    /// string while POSIX keeps it, so "" tests two different things per OS.
    /// </summary>
    protected static void SetWebhook(string? url) =>
        Environment.SetEnvironmentVariable(Alerting.WebhookUrlEnvVar, url);

    /// <summary>Set/clear ALERT_DEADLETTER_THRESHOLD (same ""-vs-Windows caveat).</summary>
    protected static void SetThreshold(string? raw) =>
        Environment.SetEnvironmentVariable(Alerting.DeadLetterThresholdEnvVar, raw);

    /// <summary>Self-referencing graph — System.Text.Json throws JsonException on it.</summary>
    protected sealed class CyclicNode
    {
        public CyclicNode? Self { get; set; }
    }

    /// <summary>A getter that throws a type System.Text.Json does not wrap.</summary>
    protected sealed class ExplodingGetter
    {
        public string Boom => throw new InvalidTimeZoneException("property getter blew up");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BuildEnvelope — the wire contract every webhook receiver in the fleet parses.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AlertingEnvelopeTests : AlertingGlobalStateGuard
{
    private static List<string> KeysInOrder(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    [Fact]
    public void EnvelopeHasExactlyTheFiveKnownKeysInAFixedOrder()
    {
        // The full envelope: no more keys than this (a receiver validating a
        // strict schema rejects surprises) and in a stable order (receiver
        // golden-file tests and any body-signing scheme compare raw bytes).
        // NOTE the order — kind, message, timestamp, connector, data. The XML doc
        // on RaiseAsync advertises "{kind, message, connector?, timestamp, data?}",
        // i.e. connector BEFORE timestamp; the code inserts it after. Consumers
        // must key by name, never by position.
        Alerting.ConnectorId = "salesforce";

        var keys = KeysInOrder(Alerting.BuildEnvelope("crawl_failed", "boom", new { attempt = 3 }));

        Assert.Equal(new[] { "kind", "message", "timestamp", "connector", "data" }, keys);
    }

    [Fact]
    public void KindAndMessageAreCarriedVerbatim()
    {
        Alerting.ConnectorId = null;

        using var doc = JsonDocument.Parse(Alerting.BuildEnvelope("dead_letter", "12 items stuck", null));

        Assert.Equal("dead_letter", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("12 items stuck", doc.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ConnectorIsOmittedWhenUnsetOrEmpty(string? connectorId)
    {
        // Wave 2 sets ConnectorId at startup; before that (and in any host that
        // never sets it) the key must be absent rather than present-and-empty —
        // a receiver routing on `connector` should see nothing, not "".
        Alerting.ConnectorId = connectorId;

        Assert.DoesNotContain("connector", KeysInOrder(Alerting.BuildEnvelope("k", "m", null)));
    }

    [Fact]
    public void ConnectorSurvivesAsWhitespaceBecauseTheGuardIsIsNullOrEmpty()
    {
        // Documents actual behaviour, not an endorsement: the connector guard is
        // IsNullOrEmpty while the webhook-URL guard is IsNullOrWhiteSpace, so a
        // ConnectorId of " " is stamped into the envelope. Reported as a defect;
        // if it is ever tightened to IsNullOrWhiteSpace this test is the one that
        // has to change, deliberately.
        Alerting.ConnectorId = "   ";

        using var doc = JsonDocument.Parse(Alerting.BuildEnvelope("k", "m", null));

        Assert.Equal("   ", doc.RootElement.GetProperty("connector").GetString());
    }

    [Fact]
    public void DataIsOmittedOnlyWhenItIsNull()
    {
        // The Python original this fleet was ported from would have written
        // `if data:` here, which drops an empty dict, an empty string, 0 and
        // False. The C# guard is `data != null`, so "the caller passed something
        // empty" and "the caller passed nothing" stay distinguishable on the wire.
        Alerting.ConnectorId = null;

        Assert.DoesNotContain("data", KeysInOrder(Alerting.BuildEnvelope("k", "m", null)));

        using var empty = JsonDocument.Parse(
            Alerting.BuildEnvelope("k", "m", new Dictionary<string, object?>()));
        Assert.Equal(JsonValueKind.Object, empty.RootElement.GetProperty("data").ValueKind);
        Assert.Empty(empty.RootElement.GetProperty("data").EnumerateObject());

        using var falsy = JsonDocument.Parse(Alerting.BuildEnvelope("k", "m", false));
        Assert.Equal(JsonValueKind.False, falsy.RootElement.GetProperty("data").ValueKind);

        using var zero = JsonDocument.Parse(Alerting.BuildEnvelope("k", "m", 0));
        Assert.Equal(0, zero.RootElement.GetProperty("data").GetInt32());
    }

    [Fact]
    public void TimestampIsAStringThatRoundTripsAsAUtcDateTimeOffset()
    {
        // Receivers sort and window alerts on this field. It must be an ISO-8601
        // string (not a number, not a local time) with an explicit +00:00 offset,
        // and it must survive ParseExact("O") losslessly — the "o" format is what
        // makes the sub-second precision round-trip.
        var before = DateTimeOffset.UtcNow;
        using var doc = JsonDocument.Parse(Alerting.BuildEnvelope("k", "m", null));
        var after = DateTimeOffset.UtcNow;

        var stamp = doc.RootElement.GetProperty("timestamp");
        Assert.Equal(JsonValueKind.String, stamp.ValueKind);
        var raw = stamp.GetString()!;

        Assert.True(
            DateTimeOffset.TryParseExact(
                raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed),
            $"timestamp '{raw}' is not round-trippable ISO-8601");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(raw, parsed.ToString("o", CultureInfo.InvariantCulture));
        Assert.InRange(parsed, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public void EnvelopeIsOneLineAndLeavesOperatorTextUnescaped()
    {
        // UnsafeRelaxedJsonEscaping + WriteIndented=false are deliberate: alerts
        // are read by humans in a chat webhook and are often shipped as one
        // JSON-per-line record. Dropping the encoder turns "<" and every
        // accented or emoji character into numeric escapes; turning on
        // indentation breaks line-delimited ingestion. Both stay valid JSON, so
        // only the raw text catches the regression.
        Alerting.ConnectorId = null;
        const string Message = "HTTP 500 <error> & \"café\" ☕";

        var json = Alerting.BuildEnvelope("crawl_failed", Message, null);

        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
        Assert.Contains("<error> &", json, StringComparison.Ordinal);
        Assert.Contains("café", json, StringComparison.Ordinal);
        Assert.Contains("☕", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u00E9", json, StringComparison.OrdinalIgnoreCase);
        // The quotes inside the message ARE still escaped — relaxed escaping
        // relaxes HTML/non-ASCII, never JSON's own structural characters.
        Assert.Contains("\\\"café\\\"", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(Message, doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void BuildEnvelopeItselfThrowsOnUnserialisableData()
    {
        // The internal helper does NOT swallow — containment lives in RaiseAsync
        // (see AlertingRaiseTests). Pinned so nobody "helpfully" adds a catch here
        // that returns null/"" and turns a serialisation bug into a silent empty
        // POST body that a receiver then has to reject.
        var cycle = new CyclicNode();
        cycle.Self = cycle;

        Assert.Throws<JsonException>(() => Alerting.BuildEnvelope("k", "m", cycle));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RaiseAsync — disabled by default, and non-throwing under every failure.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AlertingRaiseTests : AlertingGlobalStateGuard
{
    private const string Hook = "https://example.test/hook";

    [Fact]
    public async Task RaiseAsyncIsAStrictNoOpWhenTheWebhookUrlIsUnset()
    {
        // Unset is the default for most deployments, so this is the hot path.
        // Nothing may be sent AND the transport must not even be constructed —
        // if the env check ever moves below the HttpClient access, every
        // alerting-free process starts paying for a proxy/CA-bundle handler
        // (and, on a bad transport config, logging a warning) for nothing.
        SetWebhook(null);
        var factoryCalls = 0;
        Alerting.HandlerFactory = () => { factoryCalls++; return new ProbeHandler(); };
        Alerting.HttpClient = null!;   // force lazy reconstruction on next access

        var task = Alerting.RaiseAsync("crawl_failed", "must not be sent");

        Assert.True(task.IsCompletedSuccessfully, "the disabled path must complete synchronously");
        await task;
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task RaiseAsyncIsANoOpWhenTheWebhookUrlIsBlank()
    {
        // A blank value in a unit file / Task Scheduler action means "not
        // configured", not "POST to the empty URL".
        SetWebhook("   ");
        var probe = Probe();

        await Alerting.RaiseAsync("crawl_failed", "must not be sent");

        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task RaiseAsyncPostsTheEnvelopeAsUtf8Json()
    {
        SetWebhook(Hook);
        Alerting.ConnectorId = "clarizen";
        var probe = Probe();

        await Alerting.RaiseAsync(
            "crawl_failed",
            "Ingestion run failed",
            new Dictionary<string, object?> { ["objectType"] = "Account", ["attempt"] = 3 });

        Assert.Equal(1, probe.Calls);
        var sent = probe.Last;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Hook, sent.Uri);
        // Receivers dispatch on Content-Type; charset must be utf-8 or non-ASCII
        // alert text is mojibake at the far end.
        Assert.Equal("application/json", sent.MediaType);
        Assert.Equal("utf-8", sent.Charset);

        using var doc = JsonDocument.Parse(sent.Body!);
        Assert.Equal("crawl_failed", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Ingestion run failed", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("clarizen", doc.RootElement.GetProperty("connector").GetString());
        Assert.Equal("Account", doc.RootElement.GetProperty("data").GetProperty("objectType").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("data").GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task RaiseAsyncRereadsTheWebhookUrlOnEveryCall()
    {
        // The URL is read per call, not cached in a static. A long-running
        // service can be repointed (or have alerting switched off) by restarting
        // with different config in the same process image — and caching it would
        // also make every test in this file order-dependent.
        var probe = Probe();

        SetWebhook("https://first.test/hook");
        await Alerting.RaiseAsync("k", "one");
        SetWebhook("https://second.test/hook");
        await Alerting.RaiseAsync("k", "two");
        SetWebhook(null);
        await Alerting.RaiseAsync("k", "three");

        Assert.Equal(
            new[] { "https://first.test/hook", "https://second.test/hook" },
            probe.Seen.Select(r => r.Uri).ToArray());
    }

    [Fact]
    public async Task RaiseAsyncToleratesWhitespacePaddingAroundTheConfiguredUrl()
    {
        // Env values picked up from a unit file or a pasted Task Scheduler
        // argument routinely carry stray spaces. Documents that padding is
        // harmless (Uri parsing trims it) — the alert still reaches the hook.
        SetWebhook("  " + Hook + "  ");
        var probe = Probe();

        await Alerting.RaiseAsync("k", "padded url");

        Assert.Equal(1, probe.Calls);
        Assert.Equal(Hook, probe.Last.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RaiseAsyncSwallowsEveryNon2xx(HttpStatusCode status)
    {
        // A rotated webhook token (401), a decommissioned hook (404) or a
        // throttling receiver (429) must cost the crawl a log line, nothing more.
        SetWebhook(Hook);
        var probe = Probe(status);

        await Alerting.RaiseAsync("crawl_failed", "receiver said no");

        Assert.Equal(1, probe.Calls);   // attempted, and the failure was absorbed
    }

    [Fact]
    public async Task RaiseAsyncSwallowsEveryTransportException()
    {
        // The catch is `catch (Exception)` and must stay that way. Narrowing it
        // to HttpRequestException would let a proxy TLS failure
        // (AuthenticationException), a client-timeout (TaskCanceledException) or
        // a disposed/misused client (ObjectDisposedException) escape into a crawl
        // that was only trying to report a problem.
        SetWebhook(Hook);
        var failures = new Func<Exception>[]
        {
            () => new HttpRequestException("connection refused"),
            () => new TaskCanceledException("the request timed out"),         // HttpClient.Timeout
            () => new OperationCanceledException("cancelled"),
            () => new IOException("connection reset by peer"),
            () => new ObjectDisposedException(nameof(HttpClient)),
            () => new System.Security.Authentication.AuthenticationException("TLS handshake failed"),
            () => new InvalidOperationException("misconfigured handler"),
        };

        foreach (var failure in failures)
        {
            var probe = Probe(fail: failure);
            var thrown = await Record.ExceptionAsync(() => Alerting.RaiseAsync("crawl_failed", "boom"));
            Assert.True(
                thrown is null,
                $"RaiseAsync leaked {thrown?.GetType().Name} from a {failure().GetType().Name} transport failure");
            Assert.Equal(1, probe.Calls);
        }
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("example.test/hook")]           // scheme-less: not an absolute URI
    [InlineData("ht!tp://bad scheme/hook")]
    public async Task RaiseAsyncSwallowsAnUnusableWebhookUrl(string url)
    {
        // A typo in ALERT_WEBHOOK_URL throws from PostAsync BEFORE any network
        // work. That throw is inside the try, so a misconfigured operator setting
        // degrades to a log line instead of killing every crawl that alerts.
        SetWebhook(url);
        var probe = Probe();

        var thrown = await Record.ExceptionAsync(() => Alerting.RaiseAsync("crawl_failed", "bad url"));

        Assert.Null(thrown);
        Assert.Equal(0, probe.Calls);   // never reached the transport
    }

    [Fact]
    public async Task RaiseAsyncSwallowsUnserialisableDataAndSendsNothing()
    {
        // Serialisation failure is caught separately from the POST, before any
        // request is made: a caller that hands alerting a cyclic graph or a
        // property that throws gets a logged error and a dropped alert, not an
        // exception and not a half-built body on the wire. The exploding getter
        // is the interesting half — System.Text.Json rethrows the getter's own
        // exception type (InvalidTimeZoneException here), so a `catch
        // (JsonException)` would not hold.
        SetWebhook(Hook);
        var cycle = new CyclicNode();
        cycle.Self = cycle;

        foreach (object data in new object[] { cycle, new ExplodingGetter() })
        {
            var probe = Probe();
            var thrown = await Record.ExceptionAsync(() => Alerting.RaiseAsync("crawl_failed", "boom", data));
            Assert.True(thrown is null, $"RaiseAsync leaked {thrown?.GetType().Name} for {data.GetType().Name}");
            Assert.Equal(0, probe.Calls);
        }
    }

    [Fact]
    public async Task ConcurrentAlertsAllDeliverAndNoneThrow()
    {
        // Alerts are raised from whatever crawl task noticed the problem, so
        // several can be in flight at once. All must be delivered and none may
        // surface an exception — including through the lazily-initialised
        // HttpClient, which is where a concurrency regression would land.
        SetWebhook(Hook);
        var probe = Probe();

        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(i => Alerting.RaiseAsync("crawl_failed", $"alert {i}")));

        Assert.Equal(32, probe.Calls);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MaybeAlertDeadLetterAsync — the threshold gate and its off-by-one.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AlertingDeadLetterTests : AlertingGlobalStateGuard
{
    private const string Hook = "https://example.test/hook";

    [Theory]
    // Unset / blank → feature off, whatever the depth.
    [InlineData(null, 1_000_000, false)]
    [InlineData("   ", 1_000_000, false)]
    // The boundary: the doc says "exceeds", and equality does NOT fire.
    [InlineData("10", 9, false)]
    [InlineData("10", 10, false)]
    [InlineData("10", 11, true)]
    [InlineData("1", 1, false)]
    [InlineData("1", 2, true)]
    // Non-positive thresholds disable the check rather than alerting on everything.
    [InlineData("0", int.MaxValue, false)]
    [InlineData("-1", int.MaxValue, false)]
    // Unparseable values disable it too — silently, which is the reported defect.
    [InlineData("abc", 1_000_000, false)]
    [InlineData("5.5", 1_000_000, false)]
    [InlineData("1,000", 1_000_000, false)]
    [InlineData("1e3", 1_000_000, false)]
    [InlineData("2147483648", 1_000_000, false)]   // int overflow
    // NumberStyles.Integer does accept surrounding whitespace and a leading sign.
    [InlineData(" 5 ", 6, true)]
    [InlineData("+5", 6, true)]
    // A depth at or below zero can never exceed a positive threshold.
    [InlineData("5", 0, false)]
    [InlineData("5", -1, false)]
    public async Task ThresholdDecidesWhetherAnAlertIsRaised(string? threshold, int depth, bool shouldFire)
    {
        SetWebhook(Hook);
        SetThreshold(threshold);
        var probe = Probe();

        await Alerting.MaybeAlertDeadLetterAsync("c1", depth);

        Assert.Equal(shouldFire ? 1 : 0, probe.Calls);
    }

    [Fact]
    public void DisabledThresholdReturnsAnAlreadyCompletedTaskWithoutTouchingTheTransport()
    {
        // The disabled path is taken on every crawl summary in every deployment
        // that has not opted in. It must not allocate an async state machine, and
        // — like RaiseAsync's disabled path — must not build a transport.
        SetWebhook(Hook);
        SetThreshold(null);
        var factoryCalls = 0;
        Alerting.HandlerFactory = () => { factoryCalls++; return new ProbeHandler(); };
        Alerting.HttpClient = null!;

        var task = Alerting.MaybeAlertDeadLetterAsync("c1", depth: 999);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task NoAlertWhenTheThresholdIsExceededButNoWebhookIsConfigured()
    {
        // The two gates are independent: opting into dead-letter alerting without
        // a webhook must stay silent rather than throw or busy-work.
        SetWebhook(null);
        SetThreshold("1");
        var probe = Probe();

        var thrown = await Record.ExceptionAsync(() => Alerting.MaybeAlertDeadLetterAsync("c1", depth: 500));

        Assert.Null(thrown);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task DeadLetterEnvelopeCarriesConnectorDepthAndThreshold()
    {
        // The receiver pages on `kind` and sizes the incident from data.depth /
        // data.threshold, so all three keys are contract. Note the two different
        // connector fields: the envelope's comes from Alerting.ConnectorId (the
        // process identity) and data.connector from the call argument (the queue
        // that overflowed). They are set differently here on purpose — collapsing
        // them would silently change what a receiver's routing rules see.
        SetWebhook(Hook);
        SetThreshold("5");
        Alerting.ConnectorId = "process-identity";
        var probe = Probe();

        await Alerting.MaybeAlertDeadLetterAsync("queue-owner", depth: 6);

        Assert.Equal(1, probe.Calls);
        using var doc = JsonDocument.Parse(probe.Last.Body!);
        var root = doc.RootElement;
        Assert.Equal("dead_letter", root.GetProperty("kind").GetString());
        Assert.Equal("process-identity", root.GetProperty("connector").GetString());
        var data = root.GetProperty("data");
        Assert.Equal("queue-owner", data.GetProperty("connector").GetString());
        Assert.Equal(6, data.GetProperty("depth").GetInt32());
        Assert.Equal(5, data.GetProperty("threshold").GetInt32());
        // The human line has to name both numbers — it is what lands in the chat
        // channel, where nobody expands the JSON.
        var message = root.GetProperty("message").GetString()!;
        Assert.Contains("6", message, StringComparison.Ordinal);
        Assert.Contains("5", message, StringComparison.Ordinal);
        Assert.Contains("queue-owner", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReturnedTaskCompletesOnlyAfterTheWebhookPostFinishes()
    {
        // A one-shot run exits immediately after the crawl summary. If this task
        // completed before delivery, the process would tear the request down
        // mid-flight and the operator would never hear about the dead letters.
        SetWebhook(Hook);
        SetThreshold("1");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = Probe(gate: gate.Task);

        var task = Alerting.MaybeAlertDeadLetterAsync("c1", depth: 2);

        Assert.False(task.IsCompleted, "returned before the POST could possibly have completed");
        gate.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, probe.Calls);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HandlerFactory — one alerting implementation over four connector transports.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AlertingTransportFactoryTests : AlertingGlobalStateGuard
{
    [Fact]
    public void TheHostFactoryBuildsTheTransportAndIsCalledOnce()
    {
        // The seam's whole purpose: Salesforce/Altrata/Hadoop keep the transport
        // they were hardened with. "Once" matters as much as "used" — a factory
        // invoked per alert would build a fresh handler (and its connection pool)
        // for every webhook POST.
        var calls = 0;
        Alerting.HandlerFactory = () => { calls++; return new ProbeHandler(); };
        Alerting.HttpClient = null!;

        var first = Alerting.HttpClient;
        var second = Alerting.HttpClient;

        Assert.Equal(1, calls);
        Assert.Same(first, second);
    }

    [Fact]
    public void TheAlertingClientTimesOutInFiveSeconds()
    {
        // Not the 100s HttpClient default: MaybeAlertDeadLetterAsync is awaited on
        // the crawl's exit path, so an unresponsive webhook receiver would
        // otherwise hold a finished run open for a minute and a half.
        Alerting.HandlerFactory = () => new ProbeHandler();
        Alerting.HttpClient = null!;

        Assert.Equal(TimeSpan.FromSeconds(5), Alerting.HttpClient.Timeout);
    }

    [Fact]
    public void AFactoryThatThrowsDegradesToAWorkingClient()
    {
        // Alerting is the messenger; it must never be the thing that kills the
        // process. Without the guard this exception escapes from a STATIC
        // property getter — i.e. TypeInitializationException at an arbitrary
        // point mid-crawl, nowhere near the misconfiguration that caused it.
        var calls = 0;
        Alerting.HandlerFactory = () => { calls++; throw new InvalidOperationException("bad CA bundle"); };
        Alerting.HttpClient = null!;

        var client = Alerting.HttpClient;

        Assert.NotNull(client);
        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
        // The degraded client is cached like any other, so the broken factory is
        // not re-run on every alert (and, by the same token, repairing
        // HandlerFactory later has no effect until HttpClient is reset).
        Assert.Same(client, Alerting.HttpClient);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AFactoryThatReturnsNullDegradesToo()
    {
        // A host wiring `() => _handler` where _handler is still null at startup
        // is the realistic version of this. Same outcome: a usable client.
        Alerting.HandlerFactory = () => null!;
        Alerting.HttpClient = null!;

        Assert.NotNull(Alerting.HttpClient);
        Assert.Equal(TimeSpan.FromSeconds(5), Alerting.HttpClient.Timeout);
    }

    [Fact]
    public async Task AlertingKeepsWorkingAfterATransportDegrade()
    {
        // A degrade must not latch the module off. The fallback client itself
        // cannot be exercised here — it has a real handler and a POST through it
        // would leave the process — so this asserts the next thing that matters:
        // once the fallback has been taken, RaiseAsync still walks the whole path
        // to whatever transport is current, rather than short-circuiting on some
        // "alerting is broken" state left behind by the failure.
        Alerting.HandlerFactory = () => throw new InvalidOperationException("bad proxy");
        Alerting.HttpClient = null!;
        Assert.NotNull(Alerting.HttpClient);   // take the fallback path

        SetWebhook("https://example.test/hook");
        var probe = Probe();
        await Alerting.RaiseAsync("crawl_failed", "after degrade");

        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void TheDefaultFactoryIsTheChassisTransportAndABadProxyDegradesRatherThanThrows()
    {
        // With no host factory the chassis uses HttpTransport.CreateHandler, which
        // reads PROXY_URL and throws ConfigException on a bad value — proving both
        // that the default path really is HttpTransport, and that an operator
        // typo in PROXY_URL cannot take alerting (or the process) down with it.
        Alerting.HandlerFactory = null;
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, "not-a-proxy-url");
        Assert.Throws<ConfigException>(() => HttpTransport.CreateHandler());

        Alerting.HttpClient = null!;

        var client = Alerting.HttpClient;
        Assert.NotNull(client);
        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
    }
}
