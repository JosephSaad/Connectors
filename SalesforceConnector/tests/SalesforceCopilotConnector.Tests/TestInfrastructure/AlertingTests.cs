// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for Infrastructure/Alerting.cs (#11): no-op without a webhook, correct
// JSON envelope shape when configured, dead-letter threshold logic, and the
// guarantee that a failing webhook never throws into the caller.

using System.Net;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

/// <summary>Fake handler capturing the outgoing request; returns a configurable status.</summary>
file sealed class CapturingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly bool _throw;

    public int Calls { get; private set; }
    public string? LastUri { get; private set; }
    public string? LastBody { get; private set; }
    public string? LastContentType { get; private set; }

    public CapturingHandler(HttpStatusCode status = HttpStatusCode.OK, bool @throw = false)
    {
        _status = status;
        _throw = @throw;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastUri = request.RequestUri?.ToString();
        LastContentType = request.Content?.Headers.ContentType?.MediaType;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        if (_throw)
        {
            throw new HttpRequestException("simulated network failure");
        }
        return new HttpResponseMessage(_status);
    }
}

/// <summary>
/// Touches ALERT_* env vars and the Alerting HttpClient/ConnectorId seams; joins
/// the "EnvVars" collection and restores everything.
/// </summary>
[Collection("EnvVars")]
public sealed class AlertingTests : IDisposable
{
    private readonly string? _savedWebhook;
    private readonly string? _savedThreshold;
    private readonly HttpClient _savedClient;
    private readonly string? _savedConnector;

    public AlertingTests()
    {
        _savedWebhook = Environment.GetEnvironmentVariable("ALERT_WEBHOOK_URL");
        _savedThreshold = Environment.GetEnvironmentVariable("ALERT_DEADLETTER_THRESHOLD");
        _savedClient = Alerting.HttpClient;
        _savedConnector = Alerting.ConnectorId;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", _savedWebhook);
        Environment.SetEnvironmentVariable("ALERT_DEADLETTER_THRESHOLD", _savedThreshold);
        Alerting.HttpClient = _savedClient;
        Alerting.ConnectorId = _savedConnector;
    }

    // ── No-op without a webhook ──────────────────────────────────────────────

    [Fact]
    public async Task RaiseAsyncNoOpsWithoutWebhook()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", null);
        var handler = new CapturingHandler();
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.RaiseAsync("crawl_failed", "should not send");

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RaiseAsyncNoOpsWithEmptyWebhook()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "   ");
        var handler = new CapturingHandler();
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.RaiseAsync("crawl_failed", "should not send");

        Assert.Equal(0, handler.Calls);
    }

    // ── Posts the right JSON shape ───────────────────────────────────────────

    [Fact]
    public async Task RaiseAsyncPostsEnvelopeWithExpectedShape()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "https://example.test/hook");
        Alerting.ConnectorId = "myConnector";
        var handler = new CapturingHandler();
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.RaiseAsync(
            "crawl_failed",
            "Ingestion run failed",
            new Dictionary<string, object?> { ["objectType"] = "Account", ["attempt"] = 3 });

        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://example.test/hook", handler.LastUri);
        Assert.Equal("application/json", handler.LastContentType);

        var obj = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("crawl_failed", obj["kind"]!.GetValue<string>());
        Assert.Equal("Ingestion run failed", obj["message"]!.GetValue<string>());
        Assert.Equal("myConnector", obj["connector"]!.GetValue<string>());
        Assert.True(obj.ContainsKey("timestamp"));
        Assert.False(string.IsNullOrEmpty(obj["timestamp"]!.GetValue<string>()));
        var data = obj["data"]!.AsObject();
        Assert.Equal("Account", data["objectType"]!.GetValue<string>());
        Assert.Equal(3, data["attempt"]!.GetValue<int>());
    }

    [Fact]
    public async Task RaiseAsyncOmitsConnectorWhenUnset()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "https://example.test/hook");
        Alerting.ConnectorId = null;
        var handler = new CapturingHandler();
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.RaiseAsync("noticed", "no connector configured");

        var obj = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.False(obj.ContainsKey("connector"));
        Assert.False(obj.ContainsKey("data"));  // data omitted when null
    }

    // ── Never throws on failure ──────────────────────────────────────────────

    [Fact]
    public async Task RaiseAsyncSwallowsNetworkFailure()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "https://example.test/hook");
        Alerting.HttpClient = new HttpClient(new CapturingHandler(@throw: true));

        // Must not throw.
        await Alerting.RaiseAsync("crawl_failed", "boom");
    }

    [Fact]
    public async Task RaiseAsyncSwallowsNon2xx()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "https://example.test/hook");
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.RaiseAsync("crawl_failed", "server said no");

        Assert.Equal(1, handler.Calls);  // attempted, non-2xx swallowed
    }

    // ── Dead-letter threshold ────────────────────────────────────────────────

    [Fact]
    public async Task MaybeAlertDeadLetterNoOpsWhenThresholdUnset()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "https://example.test/hook");
        Environment.SetEnvironmentVariable("ALERT_DEADLETTER_THRESHOLD", null);
        var handler = new CapturingHandler();
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.MaybeAlertDeadLetterAsync("c1", depth: 1000);

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MaybeAlertDeadLetterNoOpsWhenNotExceeded()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "https://example.test/hook");
        Environment.SetEnvironmentVariable("ALERT_DEADLETTER_THRESHOLD", "10");
        var handler = new CapturingHandler();
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.MaybeAlertDeadLetterAsync("c1", depth: 10);  // equal, not exceeded

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MaybeAlertDeadLetterFiresWhenExceeded()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", "https://example.test/hook");
        Environment.SetEnvironmentVariable("ALERT_DEADLETTER_THRESHOLD", "5");
        var tcs = new TaskCompletionSource();
        var handler = new FiringHandler(tcs);
        Alerting.HttpClient = new HttpClient(handler);

        // Awaited end-to-end: delivery must complete before the call returns, so a
        // one-shot run exiting right after the crawl cannot lose the alert.
        await Alerting.MaybeAlertDeadLetterAsync("c1", depth: 6);

        Assert.True(tcs.Task.IsCompleted, "dead-letter alert POST was not made");
        var obj = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("dead_letter", obj["kind"]!.GetValue<string>());
        var data = obj["data"]!.AsObject();
        Assert.Equal(6, data["depth"]!.GetValue<int>());
        Assert.Equal(5, data["threshold"]!.GetValue<int>());
        Assert.Equal("c1", data["connector"]!.GetValue<string>());
    }

    /// <summary>Handler that signals a TCS once it has captured the request body.</summary>
    private sealed class FiringHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _signal;
        public string? LastBody { get; private set; }

        public FiringHandler(TaskCompletionSource signal) => _signal = signal;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _signal.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void BuildEnvelopeIsValidJson()
    {
        Alerting.ConnectorId = "abc";
        var json = Alerting.BuildEnvelope("k", "m", new { x = 1 });
        var obj = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("k", obj["kind"]!.GetValue<string>());
        Assert.Equal("m", obj["message"]!.GetValue<string>());
        Assert.Equal("abc", obj["connector"]!.GetValue<string>());
    }
}
