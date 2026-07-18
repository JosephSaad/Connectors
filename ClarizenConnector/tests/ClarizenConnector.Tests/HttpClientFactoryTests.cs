// HttpClientFactoryTests.cs — PROXY_URL / PROXY_BYPASS / CA_BUNDLE_PATH
// handler construction: URL/credential parsing, deterministic bypass rules,
// additive custom-root validation (in-test generated CA + leaf certificates),
// and fail-fast errors naming the offending setting. No network anywhere.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

/// <summary>In-test certificate factory (self-signed CA, CA-signed leaves, PFX/PEM files).</summary>
internal static class TestCertificates
{
    public static X509Certificate2 SelfSigned(string subject = "CN=ClarizenConnectorTest")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public static X509Certificate2 CertificateAuthority(string subject = "CN=ClarizenConnector Test CA")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>Leaf certificate issued by <paramref name="ca"/> (public part only).</summary>
    public static X509Certificate2 IssuedBy(X509Certificate2 ca, string subject = "CN=inspected.example.com")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        return request.Create(
            ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
    }
}

public class HttpClientFactoryTests
{
    // ── PROXY_URL parsing ────────────────────────────────────────────────────

    [Fact]
    public void BuildProxy_Unset_ReturnsNull()
    {
        using var env = new EnvScope(("PROXY_URL", null));
        Assert.Null(HttpClientFactory.BuildProxy());
    }

    [Fact]
    public void BuildProxy_ParsesAddressAndCredentials_StripsUserInfoFromAddress()
    {
        using var env = new EnvScope(
            ("PROXY_URL", "http://svc-proxy:p%40ss@proxy.corp.local:8080"),
            ("PROXY_BYPASS", null));
        var proxy = HttpClientFactory.BuildProxy()!;

        Assert.Equal(new Uri("http://proxy.corp.local:8080/"), proxy.Address);
        var credential = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("svc-proxy", credential.UserName);
        Assert.Equal("p@ss", credential.Password);
    }

    [Fact]
    public void BuildProxy_InvalidUrl_FailsFastNamingTheSetting()
    {
        using var env = new EnvScope(("PROXY_URL", "not a proxy url"));
        var exc = Assert.Throws<ArgumentException>(() => HttpClientFactory.BuildProxy());
        Assert.Contains("PROXY_URL", exc.Message);
    }

    [Theory]
    [InlineData("graph.microsoft.com", "graph.microsoft.com", true)]
    [InlineData("GRAPH.MICROSOFT.COM", "graph.microsoft.com", true)]      // case-insensitive
    [InlineData("eu.graph.microsoft.com", "graph.microsoft.com", true)]   // subdomain of bare domain
    [InlineData("api.clarizen.com", "*.clarizen.com", true)]
    [InlineData("clarizen.com", "*.clarizen.com", false)]                 // wildcard needs a subdomain
    [InlineData("api.clarizen.com", ".clarizen.com", true)]
    [InlineData("evil-clarizen.com", "clarizen.com", false)]              // no substring tricks
    [InlineData("clarizen.com.evil.example", "clarizen.com", false)]      // suffix only, anchored
    [InlineData("localhost", "localhost", true)]
    [InlineData("anything", "", false)]
    public void HostMatchesBypass_Rules(string host, string entry, bool expected) =>
        Assert.Equal(expected, ConfiguredProxy.HostMatchesBypass(host, entry));

    [Fact]
    public void ConfiguredProxy_BypassedHostsGoDirect_OthersGoThroughTheProxy()
    {
        using var env = new EnvScope(
            ("PROXY_URL", "http://proxy.corp.local:8080"),
            ("PROXY_BYPASS", "*.corp.local, localhost; graph.microsoft.com"));
        var proxy = HttpClientFactory.BuildProxy()!;

        Assert.True(proxy.IsBypassed(new Uri("https://state.corp.local/db")));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:9464/metrics")));
        Assert.True(proxy.IsBypassed(new Uri("https://graph.microsoft.com/v1.0/external")));
        Assert.Null(proxy.GetProxy(new Uri("https://graph.microsoft.com/v1.0/external")));

        Assert.False(proxy.IsBypassed(new Uri("https://api.clarizen.com/v2.0/services")));
        Assert.Equal(
            new Uri("http://proxy.corp.local:8080/"),
            proxy.GetProxy(new Uri("https://api.clarizen.com/v2.0/services")));
    }

    [Fact]
    public void ParseBypassList_SplitsOnCommasSemicolonsAndWhitespace() =>
        Assert.Equal(
            new[] { "a.example", "b.example", "c.example" },
            HttpClientFactory.ParseBypassList("a.example, b.example;  c.example"));

    // ── Handler construction ─────────────────────────────────────────────────

    [Fact]
    public void CreateHandler_NothingConfigured_IsAPlainHandler()
    {
        using var env = new EnvScope(("PROXY_URL", null), ("CA_BUNDLE_PATH", null));
        using var handler = Assert.IsType<SocketsHttpHandler>(HttpClientFactory.CreateHandler());
        Assert.Null(handler.Proxy);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void CreateHandler_WithProxy_InstallsTheConfiguredProxy()
    {
        using var env = new EnvScope(
            ("PROXY_URL", "http://proxy.corp.local:8080"), ("CA_BUNDLE_PATH", null));
        using var handler = Assert.IsType<SocketsHttpHandler>(HttpClientFactory.CreateHandler());
        Assert.True(handler.UseProxy);
        Assert.IsType<ConfiguredProxy>(handler.Proxy);
    }

    // ── CA_BUNDLE_PATH ───────────────────────────────────────────────────────

    [Fact]
    public void LoadCaBundle_Unset_ReturnsNull()
    {
        using var env = new EnvScope(("CA_BUNDLE_PATH", null));
        Assert.Null(HttpClientFactory.LoadCaBundle());
    }

    [Fact]
    public void LoadCaBundle_MissingFile_FailsFastNamingTheSetting()
    {
        using var dir = new TempDir();
        using var env = new EnvScope(("CA_BUNDLE_PATH", Path.Combine(dir.Path, "absent.pem")));
        var exc = Assert.Throws<ArgumentException>(() => HttpClientFactory.LoadCaBundle());
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
        Assert.Contains("does not exist", exc.Message);
    }

    [Fact]
    public void LoadCaBundle_UnparseablePem_FailsFastNamingTheSetting()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "garbage.pem");
        File.WriteAllText(path, "this is not a pem bundle");
        using var env = new EnvScope(("CA_BUNDLE_PATH", path));
        var exc = Assert.Throws<ArgumentException>(() => HttpClientFactory.LoadCaBundle());
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }

    [Fact]
    public void LoadCaBundle_ValidPem_LoadsEveryCertificate()
    {
        using var dir = new TempDir();
        using var caA = TestCertificates.CertificateAuthority("CN=Test CA A");
        using var caB = TestCertificates.CertificateAuthority("CN=Test CA B");
        var path = Path.Combine(dir.Path, "roots.pem");
        File.WriteAllText(path, caA.ExportCertificatePem() + "\n" + caB.ExportCertificatePem() + "\n");
        using var env = new EnvScope(("CA_BUNDLE_PATH", path));

        var roots = HttpClientFactory.LoadCaBundle()!;
        Assert.Equal(2, roots.Count);
    }

    [Fact]
    public void ValidateWithAdditionalRoots_AcceptsChainSignedByTheCustomRoot()
    {
        using var ca = TestCertificates.CertificateAuthority();
        using var leaf = TestCertificates.IssuedBy(ca);
        var roots = new X509Certificate2Collection { X509CertificateLoader.LoadCertificate(ca.RawData) };

        // Platform trust rejected the chain (unknown root) — the additional
        // roots recover it.
        Assert.True(HttpClientFactory.ValidateWithAdditionalRoots(
            leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, roots));
    }

    [Fact]
    public void ValidateWithAdditionalRoots_RejectsChainsFromOtherIssuers()
    {
        using var ca = TestCertificates.CertificateAuthority();
        using var stranger = TestCertificates.SelfSigned("CN=unrelated.example.com");
        var roots = new X509Certificate2Collection { X509CertificateLoader.LoadCertificate(ca.RawData) };

        Assert.False(HttpClientFactory.ValidateWithAdditionalRoots(
            stranger, null, SslPolicyErrors.RemoteCertificateChainErrors, roots));
    }

    [Fact]
    public void ValidateWithAdditionalRoots_NeverOverridesNameMismatchOrMissingCert()
    {
        using var ca = TestCertificates.CertificateAuthority();
        using var leaf = TestCertificates.IssuedBy(ca);
        var roots = new X509Certificate2Collection { X509CertificateLoader.LoadCertificate(ca.RawData) };

        // A hostname mismatch stays fatal even when the chain would validate.
        Assert.False(HttpClientFactory.ValidateWithAdditionalRoots(
            leaf, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            roots));
        Assert.False(HttpClientFactory.ValidateWithAdditionalRoots(
            null, null, SslPolicyErrors.RemoteCertificateNotAvailable, roots));
        // And a clean platform validation stays accepted (additive semantics).
        Assert.True(HttpClientFactory.ValidateWithAdditionalRoots(
            leaf, null, SslPolicyErrors.None, roots));
    }

    [Fact]
    public void CreateHandler_WithCaBundle_InstallsTheValidationCallback()
    {
        using var dir = new TempDir();
        using var ca = TestCertificates.CertificateAuthority();
        var path = Path.Combine(dir.Path, "roots.pem");
        File.WriteAllText(path, ca.ExportCertificatePem());
        using var env = new EnvScope(("CA_BUNDLE_PATH", path), ("PROXY_URL", null));

        using var handler = Assert.IsType<SocketsHttpHandler>(HttpClientFactory.CreateHandler());
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);

        // The installed callback carries the loaded roots: a leaf signed by the
        // bundled CA passes a chain-errors handshake through it.
        using var leaf = TestCertificates.IssuedBy(ca);
        Assert.True(handler.SslOptions.RemoteCertificateValidationCallback!(
            this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void AppConfigLoad_FailsFast_OnBadProxyOrCaBundle()
    {
        using var scope = new SyncStateScope();
        using var env = new EnvScope(
            ("CONNECTOR_ID", "ClarizenAdaptiveWork"),
            ("CLARIZEN_USERNAME", "svc@example.com"),
            ("SECRET_CLARIZEN_PASSWORD", "pw"),
            ("AAD_APP_TENANT_ID", "t"),
            ("AAD_APP_CLIENT_ID", "c"),
            ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
            ("PROXY_URL", "://bad"),
            ("CA_BUNDLE_PATH", null));
        var exc = Assert.Throws<ArgumentException>(Config.AppConfig.Load);
        Assert.Contains("PROXY_URL", exc.Message);

        env.Set("PROXY_URL", null);
        env.Set("CA_BUNDLE_PATH", "/definitely/not/here.pem");
        exc = Assert.Throws<ArgumentException>(Config.AppConfig.Load);
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }
}
