// Security regression coverage for the Seismic webhook receiver: HMAC
// validate-before-act, fail-closed startup, body-size cap. Drives the receiver
// over real loopback HTTP. (Fix for the "unauthenticated webhook" review finding.)

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

public class WebhookReceiverTests
{
    private const string Secret = "s3cr3t-shared-key";

    // ── SignatureValidator ─────────────────────────────────────────────────────

    [Fact]
    public void Signature_ValidHex_Accepted()
    {
        var v = new SignatureValidator(Secret);
        var body = Encoding.UTF8.GetBytes("""{"type":"contentPublished","contentId":"c1"}""");
        Assert.True(v.IsValid(body, v.ComputeHex(body)));
    }

    [Fact]
    public void Signature_Sha256Prefix_Accepted()
    {
        var v = new SignatureValidator(Secret);
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.True(v.IsValid(body, "sha256=" + v.ComputeHex(body)));
    }

    [Fact]
    public void Signature_WrongSecret_Rejected()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var sig = new SignatureValidator("other-secret").ComputeHex(body);
        Assert.False(new SignatureValidator(Secret).IsValid(body, sig));
    }

    [Fact]
    public void Signature_TamperedBody_Rejected()
    {
        var v = new SignatureValidator(Secret);
        var sig = v.ComputeHex(Encoding.UTF8.GetBytes("original"));
        Assert.False(v.IsValid(Encoding.UTF8.GetBytes("tampered"), sig));
    }

    [Fact]
    public void Signature_MissingOrGarbage_Rejected()
    {
        var v = new SignatureValidator(Secret);
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.False(v.IsValid(body, null));
        Assert.False(v.IsValid(body, ""));
        Assert.False(v.IsValid(body, "not-a-signature"));
    }

    [Fact]
    public void Signature_EmptySecret_Throws() =>
        Assert.Throws<ArgumentException>(() => new SignatureValidator(""));

    // ── Fail-closed startup ────────────────────────────────────────────────────

    [Fact]
    public void StartIfConfigured_PortSetWithoutSecret_ReturnsNull_FailClosed()
    {
        Assert.Null(WebhookReceiver.StartIfConfigured(FreePort(), secret: null));
        Assert.Null(WebhookReceiver.StartIfConfigured(FreePort(), secret: "   "));
    }

    [Fact]
    public void StartIfConfigured_PortZero_ReturnsNull()
    {
        Assert.Null(WebhookReceiver.StartIfConfigured(0, secret: Secret));
    }

    // ── End-to-end over loopback ───────────────────────────────────────────────

    [Fact]
    public async Task ValidSignedPost_Accepted_AndEnqueued()
    {
        var port = FreePort();
        using var receiver = WebhookReceiver.StartIfConfigured(port, Secret);
        Assert.NotNull(receiver);
        var body = """{"type":"contentDeleted","contentId":"c1"}""";

        var resp = await PostAsync(port, body, Sign(body));

        Assert.Equal(HttpStatusCode.Accepted, resp);
        var drained = receiver!.DrainEvents();
        Assert.Single(drained);
        Assert.Equal("c1", drained[0].ContentId);
    }

    [Fact]
    public async Task InvalidSignature_Rejected401_NothingEnqueued()
    {
        var port = FreePort();
        using var receiver = WebhookReceiver.StartIfConfigured(port, Secret);
        var body = """{"type":"contentDeleted","contentId":"c1"}""";

        var resp = await PostAsync(port, body, "sha256=deadbeef");

        Assert.Equal(HttpStatusCode.Unauthorized, resp);
        Assert.Empty(receiver!.DrainEvents());   // the critical invariant: no action on unverified input
    }

    [Fact]
    public async Task MissingSignature_Rejected401()
    {
        var port = FreePort();
        using var receiver = WebhookReceiver.StartIfConfigured(port, Secret);
        var body = """{"type":"contentDeleted","contentId":"c1"}""";

        var resp = await PostAsync(port, body, signature: null);

        Assert.Equal(HttpStatusCode.Unauthorized, resp);
        Assert.Empty(receiver!.DrainEvents());
    }

    [Fact]
    public async Task OversizeBody_Rejected_NothingEnqueued()
    {
        var port = FreePort();
        using var receiver = WebhookReceiver.StartIfConfigured(port, Secret);
        var body = new string('a', WebhookReceiver.MaxBodyBytes + 1);

        // The server rejects an over-large body without draining it, so the client
        // may observe a clean 413 or a connection reset — both are valid rejections.
        HttpStatusCode? status = null;
        try { status = await PostAsync(port, body, Sign(body)); }
        catch (HttpRequestException) { /* reset on oversize upload — also a rejection */ }

        if (status is not null)
            Assert.NotEqual(HttpStatusCode.Accepted, status);
        Assert.Empty(receiver!.DrainEvents());   // the invariant: oversize input is never acted on
    }

    [Fact]
    public async Task WrongMethodOrPath_NotFound()
    {
        var port = FreePort();
        using var receiver = WebhookReceiver.StartIfConfigured(port, Secret);
        using var http = new HttpClient();
        var get = await http.GetAsync($"http://localhost:{port}/webhook");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string Sign(string body) =>
        new SignatureValidator(Secret).ComputeHex(Encoding.UTF8.GetBytes(body));

    private static async Task<HttpStatusCode> PostAsync(int port, string body, string? signature)
    {
        using var http = new HttpClient();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (signature is not null)
            content.Headers.Add(SignatureValidator.DefaultHeader, signature);
        var resp = await http.PostAsync($"http://localhost:{port}/webhook", content);
        return resp.StatusCode;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
