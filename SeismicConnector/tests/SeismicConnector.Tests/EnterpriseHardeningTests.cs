// Enterprise hardening package — offline coverage:
//   * EventLogSink (Windows Event Log mirror) via the injected writer seam
//   * HttpTransport (PROXY_URL / PROXY_BYPASS / CA_BUNDLE_PATH additive trust)
//     with in-test self-signed certificates
//   * CertificateCredential (Graph client_assertion RS256 JWT; cert wins over
//     secret; fail-fast on misconfiguration)
//   * Dead-letter payload protection (DEADLETTER_PAYLOAD_MODE=redacted) and
//     the retry-failed re-fetch contract
//   * New webhook/HA metrics on the Prometheus surface
// No test touches the network.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── EventLogSink ─────────────────────────────────────────────────────────────

public sealed class FakeEventLogWriter : IEventLogWriter
{
    public List<(string Message, EventLogEntryKind Kind, int EventId)> Entries { get; } = new();

    public bool Throw { get; set; }

    public void WriteEntry(string message, EventLogEntryKind kind, int eventId)
    {
        if (Throw)
            throw new InvalidOperationException("event log unavailable");
        Entries.Add((message, kind, eventId));
    }
}

public class EventLogSinkTests : IDisposable
{
    private readonly FakeEventLogWriter _writer = new();

    public EventLogSinkTests()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, null);
        Environment.SetEnvironmentVariable(EventLogSink.LevelEnvVar, null);
        EventLogSink.OverrideWriter = _writer;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, null);
        Environment.SetEnvironmentVariable(EventLogSink.LevelEnvVar, null);
        EventLogSink.OverrideWriter = null;
    }

    private static LogRecord Record(int level, string message, Exception? ex = null) =>
        new() { Name = "seismic_connector.test", Level = level, Message = message, Exception = ex };

    [Fact]
    public void DisabledByDefault_CreateReturnsNull()
    {
        Assert.Null(EventLogSink.CreateIfConfigured());
    }

    [Fact]
    public void Enabled_OffWindows_WithoutInjectedWriter_IsNoOp()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        EventLogSink.OverrideWriter = null;
        var sink = EventLogSink.CreateIfConfigured();
        if (OperatingSystem.IsWindows())
            Assert.NotNull(sink);   // real writer on the deployment platform
        else
            Assert.Null(sink);      // silent no-op everywhere else
    }

    [Fact]
    public void Error_MirroredAsErrorEntry_WithStableEventId()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        var sink = EventLogSink.CreateIfConfigured()!;
        sink.Handle(Record(LogLevels.Error, "boom"));

        var entry = Assert.Single(_writer.Entries);
        Assert.Equal(EventLogEntryKind.Error, entry.Kind);
        Assert.Equal(EventLogSink.ErrorEventId, entry.EventId);
        Assert.Contains("boom", entry.Message);
        Assert.Contains("seismic_connector.test", entry.Message);  // logger name attributable
    }

    [Fact]
    public void Warning_MirroredAsWarningEntry()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        var sink = EventLogSink.CreateIfConfigured()!;
        sink.Handle(Record(LogLevels.Warning, "heads up"));

        var entry = Assert.Single(_writer.Entries);
        Assert.Equal(EventLogEntryKind.Warning, entry.Kind);
        Assert.Equal(EventLogSink.WarningEventId, entry.EventId);
    }

    [Fact]
    public void Info_FilteredAtDefaultLevel_MirroredWithEventLogLevelInfo()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        var sink = EventLogSink.CreateIfConfigured()!;
        sink.Handle(Record(LogLevels.Info, "routine"));
        Assert.Empty(_writer.Entries);  // default: WARNING+

        Environment.SetEnvironmentVariable(EventLogSink.LevelEnvVar, "info");
        var verbose = EventLogSink.CreateIfConfigured()!;
        verbose.Handle(Record(LogLevels.Info, "routine"));
        var entry = Assert.Single(_writer.Entries);
        Assert.Equal(EventLogEntryKind.Information, entry.Kind);
        Assert.Equal(EventLogSink.InfoEventId, entry.EventId);
    }

    [Fact]
    public void ExceptionDetails_IncludedInMirroredMessage()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        var sink = EventLogSink.CreateIfConfigured()!;
        sink.Handle(Record(LogLevels.Error, "crawl failed", new TimeoutException("graph timed out")));

        Assert.Contains("graph timed out", Assert.Single(_writer.Entries).Message);
    }

    [Fact]
    public void ThrowingWriter_NeverThrowsIntoLoggingPipeline()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        _writer.Throw = true;
        var sink = EventLogSink.CreateIfConfigured()!;

        var exception = Xunit.Record.Exception(() => sink.Handle(Record(LogLevels.Error, "boom")));

        Assert.Null(exception);
        Assert.Equal(1, sink.WriteFailures);
    }

    [Fact]
    public void OversizeMessage_TruncatedUnderEventLogCap()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        var sink = EventLogSink.CreateIfConfigured()!;
        sink.Handle(Record(LogLevels.Error, new string('x', EventLogSink.MaxMessageChars + 5_000)));

        var entry = Assert.Single(_writer.Entries);
        Assert.True(entry.Message.Length <= EventLogSink.MaxMessageChars + 20);
        Assert.EndsWith("[truncated]", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_AlwaysInformation_WithLifecycleEventId_EvenAtDefaultLevel()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        EventLogSink.Lifecycle("Service started");

        var entry = Assert.Single(_writer.Entries);
        Assert.Equal(EventLogEntryKind.Information, entry.Kind);
        Assert.Equal(EventLogSink.LifecycleEventId, entry.EventId);
    }

    [Fact]
    public void Lifecycle_Disabled_WritesNothing()
    {
        EventLogSink.Lifecycle("Service started");
        Assert.Empty(_writer.Entries);
    }

    [Fact]
    public void InstallOnRoot_AttachesHandler_WhenEnabled()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        var sink = EventLogSink.InstallOnRoot();
        try
        {
            Assert.NotNull(sink);
            Assert.Contains(sink!, Logging.Root.Handlers);
        }
        finally
        {
            if (sink is not null)
                Logging.Root.RemoveHandler(sink);
        }
    }
}

// ── HttpTransport (proxy + custom CA) ────────────────────────────────────────

public class HttpTransportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "seismic-transport-" + Guid.NewGuid().ToString("N"));

    public HttpTransportTests()
    {
        Directory.CreateDirectory(_tempDir);
        ClearEnv();
    }

    public void Dispose()
    {
        ClearEnv();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static void ClearEnv()
    {
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, null);
        Environment.SetEnvironmentVariable(HttpTransport.ProxyBypassEnvVar, null);
        Environment.SetEnvironmentVariable(HttpTransport.CaBundleEnvVar, null);
    }

    internal static X509Certificate2 SelfSigned(string cn, bool ca = false)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(ca, false, 0, critical: true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    // ── proxy ────────────────────────────────────────────────────────────────

    [Fact]
    public void NoEnv_MeansDirect_NoProxy_NoCustomTrust()
    {
        Assert.Null(HttpTransport.BuildProxy());
        Assert.Null(HttpTransport.LoadCustomRoots());
        using var handler = HttpTransport.CreateHandler();
        Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Null(((SocketsHttpHandler)handler).Proxy);
    }

    [Fact]
    public void ProxyUrl_BuildsWebProxy_AndHandlerUsesIt()
    {
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, "http://proxy.contoso.com:8080");
        var proxy = Assert.IsType<WebProxy>(HttpTransport.BuildProxy());
        Assert.Equal(new Uri("http://proxy.contoso.com:8080"), proxy.Address);

        using var handler = (SocketsHttpHandler)HttpTransport.CreateHandler();
        Assert.NotNull(handler.Proxy);
        Assert.True(handler.UseProxy);
    }

    [Fact]
    public void ProxyBypass_WildcardsSkipTheProxy()
    {
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, "http://proxy.contoso.com:8080");
        Environment.SetEnvironmentVariable(HttpTransport.ProxyBypassEnvVar, "*.contoso.local; 10.0.0.*");
        var proxy = (WebProxy)HttpTransport.BuildProxy()!;

        Assert.True(proxy.IsBypassed(new Uri("https://sql.contoso.local")));
        Assert.True(proxy.IsBypassed(new Uri("http://10.0.0.15")));
        Assert.False(proxy.IsBypassed(new Uri("https://api.seismic.com")));
    }

    [Fact]
    public void InvalidProxyUrl_FailsFast_NamingTheSetting()
    {
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, "not a url");
        var ex = Assert.Throws<ConfigException>(() => HttpTransport.BuildProxy());
        Assert.Contains(HttpTransport.ProxyUrlEnvVar, ex.Message);
    }

    // ── CA bundle ────────────────────────────────────────────────────────────

    [Fact]
    public void CaBundle_MissingFile_FailsFast_NamingTheSetting()
    {
        Environment.SetEnvironmentVariable(
            HttpTransport.CaBundleEnvVar, Path.Combine(_tempDir, "missing.pem"));
        var ex = Assert.Throws<ConfigException>(() => HttpTransport.LoadCustomRoots());
        Assert.Contains(HttpTransport.CaBundleEnvVar, ex.Message);
    }

    [Fact]
    public void CaBundle_GarbagePem_FailsFast_NamingTheSetting()
    {
        var path = Path.Combine(_tempDir, "garbage.pem");
        File.WriteAllText(path, "this is not a certificate");
        Environment.SetEnvironmentVariable(HttpTransport.CaBundleEnvVar, path);
        var ex = Assert.Throws<ConfigException>(() => HttpTransport.LoadCustomRoots());
        Assert.Contains(HttpTransport.CaBundleEnvVar, ex.Message);
    }

    [Fact]
    public void CaBundle_ValidPem_LoadsAllCertificates()
    {
        using var rootA = SelfSigned("Contoso Inspection CA", ca: true);
        using var rootB = SelfSigned("Contoso Private Root", ca: true);
        var path = Path.Combine(_tempDir, "bundle.pem");
        File.WriteAllText(path, rootA.ExportCertificatePem() + "\n" + rootB.ExportCertificatePem());
        Environment.SetEnvironmentVariable(HttpTransport.CaBundleEnvVar, path);

        var roots = HttpTransport.LoadCustomRoots();
        Assert.NotNull(roots);
        Assert.Equal(2, roots!.Count);
    }

    [Fact]
    public void AdditiveTrust_SystemTrustedChain_PassesUntouched()
    {
        using var anyRoot = SelfSigned("irrelevant", ca: true);
        var roots = new X509Certificate2Collection { anyRoot };
        Assert.True(HttpTransport.ValidateWithAdditiveTrust(
            null, null, SslPolicyErrors.None, roots));
    }

    [Fact]
    public void AdditiveTrust_ChainToCustomRoot_Accepted()
    {
        using var custom = SelfSigned("Contoso TLS Inspection", ca: true);
        var roots = new X509Certificate2Collection { custom };
        // The inspection appliance presents its own (custom-rooted) certificate:
        // system trust fails with chain errors, the custom bundle accepts it.
        Assert.True(HttpTransport.ValidateWithAdditiveTrust(
            custom, null, SslPolicyErrors.RemoteCertificateChainErrors, roots));
    }

    [Fact]
    public void AdditiveTrust_LeafIssuedByCustomRoot_Accepted()
    {
        using var caRsa = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=Contoso Issuing CA", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var caCert = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var leafRsa = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            "CN=api.seismic.com", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var leaf = leafRequest.Create(
            caCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(14),
            new byte[] { 1, 2, 3, 4 });

        var roots = new X509Certificate2Collection { caCert };
        Assert.True(HttpTransport.ValidateWithAdditiveTrust(
            leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, roots));
    }

    [Fact]
    public void AdditiveTrust_UntrustedIssuer_Rejected()
    {
        using var trusted = SelfSigned("Trusted Root", ca: true);
        using var rogue = SelfSigned("Rogue Cert", ca: true);
        var roots = new X509Certificate2Collection { trusted };
        Assert.False(HttpTransport.ValidateWithAdditiveTrust(
            rogue, null, SslPolicyErrors.RemoteCertificateChainErrors, roots));
    }

    [Fact]
    public void AdditiveTrust_HostnameMismatch_NeverExcused()
    {
        using var custom = SelfSigned("Contoso TLS Inspection", ca: true);
        var roots = new X509Certificate2Collection { custom };
        Assert.False(HttpTransport.ValidateWithAdditiveTrust(
            custom, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            roots));
        Assert.False(HttpTransport.ValidateWithAdditiveTrust(
            null, null, SslPolicyErrors.RemoteCertificateNotAvailable, roots));
    }
}

// ── CertificateCredential (Graph client_assertion) ───────────────────────────

public class CertificateCredentialTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "seismic-cert-" + Guid.NewGuid().ToString("N"));

    public CertificateCredentialTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static GraphSettings Settings(
        string? certPath = null, string? certPassword = null, string? thumbprint = null) => new()
    {
        TenantId = "tenant-guid",
        ClientId = "app-client-id",
        ClientSecret = "the-secret",
        ClientCertPath = certPath,
        ClientCertPassword = certPassword,
        ClientCertThumbprint = thumbprint,
        MaxRetries = 2,
        RetryBackoffBase = 1,
    };

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    [Fact]
    public void Assertion_IsWellFormedRs256Jwt_WithCertificateBinding()
    {
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        var jwt = CertificateCredential.BuildAssertion(
            cert, "app-client-id", "https://login.microsoftonline.com/t/oauth2/v2.0/token");

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonNode.Parse(Encoding.UTF8.GetString(FromBase64Url(parts[0])))!.AsObject();
        Assert.Equal("RS256", header["alg"]!.GetValue<string>());
        Assert.Equal("JWT", header["typ"]!.GetValue<string>());
        Assert.Equal(
            CertificateCredential.Base64Url(SHA256.HashData(cert.RawData)),
            header["x5t#S256"]!.GetValue<string>());

        var claims = JsonNode.Parse(Encoding.UTF8.GetString(FromBase64Url(parts[1])))!.AsObject();
        Assert.Equal("https://login.microsoftonline.com/t/oauth2/v2.0/token", claims["aud"]!.GetValue<string>());
        Assert.Equal("app-client-id", claims["iss"]!.GetValue<string>());
        Assert.Equal("app-client-id", claims["sub"]!.GetValue<string>());
        Assert.True(Guid.TryParse(claims["jti"]!.GetValue<string>(), out _));
        var nbf = claims["nbf"]!.GetValue<long>();
        var exp = claims["exp"]!.GetValue<long>();
        Assert.True(exp > nbf, "assertion must expire after nbf");
        Assert.True(exp - nbf <= 11 * 60, "assertion lifetime must stay within platform limits");
    }

    [Fact]
    public void Assertion_SignatureVerifies_WithCertificatePublicKey()
    {
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        var jwt = CertificateCredential.BuildAssertion(cert, "app", "https://aud/token");
        var parts = jwt.Split('.');

        using var rsa = cert.GetRSAPublicKey()!;
        var verified = rsa.VerifyData(
            Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]),
            FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        Assert.True(verified);
    }

    [Fact]
    public void Assertion_FreshJtiPerCall()
    {
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        static string Jti(string jwt) =>
            JsonNode.Parse(Encoding.UTF8.GetString(FromBase64Url(jwt.Split('.')[1])))!["jti"]!
                .GetValue<string>();
        var first = CertificateCredential.BuildAssertion(cert, "app", "https://aud");
        var second = CertificateCredential.BuildAssertion(cert, "app", "https://aud");
        Assert.NotEqual(Jti(first), Jti(second));
    }

    [Fact]
    public void Load_NotConfigured_ReturnsNull()
    {
        Assert.Null(CertificateCredential.Load(Settings()));
        Assert.False(CertificateCredential.IsConfigured(Settings()));
    }

    [Fact]
    public void Load_Pfx_WithPassword_CarriesPrivateKey()
    {
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        var path = Path.Combine(_tempDir, "auth.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "pfx-pass"));

        using var loaded = CertificateCredential.Load(Settings(certPath: path, certPassword: "pfx-pass"))!;
        Assert.True(loaded.HasPrivateKey);
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        Assert.True(CertificateCredential.IsConfigured(Settings(certPath: path)));
    }

    [Fact]
    public void Load_PemWithKey_CarriesPrivateKey()
    {
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        using var rsa = cert.GetRSAPrivateKey()!;
        var path = Path.Combine(_tempDir, "auth.pem");
        File.WriteAllText(path, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());

        using var loaded = CertificateCredential.Load(Settings(certPath: path))!;
        Assert.True(loaded.HasPrivateKey);
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
    }

    [Fact]
    public void Load_MissingFile_FailsFast_NamingTheSetting()
    {
        var ex = Assert.Throws<ConfigException>(
            () => CertificateCredential.Load(Settings(certPath: Path.Combine(_tempDir, "nope.pfx"))));
        Assert.Contains(CertificateCredential.CertPathEnvVar, ex.Message);
    }

    [Fact]
    public void Load_UnknownThumbprint_FailsFast_NamingTheSetting()
    {
        var ex = Assert.Throws<ConfigException>(
            () => CertificateCredential.Load(Settings(thumbprint: new string('0', 40))));
        Assert.Contains(CertificateCredential.CertThumbprintEnvVar, ex.Message);
    }

    // ── GraphClient integration: certificate wins over secret ────────────────

    [Fact]
    public void TokenForm_SecretMode_UsesClientSecret()
    {
        var client = new GraphClient(Settings(), new FakeHttpHandler());
        var form = client.BuildTokenForm("https://login/t/oauth2/v2.0/token");

        Assert.Equal("the-secret", form["client_secret"]);
        Assert.False(form.ContainsKey("client_assertion"));
        Assert.Equal("client_secret", client.AuthMode);
    }

    [Fact]
    public void TokenForm_CertificateConfigured_WinsOverSecret()
    {
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        var path = Path.Combine(_tempDir, "auth.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, (string?)null));

        var client = new GraphClient(Settings(certPath: path), new FakeHttpHandler());
        var form = client.BuildTokenForm("https://login/t/oauth2/v2.0/token");

        Assert.False(form.ContainsKey("client_secret"));
        Assert.Equal(CertificateCredential.AssertionType, form["client_assertion_type"]);
        Assert.StartsWith("eyJ", form["client_assertion"], StringComparison.Ordinal);
        Assert.Equal("certificate", client.AuthMode);
    }

    [Fact]
    public async Task TokenRequest_OverTheWire_CarriesAssertion_NeverTheSecret()
    {
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        var path = Path.Combine(_tempDir, "auth.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, (string?)null));

        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Post, "/oauth2/v2.0/token", (_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"tok-1","expires_in":3600}"""));
        handler.When(HttpMethod.Get, "/external/connections/", (_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));

        var client = new GraphClient(Settings(certPath: path), handler);
        await client.GetAsync("/external/connections/probe");

        var tokenRequest = handler.Requests.Single(r => r.Url.Contains("/oauth2/v2.0/token"));
        Assert.Contains("client_assertion=", tokenRequest.Body);
        Assert.Contains("client_assertion_type=", tokenRequest.Body);
        Assert.DoesNotContain("client_secret", tokenRequest.Body);
        Assert.DoesNotContain("the-secret", tokenRequest.Body);
    }
}

// ── Dead-letter payload protection ───────────────────────────────────────────

public class DeadLetterRedactionTests : IDisposable
{
    public DeadLetterRedactionTests() =>
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);

    private static JsonObject ExternalItemBody() => new()
    {
        ["id"] = "c1",
        ["acl"] = new JsonArray(
            new JsonObject { ["type"] = "user", ["value"] = "entra-user-1", ["accessType"] = "grant" }),
        ["properties"] = new JsonObject
        {
            ["title"] = "FY26 pricing deck",
            ["teamsiteId"] = "ts1",
            ["versionId"] = "v7",
            ["views"] = 42,
            ["title@odata.type"] = "String",
        },
        ["content"] = new JsonObject { ["value"] = "TOP SECRET CONTENT BODY", ["type"] = "text" },
    };

    [Fact]
    public void FullMode_Explicit_StoresPayloadVerbatim()
    {
        // Full mode is now OPT-IN (the shipped default is redacted); set it
        // explicitly to exercise the verbatim round-trip.
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, "full");
        using var state = new TempStateDir();
        SyncState.AppendFailedRecords(
            SyncState.FailedRecordsPath("RedactTest"),
            new[] { ("c1", "HTTP 503") },
            "ContentItem",
            requestBodies: new Dictionary<string, JsonNode?> { ["c1"] = ExternalItemBody() });

        var record = Assert.Single(SyncState.ReadFailedRecords("RedactTest"));
        Assert.Equal(
            "TOP SECRET CONTENT BODY",
            record["request_body"]!["content"]!["value"]!.GetValue<string>());
        Assert.Equal("FY26 pricing deck", record["request_body"]!["properties"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void DefaultMode_IsRedacted_NoRawValuesOnDisk()
    {
        // NEW shipped default: with DEADLETTER_PAYLOAD_MODE unset, payloads are
        // redacted — no raw indexed content / property values ever hit disk.
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("RedactTest");
        SyncState.AppendFailedRecords(
            path,
            new[] { ("c1", "HTTP 503") },
            "ContentItem",
            requestBodies: new Dictionary<string, JsonNode?> { ["c1"] = ExternalItemBody() });

        // Nothing sensitive survives verbatim anywhere in the raw file.
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("TOP SECRET CONTENT BODY", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("FY26 pricing deck", raw, StringComparison.Ordinal);

        var record = Assert.Single(SyncState.ReadFailedRecords("RedactTest"));
        Assert.StartsWith(
            "[redacted sha256=",
            record["request_body"]!["content"]!["value"]!.GetValue<string>(),
            StringComparison.Ordinal);
        // Retry inputs + identifiers still survive redaction.
        Assert.Equal("c1", record["item_id"]!.GetValue<string>());
        Assert.Equal("ts1", record["request_body"]!["properties"]!["teamsiteId"]!.GetValue<string>());
    }

    [Fact]
    public void RedactedMode_StripsContentAndPropertyValues_KeepsIdsVersionErrorAcl()
    {
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, "redacted");
        using var state = new TempStateDir();
        SyncState.AppendFailedRecords(
            SyncState.FailedRecordsPath("RedactTest"),
            new[] { ("c1", "HTTP 503: service unavailable") },
            "ContentItem",
            requestBodies: new Dictionary<string, JsonNode?> { ["c1"] = ExternalItemBody() },
            responseBodies: new Dictionary<string, JsonNode?>
            {
                ["c1"] = new JsonObject
                {
                    ["error"] = new JsonObject { ["code"] = "serviceNotAvailable", ["message"] = "try later" },
                },
            });

        var record = Assert.Single(SyncState.ReadFailedRecords("RedactTest"));
        var request = record["request_body"]!.AsObject();

        // Stripped: indexed content and free-text property values (hash stub kept).
        var content = request["content"]!["value"]!.GetValue<string>();
        Assert.StartsWith("[redacted sha256=", content, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP SECRET", content, StringComparison.Ordinal);
        Assert.StartsWith(
            "[redacted sha256=",
            request["properties"]!["title"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.True(request["properties"]!["views"] is null);  // numeric value stripped

        // Kept: identifiers, version, ACL, error/attempt metadata, odata annotations.
        Assert.Equal("c1", request["id"]!.GetValue<string>());
        Assert.Equal("ts1", request["properties"]!["teamsiteId"]!.GetValue<string>());
        Assert.Equal("v7", request["properties"]!["versionId"]!.GetValue<string>());
        Assert.Equal("String", request["properties"]!["title@odata.type"]!.GetValue<string>());
        Assert.Equal("entra-user-1", request["acl"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("HTTP 503: service unavailable", record["error"]!.GetValue<string>());
        Assert.Equal(
            "serviceNotAvailable",
            record["response_body"]!["error"]!["code"]!.GetValue<string>());

        // The retry contract's inputs survive redaction untouched.
        Assert.Equal("c1", record["item_id"]!.GetValue<string>());
        Assert.Equal("ContentItem", record["object_type"]!.GetValue<string>());
    }

    [Fact]
    public void RedactionStub_IsDeterministic_SoPayloadsStayComparable()
    {
        Assert.Equal(SyncState.RedactionStub("same payload"), SyncState.RedactionStub("same payload"));
        Assert.NotEqual(SyncState.RedactionStub("payload a"), SyncState.RedactionStub("payload b"));
    }

    [Fact]
    public void Redaction_NeverMutatesTheCallersPayload()
    {
        var original = ExternalItemBody();
        SyncState.RedactDeadLetterPayload(original);
        Assert.Equal("TOP SECRET CONTENT BODY", original["content"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task RedactedMode_RetryRefetchesFullContentFromSource()
    {
        // retry-failed replays only item_id/object_type; the payload is
        // re-fetched from Seismic. A redacted dead-letter record must therefore
        // still produce a FULL re-ingest.
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, "redacted");
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));

        SyncState.AppendFailedRecords(
            SyncState.FailedRecordsPath(harness.Config.Connector.Id),
            new[] { ("c1", "HTTP 503") },
            "ContentItem",
            requestBodies: new Dictionary<string, JsonNode?> { ["c1"] = ExternalItemBody() });
        var record = Assert.Single(SyncState.ReadFailedRecords(harness.Config.Connector.Id));

        // What CmdRetryFailed does with a ContentItem record: IngestSingleAsync(item_id).
        var ok = await harness.Pipeline.IngestSingleAsync(
            record["item_id"]!.GetValue<string>(), "ts1");

        Assert.True(ok);
        var put = harness.LastPutBody("c1");
        Assert.NotNull(put);
        var contentValue = put!["content"]!["value"]!.GetValue<string>();
        Assert.Contains("payload of c1", contentValue, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted", contentValue, StringComparison.Ordinal);
    }
}

// ── MSI authoring (packaging/msi) ────────────────────────────────────────────

public class MsiPackagingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("SeismicConnector.sln").Length == 0)
            dir = dir.Parent;
        Assert.True(dir != null,
            "could not locate repo root (SeismicConnector.sln) above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static string WxsPath =>
        Path.Combine(RepoRoot(), "packaging", "msi", "SeismicConnector.wxs");

    [Fact]
    public void Wxs_IsWellFormedXml_InTheWixV4V5Namespace()
    {
        var doc = System.Xml.Linq.XDocument.Load(WxsPath);  // throws on malformed XML
        Assert.Equal("Wix", doc.Root!.Name.LocalName);
        Assert.Equal("http://wixtoolset.org/schemas/v4/wxs", doc.Root.Name.NamespaceName);
    }

    [Fact]
    public void Wxs_RegistersTheService_ButNeverStartsIt_AndPinsUpgradeCode()
    {
        var doc = System.Xml.Linq.XDocument.Load(WxsPath);
        System.Xml.Linq.XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";

        var package = doc.Root!.Element(ns + "Package")!;
        Assert.False(string.IsNullOrEmpty(package.Attribute("UpgradeCode")?.Value));
        Assert.NotNull(package.Element(ns + "MajorUpgrade"));  // clean in-place upgrades

        var serviceInstall = doc.Descendants(ns + "ServiceInstall").Single();
        Assert.Equal("SeismicConnector", serviceInstall.Attribute("Name")!.Value);

        // The installer must NOT start the service (config does not exist yet):
        // ServiceControl carries no Start attribute.
        var serviceControl = doc.Descendants(ns + "ServiceControl").Single();
        Assert.Null(serviceControl.Attribute("Start"));
        Assert.Equal("uninstall", serviceControl.Attribute("Remove")!.Value);

        // Every shipped repo file referenced via RepoRoot must actually exist.
        foreach (var file in doc.Descendants(ns + "File"))
        {
            var source = file.Attribute("Source")!.Value;
            if (!source.Contains("$(var.RepoRoot)", StringComparison.Ordinal))
                continue;
            var relative = source.Replace("$(var.RepoRoot)/", "", StringComparison.Ordinal);
            var full = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"MSI references a missing repo file: {relative}");
        }
    }
}

// ── New metrics on the Prometheus surface ────────────────────────────────────

public class HardeningMetricsTests
{
    [Fact]
    public void WebhookAndHaMetrics_RenderWithActualNames()
    {
        Metrics.ResetForTests();
        try
        {
            Metrics.IncWebhookAccepted(3);
            Metrics.IncWebhookRejected(2);
            Metrics.IncWebhookDropped(5);
            Metrics.SetWebhookQueueDepth(7);
            Metrics.IncHaClaimsAcquired(4);
            Metrics.AddHaClaimsHeld(2);

            var text = Metrics.RenderPrometheus();
            Assert.Contains("seismic_connector_webhook_accepted_total 3", text);
            Assert.Contains("seismic_connector_webhook_rejected_total 2", text);
            Assert.Contains("seismic_connector_webhook_dropped_total 5", text);
            Assert.Contains("seismic_connector_webhook_queue_depth 7", text);
            Assert.Contains("seismic_connector_ha_claims_acquired_total 4", text);
            Assert.Contains("seismic_connector_ha_claims_held 2", text);
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }

    [Fact]
    public void HaClaimsHeld_NeverReportsNegative()
    {
        Metrics.ResetForTests();
        try
        {
            Metrics.AddHaClaimsHeld(-3);  // completing claims stolen elsewhere
            Assert.Equal(0, Metrics.HaClaimsHeld);
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }
}
