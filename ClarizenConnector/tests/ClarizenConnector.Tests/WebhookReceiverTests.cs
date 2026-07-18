// WebhookReceiver end-to-end over real loopback HTTP: signature acceptance/
// rejection, malformed rejection, method/path handling, the fail-closed
// missing-secret guard, the disabled toggle, and metrics.

using System.Net;
using System.Text;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;
using ClarizenConnector.Webhook;

namespace ClarizenConnector.Tests;

public class WebhookReceiverTests : IDisposable
{
    private const string Secret = "webhook-shared-secret";
    private const string Body =
        """{"events":[{"entityType":"Task","id":"/Task/1","operation":"update"}]}""";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly WebhookProcessor _processor;

    public WebhookReceiverTests()
    {
        Metrics.ResetForTests();
        _store = new IdentityStore("WebhookRecv", Path.Combine(_dir.Path, "identity.db"));
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task",
                    SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
                },
            },
        };
        var config = TestConfig.Make();
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var clarizen = new ClarizenClient(config, new ApiBudget(1_000_000), handler);
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var pipeline = new IngestPipeline(
            config, schema, clarizen, new FakeGraphClient(config), resolver, new ItemConverter(config),
            ha: null,
            inventoryFactory: id => new ItemInventory(id, Path.Combine(_dir.Path, $"inv_{id}.db")));
        _processor = new WebhookProcessor(schema, clarizen, pipeline, TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        _store.Dispose();
        _stateScope.Dispose();
        _dir.Dispose();
        Metrics.ResetForTests();
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static EnvScope Enabled(int port, string? secret = Secret, string? requireTimestamp = null) => new(
        (WebhookReceiver.PortEnvVar, port.ToString()),
        (WebhookReceiver.SecretEnvVar, secret),
        (WebhookReceiver.PathEnvVar, null),
        (WebhookReceiver.HeaderEnvVar, null),
        (WebhookAuthenticator.RequireTimestampEnvVar, requireTimestamp),
        (WebhookAuthenticator.ToleranceEnvVar, null),
        (WebhookAuthenticator.TimestampHeaderEnvVar, null),
        ("USE_KEY_VAULT", null));

    /// <summary>Unix-seconds timestamp, optionally offset (negative = in the past).</summary>
    private static string Ts(int offsetSeconds = 0) =>
        DateTimeOffset.UtcNow.AddSeconds(offsetSeconds).ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>HMAC over the anti-replay canonical message "{ts}.{body}".</summary>
    private static string SignedWithTs(string timestamp, string body) =>
        new SignatureValidator(Secret).ComputeHex(timestamp, Encoding.UTF8.GetBytes(body));

    private static async Task<HttpResponseMessage> PostAsync(
        int port, string body, string? signature, string? timestamp = null,
        string path = "/webhook", string method = "POST")
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"http://localhost:{port}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
            request.Headers.TryAddWithoutValidation(SignatureValidator.DefaultHeader, signature);
        if (timestamp is not null)
            request.Headers.TryAddWithoutValidation(WebhookAuthenticator.DefaultTimestampHeader, timestamp);
        return await client.SendAsync(request);
    }

    [Fact]
    public void Disabled_WhenPortUnset()
    {
        using var scope = new EnvScope((WebhookReceiver.PortEnvVar, null));
        Assert.Null(WebhookReceiver.StartIfConfigured(_processor));
    }

    [Fact]
    public void FailClosed_WhenSecretMissing()
    {
        var port = FreePort();
        using var scope = Enabled(port, secret: null);
        // Port set but no secret → must refuse to start (no unauthenticated endpoint).
        Assert.Null(WebhookReceiver.StartIfConfigured(_processor));
    }

    [Fact]
    public async Task ValidSignature_Accepted_Enqueued_Metrics()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);
        Assert.NotNull(receiver);

        var ts = Ts();
        var response = await PostAsync(port, Body, SignedWithTs(ts, Body), ts);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, _processor.Debouncer.PendingCount);
        Assert.Equal(1, Metrics.WebhookEventsReceived);
        Assert.Equal(1, Metrics.WebhookEventsAccepted);
        Assert.Equal(0, Metrics.WebhookEventsRejected);
        Assert.Equal(1, Metrics.WebhookReceiverUp);
    }

    [Fact]
    public async Task InvalidSignature_Rejected_NotEnqueued()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        // Fresh timestamp, but a signature that does not match it → bad signature.
        var response = await PostAsync(port, Body, signature: "deadbeef", timestamp: Ts());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _processor.Debouncer.PendingCount);
        Assert.Equal(1, Metrics.WebhookEventsRejected);
        Assert.Equal(0, Metrics.WebhookEventsAccepted);
    }

    [Fact]
    public async Task MissingSignature_Rejected()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        var response = await PostAsync(port, Body, signature: null, timestamp: Ts());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _processor.Debouncer.PendingCount);
    }

    [Fact]
    public async Task StaleTimestamp_Rejected_FailClosed()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        // 10 minutes old, correctly signed for that timestamp — outside the
        // default 5-minute window, so it must be rejected fail-closed.
        var ts = Ts(-600);
        var response = await PostAsync(port, Body, SignedWithTs(ts, Body), ts);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _processor.Debouncer.PendingCount);
        Assert.Equal(1, Metrics.WebhookEventsRejected);
    }

    [Fact]
    public async Task ReplayWithinWindow_Rejected()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        var ts = Ts();
        var sig = SignedWithTs(ts, Body);
        var first = await PostAsync(port, Body, sig, ts);
        var replay = await PostAsync(port, Body, sig, ts);   // byte-for-byte replay

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(1, _processor.Debouncer.PendingCount);   // only the first enqueued
        Assert.Equal(1, Metrics.WebhookEventsAccepted);
    }

    [Fact]
    public async Task MissingTimestamp_Rejected_WhenStrict()
    {
        var port = FreePort();
        using var scope = Enabled(port);   // require-timestamp defaults ON
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        // Legacy body-only signature, no timestamp header → rejected under strict.
        var bodyOnly = new SignatureValidator(Secret).ComputeHex(Encoding.UTF8.GetBytes(Body));
        var response = await PostAsync(port, Body, bodyOnly);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _processor.Debouncer.PendingCount);
    }

    [Fact]
    public async Task MissingTimestamp_Accepted_InMigrationMode()
    {
        var port = FreePort();
        using var scope = Enabled(port, requireTimestamp: "false");
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        // Migration: body-only signature with no timestamp is accepted (no replay
        // protection) so senders that cannot yet send a timestamp keep working.
        var bodyOnly = new SignatureValidator(Secret).ComputeHex(Encoding.UTF8.GetBytes(Body));
        var response = await PostAsync(port, Body, bodyOnly);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, _processor.Debouncer.PendingCount);
    }

    [Fact]
    public async Task MalformedPayload_ValidSig_Rejected400()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        const string garbage = "not even json";
        var ts = Ts();
        var sig = SignedWithTs(ts, garbage);
        var response = await PostAsync(port, garbage, sig, ts);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _processor.Debouncer.PendingCount);
        Assert.Equal(1, Metrics.WebhookEventsRejected);
    }

    [Fact]
    public async Task NonPost_405_And_WrongPath_404()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        var get = await PostAsync(port, Body, null, method: "GET");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);

        var wrongPath = await PostAsync(port, Body, null, path: "/nope");
        Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
    }

    [Fact]
    public async Task Sha256PrefixedSignature_Accepted()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);

        var ts = Ts();
        var sig = "sha256=" + SignedWithTs(ts, Body);
        var response = await PostAsync(port, Body, sig, ts);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public void Dispose_SetsReceiverDown()
    {
        var port = FreePort();
        using var scope = Enabled(port);
        var receiver = WebhookReceiver.StartIfConfigured(_processor);
        Assert.NotNull(receiver);
        Assert.Equal(1, Metrics.WebhookReceiverUp);
        receiver!.Dispose();
        Assert.Equal(0, Metrics.WebhookReceiverUp);
        receiver.Dispose();  // idempotent
    }

    [Fact]
    public void NormalizePath()
    {
        Assert.Equal("/webhook", WebhookReceiver.NormalizePath(null));
        Assert.Equal("/webhook", WebhookReceiver.NormalizePath(""));
        Assert.Equal("/hook", WebhookReceiver.NormalizePath("hook"));
        Assert.Equal("/clarizen/events", WebhookReceiver.NormalizePath("/clarizen/events"));
    }
}
