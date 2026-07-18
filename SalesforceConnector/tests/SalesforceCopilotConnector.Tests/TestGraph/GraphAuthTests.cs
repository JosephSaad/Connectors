// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for Graph/GraphAuth.cs — certificate-credential selection and the
// client_assertion JWT (shape, claims, x5t#S256 binding, RS256 signature).
// All certificates are generated in-test; no token endpoint is contacted.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Identity;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Tests.TestInfrastructure;

namespace SalesforceCopilotConnector.Tests.TestGraph;

/// <summary>Touches GRAPH_CLIENT_CERT_* / AZURE_* env vars — "EnvVars" collection.</summary>
[Collection("EnvVars")]
public sealed class GraphAuthTests : IDisposable
{
    private readonly string _tmpPath = Directory.CreateTempSubdirectory("graphauth_").FullName;
    private readonly Dictionary<string, string?> _savedEnv = new();

    private static readonly string[] TouchedVars =
    {
        GraphAuth.CertPathEnvVar, GraphAuth.CertPasswordEnvVar, GraphAuth.CertThumbprintEnvVar,
        "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_AUTHORITY_HOST",
    };

    public GraphAuthTests()
    {
        foreach (var name in TouchedVars)
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
            Environment.SetEnvironmentVariable(name, value);
        try
        {
            Directory.Delete(_tmpPath, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private string WritePfx(X509Certificate2 certificate, string? password = null)
    {
        var path = Path.Combine(_tmpPath, Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static (JsonObject Header, JsonObject Payload, byte[] Signature, string SigningInput) Decode(string jwt)
    {
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        return (
            JsonNode.Parse(FromBase64Url(parts[0]))!.AsObject(),
            JsonNode.Parse(FromBase64Url(parts[1]))!.AsObject(),
            FromBase64UrlBytes(parts[2]),
            parts[0] + "." + parts[1]);
    }

    private static string FromBase64Url(string value) =>
        Encoding.UTF8.GetString(FromBase64UrlBytes(value));

    private static byte[] FromBase64UrlBytes(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    // ── Client assertion JWT shape ───────────────────────────────────────────

    [Fact]
    public void AssertionHeaderCarriesRs256AndX5tS256()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        var jwt = ClientAssertionJwt.Build(certificate, "tid-123", "cid-456", "https://login.microsoftonline.com");
        var (header, _, _, _) = Decode(jwt);

        Assert.Equal("RS256", header["alg"]!.GetValue<string>());
        Assert.Equal("JWT", header["typ"]!.GetValue<string>());
        Assert.Equal(
            ClientAssertionJwt.Base64Url(SHA256.HashData(certificate.RawData)),
            header["x5t#S256"]!.GetValue<string>());
    }

    [Fact]
    public void AssertionClaimsMatchTenantClientAndWindow()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var jwt = ClientAssertionJwt.Build(
            certificate, "tid-123", "cid-456", "https://login.microsoftonline.com", now);
        var (_, payload, _, _) = Decode(jwt);

        Assert.Equal(
            "https://login.microsoftonline.com/tid-123/oauth2/v2.0/token",
            payload["aud"]!.GetValue<string>());
        Assert.Equal("cid-456", payload["iss"]!.GetValue<string>());
        Assert.Equal("cid-456", payload["sub"]!.GetValue<string>());
        Assert.True(Guid.TryParse(payload["jti"]!.GetValue<string>(), out _));
        Assert.Equal(1_790_000_000, payload["nbf"]!.GetValue<long>());
        Assert.Equal(1_790_000_000, payload["iat"]!.GetValue<long>());
        Assert.Equal(
            1_790_000_000 + (long)ClientAssertionJwt.Lifetime.TotalSeconds,
            payload["exp"]!.GetValue<long>());
    }

    [Fact]
    public void AssertionSignatureVerifiesWithTheCertificatePublicKey()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        var jwt = ClientAssertionJwt.Build(certificate, "tid", "cid", "https://login.microsoftonline.com");
        var (_, _, signature, signingInput) = Decode(jwt);

        using var rsa = certificate.GetRSAPublicKey()!;
        Assert.True(rsa.VerifyData(
            Encoding.ASCII.GetBytes(signingInput), signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        // Tampering breaks the signature.
        var tampered = Encoding.ASCII.GetBytes(signingInput + "x");
        Assert.False(rsa.VerifyData(tampered, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void EachAssertionHasAFreshJti()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        var first = Decode(ClientAssertionJwt.Build(certificate, "t", "c", "https://login.microsoftonline.com"));
        var second = Decode(ClientAssertionJwt.Build(certificate, "t", "c", "https://login.microsoftonline.com"));
        Assert.NotEqual(
            first.Payload["jti"]!.GetValue<string>(),
            second.Payload["jti"]!.GetValue<string>());
    }

    [Fact]
    public void Base64UrlUsesTheJwtAlphabetWithoutPadding()
    {
        var encoded = ClientAssertionJwt.Base64Url(new byte[] { 0xfb, 0xff, 0xfe, 0x01 });
        Assert.DoesNotContain("=", encoded);
        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
    }

    [Fact]
    public void AuthorityHostHonorsSovereignOverride()
    {
        Assert.Equal("https://login.microsoftonline.com", GraphAuth.AuthorityHost());
        Environment.SetEnvironmentVariable("AZURE_AUTHORITY_HOST", "https://login.microsoftonline.us/");
        Assert.Equal("https://login.microsoftonline.us", GraphAuth.AuthorityHost());

        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        var jwt = ClientAssertionJwt.Build(certificate, "tid", "cid", GraphAuth.AuthorityHost());
        var (_, payload, _, _) = Decode(jwt);
        Assert.Equal(
            "https://login.microsoftonline.us/tid/oauth2/v2.0/token",
            payload["aud"]!.GetValue<string>());
    }

    // ── Credential mode selection ────────────────────────────────────────────

    [Fact]
    public void DefaultModeIsTheDefaultCredentialChain()
    {
        Assert.False(GraphAuth.CertificateConfigured);
        var credential = GraphAuth.CreateCredential();
        Assert.IsType<DefaultAzureCredential>(credential);
    }

    [Fact]
    public void CertificateWinsWhenBothCertAndSecretAreConfigured()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        Environment.SetEnvironmentVariable(GraphAuth.CertPathEnvVar, WritePfx(certificate));
        Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "tid");
        Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "cid");
        Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "a-secret-that-must-lose");

        Assert.True(GraphAuth.CertificateConfigured);
        var credential = GraphAuth.CreateCredential();
        Assert.IsType<ClientAssertionCredential>(credential);
    }

    [Fact]
    public void CertificateModeRequiresTenantAndClientId()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        Environment.SetEnvironmentVariable(GraphAuth.CertPathEnvVar, WritePfx(certificate));

        var exc = Assert.Throws<InvalidOperationException>(() => GraphAuth.CreateCredential());
        Assert.Contains("AZURE_TENANT_ID", exc.Message);

        Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "tid");
        exc = Assert.Throws<InvalidOperationException>(() => GraphAuth.CreateCredential());
        Assert.Contains("AZURE_CLIENT_ID", exc.Message);
    }

    // ── PFX loading ──────────────────────────────────────────────────────────

    [Fact]
    public void PfxLoadsWithAndWithoutPassword()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();

        var plain = GraphAuth.LoadPfx(WritePfx(certificate), password: null);
        Assert.True(plain.HasPrivateKey);
        Assert.Equal(certificate.Thumbprint, plain.Thumbprint);

        var protectedPath = WritePfx(certificate, "pfx-pass");
        var loaded = GraphAuth.LoadPfx(protectedPath, "pfx-pass");
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void WrongPfxPasswordFailsFastNamingTheSetting()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        var path = WritePfx(certificate, "correct");
        var exc = Assert.Throws<InvalidOperationException>(() => GraphAuth.LoadPfx(path, "wrong"));
        Assert.Contains(GraphAuth.CertPathEnvVar, exc.Message);
        Assert.Contains(GraphAuth.CertPasswordEnvVar, exc.Message);
    }

    [Fact]
    public void MissingPfxFileFailsFastNamingTheSetting()
    {
        var exc = Assert.Throws<InvalidOperationException>(
            () => GraphAuth.LoadPfx(Path.Combine(_tmpPath, "missing.pfx"), null));
        Assert.Contains(GraphAuth.CertPathEnvVar, exc.Message);
    }

    [Fact]
    public void UnknownThumbprintFailsFastNamingTheSetting()
    {
        var exc = Assert.Throws<InvalidOperationException>(
            () => GraphAuth.FindInStores("0000000000000000000000000000000000000000"));
        Assert.Contains(GraphAuth.CertThumbprintEnvVar, exc.Message);
    }

    [Fact]
    public void ErrorMessagesNeverContainKeyMaterial()
    {
        using var certificate = TestCertificates.CreateSelfSignedWithKey();
        var path = WritePfx(certificate, "super-secret-password");
        Environment.SetEnvironmentVariable(GraphAuth.CertPasswordEnvVar, "wrong-guess");
        var exc = Assert.Throws<InvalidOperationException>(() => GraphAuth.LoadPfx(path, "wrong-guess"));
        Assert.DoesNotContain("super-secret-password", exc.Message);
        Assert.DoesNotContain("wrong-guess", exc.Message);
    }
}
