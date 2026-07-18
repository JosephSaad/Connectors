// Certificate credential for the Graph token flow (Graph/ClientAssertion.cs +
// GraphClient.BuildTokenRequest): RS256 client-assertion JWT structure
// (x5t#S256 header, aud/iss/sub/jti/nbf/exp claims, verifiable signature),
// certificate-wins-over-secret precedence, PFX/PEM file loading with fail-fast
// naming the setting, and mode-only logging. Fully offline — the certificates
// are generated in-test.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class ClientAssertionTests
{
    internal static X509Certificate2 MakeCert(string cn = "CN=graph-connector-test")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(cn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string TokenEndpoint =
        "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/token";

    [Fact]
    public void Build_ProducesThreePartJwt_WithRs256HeaderAndX5tS256()
    {
        using var cert = MakeCert();
        var jwt = ClientAssertion.Build(cert, ClientId, TokenEndpoint);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonNode.Parse(FromBase64Url(parts[0]))!.AsObject();
        Assert.Equal("RS256", header["alg"]!.GetValue<string>());
        Assert.Equal("JWT", header["typ"]!.GetValue<string>());
        // x5t#S256 = base64url(SHA-256 over the DER cert) — never the SHA-1 x5t.
        Assert.Equal(
            ClientAssertion.Base64Url(SHA256.HashData(cert.RawData)),
            header["x5t#S256"]!.GetValue<string>());
        Assert.False(header.ContainsKey("x5t"));
    }

    [Fact]
    public void Build_Claims_AudIssSubJtiNbfExp()
    {
        using var cert = MakeCert();
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var jwt = ClientAssertion.Build(cert, ClientId, TokenEndpoint, now, jti: "fixed-jti-1");

        var payload = JsonNode.Parse(FromBase64Url(jwt.Split('.')[1]))!.AsObject();
        Assert.Equal(TokenEndpoint, payload["aud"]!.GetValue<string>());
        Assert.Equal(ClientId, payload["iss"]!.GetValue<string>());
        Assert.Equal(ClientId, payload["sub"]!.GetValue<string>());
        Assert.Equal("fixed-jti-1", payload["jti"]!.GetValue<string>());
        Assert.Equal(now.ToUnixTimeSeconds(), payload["nbf"]!.GetValue<long>());
        Assert.Equal(now.AddMinutes(10).ToUnixTimeSeconds(), payload["exp"]!.GetValue<long>());
    }

    [Fact]
    public void Build_JtiIsUniquePerAssertion()
    {
        using var cert = MakeCert();
        string Jti(string jwt) =>
            JsonNode.Parse(FromBase64Url(jwt.Split('.')[1]))!["jti"]!.GetValue<string>();
        Assert.NotEqual(
            Jti(ClientAssertion.Build(cert, ClientId, TokenEndpoint)),
            Jti(ClientAssertion.Build(cert, ClientId, TokenEndpoint)));
    }

    [Fact]
    public void Build_SignatureVerifies_WithCertificatePublicKey()
    {
        using var cert = MakeCert();
        var jwt = ClientAssertion.Build(cert, ClientId, TokenEndpoint);
        var parts = jwt.Split('.');

        using var rsa = cert.GetRSAPublicKey()!;
        var verified = rsa.VerifyData(
            Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]),
            FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        Assert.True(verified);

        // A different key must NOT verify it (the signature is real, not decorative).
        using var otherCert = MakeCert("CN=other");
        using var otherKey = otherCert.GetRSAPublicKey()!;
        Assert.False(otherKey.VerifyData(
            Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]),
            FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void LoadFromFile_MissingPath_FailsFast_NamingSetting()
    {
        var exc = Assert.Throws<ArgumentException>(
            () => ClientAssertion.LoadFromFile("/nonexistent/cert.pfx", null));
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", exc.Message);
    }

    [Fact]
    public void LoadFromFile_Pfx_RoundTrips_WithPassword()
    {
        using var cert = MakeCert();
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "graph.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "pfx-pass"));

        using var loaded = ClientAssertion.LoadFromFile(path, "pfx-pass");
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);

        var wrong = Assert.Throws<ArgumentException>(
            () => ClientAssertion.LoadFromFile(path, "wrong-pass"));
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", wrong.Message);
        Assert.Contains("GRAPH_CLIENT_CERT_PASSWORD", wrong.Message);
    }

    [Fact]
    public void LoadFromFile_PemWithKey_Loads()
    {
        using var cert = MakeCert();
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "graph.pem");
        using var key = cert.GetRSAPrivateKey()!;
        File.WriteAllText(path,
            cert.ExportCertificatePem() + Environment.NewLine + key.ExportRSAPrivateKeyPem());

        using var loaded = ClientAssertion.LoadFromFile(path, null);
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void LoadConfigured_NeitherKnobSet_ReturnsNull()
    {
        Assert.Null(ClientAssertion.LoadConfigured(TestConfig.Make()));
    }
}

public class GraphClientCertificateFlowTests
{
    private static AppConfig WithCert(string certPath, string? password = null) => new()
    {
        ConnectorId = "BdhHadoopMart",
        ConnectorName = "BDH Hadoop Data Mart",
        ConnectorDescription = "Test connector",
        HdfsMode = "webhdfs",
        HdfsNamenodeUrl = "http://namenode.example:9870/webhdfs/v1",
        BdhRootPath = "/data/bdh",
        AadTenantId = "11111111-1111-1111-1111-111111111111",
        AadClientId = "22222222-2222-2222-2222-222222222222",
        AadClientSecret = "secret-that-must-lose",
        GraphClientCertPath = certPath,
        GraphClientCertPassword = password,
    };

    [Fact]
    public async Task GetToken_CertificateConfigured_SendsClientAssertion_NotSecret()
    {
        using var cert = ClientAssertionTests.MakeCert();
        using var dir = new TempDir();
        var certPath = Path.Combine(dir.Path, "graph.pfx");
        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pkcs12, "p"));

        string? capturedForm = null;
        var handler = new MockHttpHandler((_, body) =>
        {
            capturedForm = body;
            return MockHttpHandler.Json(
                System.Net.HttpStatusCode.OK, """{"access_token":"tok-cert","expires_in":3600}""");
        });
        var client = new GraphClient(WithCert(certPath, "p"), handler);

        var token = await client.GetTokenAsync();
        Assert.Equal("tok-cert", token);
        Assert.NotNull(capturedForm);
        Assert.Contains("client_assertion_type=", capturedForm);
        Assert.Contains(Uri.EscapeDataString(ClientAssertion.AssertionType), capturedForm);
        Assert.Contains("client_assertion=", capturedForm);
        // Certificate WINS: the configured secret must never be transmitted.
        Assert.DoesNotContain("client_secret", capturedForm);
        Assert.DoesNotContain("secret-that-must-lose", capturedForm);
    }

    [Fact]
    public void BuildTokenRequest_NoCert_UsesClientSecret_Unchanged()
    {
        var client = new GraphClient(TestConfig.Make(), new MockHttpHandler(
            (_, _) => MockHttpHandler.Json(System.Net.HttpStatusCode.OK, "{}")));
        var form = client.BuildTokenRequest();
        Assert.Equal("secret", form["client_secret"]);
        Assert.False(form.ContainsKey("client_assertion"));
        Assert.False(form.ContainsKey("client_assertion_type"));
    }

    [Fact]
    public void BuildTokenRequest_BadCertPath_FailsFast_NamingSetting()
    {
        var client = new GraphClient(WithCert("/nonexistent/graph.pfx"), new MockHttpHandler(
            (_, _) => MockHttpHandler.Json(System.Net.HttpStatusCode.OK, "{}")));
        var exc = Assert.Throws<ArgumentException>(() => client.BuildTokenRequest());
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", exc.Message);
    }

    [Fact]
    public void BuildTokenRequest_LogsAuthMode_Only_NoMaterial()
    {
        using var cert = ClientAssertionTests.MakeCert();
        using var dir = new TempDir();
        var certPath = Path.Combine(dir.Path, "graph.pfx");
        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pkcs12, "p"));

        var lines = new List<string>();
        Logging.TestSink = (_, _, message) => lines.Add(message);
        try
        {
            var client = new GraphClient(WithCert(certPath, "p"), new MockHttpHandler(
                (_, _) => MockHttpHandler.Json(System.Net.HttpStatusCode.OK, "{}")));
            var form = client.BuildTokenRequest();
            client.BuildTokenRequest();  // mode logged once, not per request

            Assert.Single(lines, l => l.Contains("Graph auth mode: certificate"));
            // No key material, assertion text or password in any log line.
            Assert.DoesNotContain(lines, l => l.Contains(form["client_assertion"]));
            Assert.DoesNotContain(lines, l => l.Contains("secret-that-must-lose"));
            Assert.DoesNotContain(lines, l => l.Contains("p\""));
        }
        finally
        {
            Logging.TestSink = null;
        }
    }

    [Fact]
    public void AppConfigLoad_CertPath_MakesSecretOptional_AndCloneCarriesIt()
    {
        using var env = new EnvScope(
            ("CONNECTOR_ID", "BdhHadoopMart"),
            ("HDFS_MODE", "webhdfs"),
            ("HDFS_NAMENODE_URL", "http://namenode.example:9870/webhdfs/v1"),
            ("AAD_APP_TENANT_ID", "t"),
            ("AAD_APP_CLIENT_ID", "c"),
            ("SECRET_AAD_APP_CLIENT_SECRET", null),
            ("GRAPH_CLIENT_CERT_PATH", "/etc/pki/graph.pfx"),
            ("GRAPH_CLIENT_CERT_PASSWORD", "pw"),
            ("GRAPH_CLIENT_CERT_THUMBPRINT", null),
            ("USE_KEY_VAULT", null));
        var config = AppConfig.Load();
        Assert.True(config.HasGraphClientCertificate);
        Assert.Equal(string.Empty, config.AadClientSecret);
        Assert.Equal("/etc/pki/graph.pfx", config.GraphClientCertPath);
        Assert.Equal("pw", config.GraphClientCertPassword);

        var clone = config.CloneForConnection("ShardA");
        Assert.Equal(config.GraphClientCertPath, clone.GraphClientCertPath);
        Assert.Equal(config.GraphClientCertPassword, clone.GraphClientCertPassword);
        Assert.Equal(config.GraphClientCertThumbprint, clone.GraphClientCertThumbprint);
    }

    [Fact]
    public void AppConfigLoad_NoCert_SecretStillRequired()
    {
        using var env = new EnvScope(
            ("CONNECTOR_ID", "BdhHadoopMart"),
            ("HDFS_MODE", "webhdfs"),
            ("HDFS_NAMENODE_URL", "http://namenode.example:9870/webhdfs/v1"),
            ("AAD_APP_TENANT_ID", "t"),
            ("AAD_APP_CLIENT_ID", "c"),
            ("SECRET_AAD_APP_CLIENT_SECRET", null),
            ("GRAPH_CLIENT_CERT_PATH", null),
            ("GRAPH_CLIENT_CERT_THUMBPRINT", null),
            ("USE_KEY_VAULT", null));
        var exc = Assert.Throws<ArgumentException>(() => AppConfig.Load());
        Assert.Contains("SECRET_AAD_APP_CLIENT_SECRET", exc.Message);
    }
}
