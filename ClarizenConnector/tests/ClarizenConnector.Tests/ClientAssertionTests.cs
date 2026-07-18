// ClientAssertionTests.cs — certificate credential for Graph auth: offline
// JWT-shape assertions (RS256, x5t#S256, aud/jti/nbf/exp), PFX/PEM loading,
// fail-fast config errors, and the GraphClient token-request wiring (cert wins
// over secret; no client_secret on the wire in certificate mode).

using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Tests;

public class ClientAssertionTests
{
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string TokenEndpoint =
        "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/token";

    private static (JsonObject Header, JsonObject Payload, string SigningInput, byte[] Signature)
        Decode(string jwt)
    {
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var header = JsonNode.Parse(Base64Url.DecodeFromChars(parts[0]))!.AsObject();
        var payload = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1]))!.AsObject();
        return (header, payload, parts[0] + "." + parts[1], Base64Url.DecodeFromChars(parts[2]));
    }

    [Fact]
    public void Build_ProducesTheExpectedJwtShape()
    {
        using var cert = TestCertificates.SelfSigned();
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

        var jwt = ClientAssertion.Build(cert, ClientId, TokenEndpoint, now, jti: "fixed-jti");
        var (header, payload, _, _) = Decode(jwt);

        Assert.Equal("RS256", header["alg"]!.GetValue<string>());
        Assert.Equal("JWT", header["typ"]!.GetValue<string>());
        Assert.Equal(
            Base64Url.EncodeToString(SHA256.HashData(cert.RawData)),
            header["x5t#S256"]!.GetValue<string>());

        Assert.Equal(TokenEndpoint, payload["aud"]!.GetValue<string>());
        Assert.Equal(ClientId, payload["iss"]!.GetValue<string>());
        Assert.Equal(ClientId, payload["sub"]!.GetValue<string>());
        Assert.Equal("fixed-jti", payload["jti"]!.GetValue<string>());
        Assert.Equal(now.AddSeconds(-60).ToUnixTimeSeconds(), payload["nbf"]!.GetValue<long>());
        Assert.Equal(now.ToUnixTimeSeconds(), payload["iat"]!.GetValue<long>());
        Assert.Equal(now.AddMinutes(10).ToUnixTimeSeconds(), payload["exp"]!.GetValue<long>());
    }

    [Fact]
    public void Build_SignatureVerifiesWithTheCertificatePublicKey()
    {
        using var cert = TestCertificates.SelfSigned();
        var jwt = ClientAssertion.Build(cert, ClientId, TokenEndpoint);
        var (_, _, signingInput, signature) = Decode(jwt);

        using var rsa = cert.GetRSAPublicKey()!;
        Assert.True(rsa.VerifyData(
            Encoding.UTF8.GetBytes(signingInput), signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        // A tampered payload must not verify.
        Assert.False(rsa.VerifyData(
            Encoding.UTF8.GetBytes(signingInput + "x"), signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Build_FreshJtiPerAssertion()
    {
        using var cert = TestCertificates.SelfSigned();
        var first = Decode(ClientAssertion.Build(cert, ClientId, TokenEndpoint)).Payload;
        var second = Decode(ClientAssertion.Build(cert, ClientId, TokenEndpoint)).Payload;
        Assert.NotEqual(first["jti"]!.GetValue<string>(), second["jti"]!.GetValue<string>());
    }

    // ── Certificate loading ──────────────────────────────────────────────────

    [Fact]
    public void LoadFromPath_Pfx_RoundTripsWithPrivateKey()
    {
        using var dir = new TempDir();
        using var cert = TestCertificates.SelfSigned();
        var path = Path.Combine(dir.Path, "graph-client.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "pfx-pw"));

        using var loaded = ClientAssertion.LoadFromPath(path, "pfx-pw");
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void LoadFromPath_Pem_RoundTripsWithPrivateKey()
    {
        using var dir = new TempDir();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=pem-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var path = Path.Combine(dir.Path, "graph-client.pem");
        File.WriteAllText(path, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem() + "\n");

        using var loaded = ClientAssertion.LoadFromPath(path, password: null);
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void LoadFromPath_MissingOrCorrupt_FailsFastNamingTheSetting()
    {
        using var dir = new TempDir();
        var missing = Assert.Throws<ArgumentException>(
            () => ClientAssertion.LoadFromPath(Path.Combine(dir.Path, "absent.pfx"), null));
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", missing.Message);

        var garbagePath = Path.Combine(dir.Path, "garbage.pfx");
        File.WriteAllText(garbagePath, "not a certificate");
        var corrupt = Assert.Throws<ArgumentException>(
            () => ClientAssertion.LoadFromPath(garbagePath, null));
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", corrupt.Message);
    }

    [Fact]
    public void LoadFromStore_UnknownThumbprint_FailsFastNamingTheSetting()
    {
        // Off Windows: the platform error. On Windows: not-found for a
        // nonexistent thumbprint. Both name the setting.
        var exc = Assert.Throws<ArgumentException>(
            () => ClientAssertion.LoadFromStore("0000000000000000000000000000000000000000"));
        Assert.Contains("GRAPH_CLIENT_CERT_THUMBPRINT", exc.Message);
    }

    // ── GraphClient wiring ───────────────────────────────────────────────────

    private static AppConfig CertConfig(string certPath, string? secret = "unused-secret") => new()
    {
        ConnectorId = "ClarizenAdaptiveWork",
        ConnectorName = "Clarizen AdaptiveWork",
        ConnectorDescription = "Test connector",
        ClarizenBaseUrl = "https://api.clarizen.example/v2.0/services",
        ClarizenUsername = "svc@example.com",
        ClarizenPassword = "pw",
        AadTenantId = "11111111-1111-1111-1111-111111111111",
        AadClientId = ClientId,
        AadClientSecret = secret,
        GraphClientCertPath = certPath,
    };

    private static MockHttpHandler TokenCapturingHandler() => new((request, _) =>
        request.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal)
            ? MockHttpHandler.Json(
                HttpStatusCode.OK, """{"access_token": "tok", "expires_in": 3600}""")
            : MockHttpHandler.Json(HttpStatusCode.OK, "{}"));

    [Fact]
    public async Task GraphClient_CertificateConfigured_SendsAssertion_NeverTheSecret()
    {
        using var dir = new TempDir();
        using var cert = TestCertificates.SelfSigned();
        var path = Path.Combine(dir.Path, "graph-client.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));  // config carries no password

        var handler = TokenCapturingHandler();
        // Secret AND certificate configured — the certificate must win.
        var client = new GraphClient(CertConfig(path, secret: "unused-secret"), handler);

        var response = await client.GetAsync("external/connections/X");
        Assert.True(response.IsSuccess);

        var tokenRequest = handler.Requests.Single(r => r.Url.Contains("/oauth2/v2.0/token"));
        Assert.Contains("client_assertion_type=" + Uri.EscapeDataString(ClientAssertion.AssertionType),
            tokenRequest.Body);
        Assert.Contains("client_assertion=", tokenRequest.Body);
        Assert.DoesNotContain("client_secret", tokenRequest.Body);

        // The assertion on the wire is a valid RS256 JWT for OUR cert + endpoint.
        var assertion = Uri.UnescapeDataString(
            tokenRequest.Body.Split('&').Single(p => p.StartsWith("client_assertion=", StringComparison.Ordinal))
                ["client_assertion=".Length..]);
        var parts = assertion.Split('.');
        var payload = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1]))!.AsObject();
        Assert.Equal(client.TokenEndpoint, payload["aud"]!.GetValue<string>());
        using var rsa = cert.GetRSAPublicKey()!;
        Assert.True(rsa.VerifyData(
            Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task GraphClient_SecretOnly_StillUsesTheSecretFlow()
    {
        var handler = TokenCapturingHandler();
        var client = new GraphClient(TestConfig.Make(), handler);

        var response = await client.GetAsync("external/connections/X");
        Assert.True(response.IsSuccess);

        var tokenRequest = handler.Requests.Single(r => r.Url.Contains("/oauth2/v2.0/token"));
        Assert.Contains("client_secret=", tokenRequest.Body);
        Assert.DoesNotContain("client_assertion", tokenRequest.Body);
    }

    // ── AppConfig validation ─────────────────────────────────────────────────

    private static EnvScope BaseLoadEnv() => new(
        ("CONNECTOR_ID", "ClarizenAdaptiveWork"),
        ("CLARIZEN_USERNAME", "svc@example.com"),
        ("SECRET_CLARIZEN_PASSWORD", "pw"),
        ("AAD_APP_TENANT_ID", "t"),
        ("AAD_APP_CLIENT_ID", "c"),
        ("SECRET_AAD_APP_CLIENT_SECRET", null),
        ("GRAPH_CLIENT_CERT_PATH", null),
        ("GRAPH_CLIENT_CERT_THUMBPRINT", null),
        ("GRAPH_CLIENT_CERT_PASSWORD", null));

    [Fact]
    public void AppConfigLoad_NeitherSecretNorCertificate_FailsFast()
    {
        using var env = BaseLoadEnv();
        var exc = Assert.Throws<ArgumentException>(AppConfig.Load);
        Assert.Contains("SECRET_AAD_APP_CLIENT_SECRET", exc.Message);
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", exc.Message);
    }

    [Fact]
    public void AppConfigLoad_CertificateWithoutSecret_Succeeds()
    {
        using var dir = new TempDir();
        using var cert = TestCertificates.SelfSigned();
        var path = Path.Combine(dir.Path, "graph-client.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));

        using var env = BaseLoadEnv();
        env.Set("GRAPH_CLIENT_CERT_PATH", path);

        var config = AppConfig.Load();
        Assert.True(config.UseGraphCertificate);
        Assert.Equal(path, config.GraphClientCertPath);
        Assert.Null(config.AadClientSecret);
    }

    [Fact]
    public void AppConfigLoad_CertPathDoesNotExist_FailsFastNamingTheSetting()
    {
        using var env = BaseLoadEnv();
        env.Set("GRAPH_CLIENT_CERT_PATH", "/nope/graph-client.pfx");
        var exc = Assert.Throws<ArgumentException>(AppConfig.Load);
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", exc.Message);
        Assert.Contains("does not exist", exc.Message);
    }

    [Fact]
    public void AppConfigLoad_ThumbprintOffWindows_FailsFastWithGuidance()
    {
        if (OperatingSystem.IsWindows())
            return;  // the guard is for non-Windows hosts
        using var env = BaseLoadEnv();
        env.Set("GRAPH_CLIENT_CERT_THUMBPRINT", "AA00");
        var exc = Assert.Throws<ArgumentException>(AppConfig.Load);
        Assert.Contains("GRAPH_CLIENT_CERT_THUMBPRINT", exc.Message);
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", exc.Message);  // points at the alternative
    }

    [Fact]
    public void CloneForConnection_CarriesTheCertificateCredential()
    {
        var config = CertConfig("/some/path.pfx", secret: null);
        var clone = config.CloneForConnection("ShardA");
        Assert.Equal("/some/path.pfx", clone.GraphClientCertPath);
        Assert.True(clone.UseGraphCertificate);
        Assert.Null(clone.AadClientSecret);
    }
}
