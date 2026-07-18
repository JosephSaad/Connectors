// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for Infrastructure/HttpClientFactory.cs — PROXY_URL/PROXY_BYPASS
// parsing, CA_BUNDLE_PATH loading (and its fail-fast contract), and the
// additive-trust certificate validation callback (exercised with certificates
// generated in-test; no network).

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

/// <summary>Shared in-test certificate factory (also used by GraphAuthTests).</summary>
internal static class TestCertificates
{
    /// <summary>Self-signed CA certificate (CA basic constraints, with private key).</summary>
    internal static X509Certificate2 CreateCa(string subject = "CN=SFC Test CA")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
                critical: true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>Leaf certificate issued by <paramref name="ca"/> (public part only).</summary>
    internal static X509Certificate2 CreateLeaf(X509Certificate2 ca, string subject = "CN=graph.test.local")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        return request.Create(
            ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
    }

    /// <summary>Self-signed end-entity certificate WITH private key (for PFX/JWT tests).</summary>
    internal static X509Certificate2 CreateSelfSignedWithKey(string subject = "CN=sfc-client-cert")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}

/// <summary>Touches PROXY_URL/PROXY_BYPASS/CA_BUNDLE_PATH — "EnvVars" collection.</summary>
[Collection("EnvVars")]
public sealed class HttpClientFactoryTests : IDisposable
{
    private readonly string _tmpPath = Directory.CreateTempSubdirectory("httpfactory_").FullName;
    private readonly string? _savedProxyUrl = Environment.GetEnvironmentVariable("PROXY_URL");
    private readonly string? _savedProxyBypass = Environment.GetEnvironmentVariable("PROXY_BYPASS");
    private readonly string? _savedCaBundle = Environment.GetEnvironmentVariable("CA_BUNDLE_PATH");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PROXY_URL", _savedProxyUrl);
        Environment.SetEnvironmentVariable("PROXY_BYPASS", _savedProxyBypass);
        Environment.SetEnvironmentVariable("CA_BUNDLE_PATH", _savedCaBundle);
        try
        {
            Directory.Delete(_tmpPath, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    // ── Proxy configuration ──────────────────────────────────────────────────

    [Fact]
    public void NoProxyUrlMeansDefaultBehavior()
    {
        Environment.SetEnvironmentVariable("PROXY_URL", null);
        Assert.Null(HttpClientFactory.BuildProxyFromEnvironment());

        using var handler = HttpClientFactory.CreateHandler();
        Assert.Null(handler.Proxy);      // .NET default (HTTPS_PROXY et al.) applies
        Assert.True(handler.UseProxy);
    }

    [Fact]
    public void ProxyUrlIsParsedIntoWebProxy()
    {
        Environment.SetEnvironmentVariable("PROXY_URL", "http://proxy.corp.example:8080");
        Environment.SetEnvironmentVariable("PROXY_BYPASS", null);

        var proxy = HttpClientFactory.BuildProxyFromEnvironment();
        Assert.NotNull(proxy);
        Assert.Equal(new Uri("http://proxy.corp.example:8080"), proxy!.Address);
        Assert.Empty(proxy.BypassList);
    }

    [Fact]
    public void ProxyCredentialsComeFromUserInfo()
    {
        Environment.SetEnvironmentVariable("PROXY_URL", "http://svc%40corp:p%40ss@proxy.corp.example:3128");
        var proxy = HttpClientFactory.BuildProxyFromEnvironment()!;
        var credential = Assert.IsType<System.Net.NetworkCredential>(proxy.Credentials);
        Assert.Equal("svc@corp", credential.UserName);
        Assert.Equal("p@ss", credential.Password);
    }

    [Fact]
    public void ProxyBypassEntriesSkipTheProxy()
    {
        Environment.SetEnvironmentVariable("PROXY_URL", "http://proxy.corp.example:8080");
        Environment.SetEnvironmentVariable(
            "PROXY_BYPASS", "login.microsoftonline.com; .internal.corp.example");

        var proxy = HttpClientFactory.BuildProxyFromEnvironment()!;
        Assert.Equal(2, proxy.BypassList.Length);
        Assert.True(proxy.IsBypassed(new Uri("https://login.microsoftonline.com/tenant/oauth2")));
        Assert.True(proxy.IsBypassed(new Uri("https://sfdb.internal.corp.example/api")));
        Assert.False(proxy.IsBypassed(new Uri("https://graph.microsoft.com/v1.0/external")));
        Assert.False(proxy.IsBypassed(new Uri("https://evil-login.microsoftonline.com.attacker.net/")));
    }

    [Fact]
    public void InvalidProxyUrlFailsFastNamingTheSetting()
    {
        Environment.SetEnvironmentVariable("PROXY_URL", "not a url");
        var exc = Assert.Throws<InvalidOperationException>(
            () => HttpClientFactory.BuildProxyFromEnvironment());
        Assert.Contains("PROXY_URL", exc.Message);
    }

    [Fact]
    public void ParseBypassListSplitsAndEscapes()
    {
        Assert.Empty(HttpClientFactory.ParseBypassList(null));
        Assert.Empty(HttpClientFactory.ParseBypassList("  "));

        var parsed = HttpClientFactory.ParseBypassList("a.example;b.example, c.example");
        Assert.Equal(3, parsed.Length);
        Assert.All(parsed, entry => Assert.Contains("\\.", entry));  // dots escaped

        // Pre-escaped regex entries pass through untouched.
        var regex = HttpClientFactory.ParseBypassList(@"^https://raw\.example/");
        Assert.Equal(@"^https://raw\.example/", Assert.Single(regex));
    }

    // ── CA bundle loading ────────────────────────────────────────────────────

    [Fact]
    public void CaBundleUnsetIsNullAndHandlerHasNoCallback()
    {
        Environment.SetEnvironmentVariable("CA_BUNDLE_PATH", null);
        Assert.Null(HttpClientFactory.LoadCaBundleFromEnvironment());
        using var handler = HttpClientFactory.CreateHandler();
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void ValidPemBundleLoads()
    {
        using var ca = TestCertificates.CreateCa();
        var pemPath = Path.Combine(_tmpPath, "corp-roots.pem");
        File.WriteAllText(pemPath, ca.ExportCertificatePem() + "\n");

        var loaded = HttpClientFactory.LoadCaBundle(pemPath);
        Assert.Single(loaded);
        Assert.Equal(ca.Thumbprint, loaded[0].Thumbprint);

        Environment.SetEnvironmentVariable("CA_BUNDLE_PATH", pemPath);
        using var handler = HttpClientFactory.CreateHandler(maxConnectionsPerServer: 20);
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(20, handler.MaxConnectionsPerServer);
    }

    [Fact]
    public void MissingBundleFailsFastNamingTheSetting()
    {
        var exc = Assert.Throws<InvalidOperationException>(
            () => HttpClientFactory.LoadCaBundle(Path.Combine(_tmpPath, "missing.pem")));
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }

    [Fact]
    public void UnparseablePemFailsFastNamingTheSetting()
    {
        var garbagePath = Path.Combine(_tmpPath, "garbage.pem");
        File.WriteAllText(garbagePath, "this is not pem content");
        var exc = Assert.Throws<InvalidOperationException>(
            () => HttpClientFactory.LoadCaBundle(garbagePath));
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }

    [Fact]
    public void CreateHandlerSurfacesBrokenBundleAtConstruction()
    {
        Environment.SetEnvironmentVariable("CA_BUNDLE_PATH", Path.Combine(_tmpPath, "nope.pem"));
        var exc = Assert.Throws<InvalidOperationException>(() => HttpClientFactory.CreateHandler());
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }

    // ── Additive-trust validation callback ───────────────────────────────────

    [Fact]
    public void SystemTrustedChainsStillPass()
    {
        using var ca = TestCertificates.CreateCa();
        var extraRoots = new X509Certificate2Collection();
        // errors == None → accepted without consulting the custom store.
        Assert.True(HttpClientFactory.ValidateWithAdditionalRoots(
            null, null, SslPolicyErrors.None, extraRoots));
    }

    [Fact]
    public void UntrustedChainPassesWhenRootIsInTheBundle()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca);
        var extraRoots = new X509Certificate2Collection { ca };

        Assert.True(HttpClientFactory.ValidateWithAdditionalRoots(
            leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots));
    }

    [Fact]
    public void UntrustedChainFailsWhenRootIsNotInTheBundle()
    {
        using var ca = TestCertificates.CreateCa("CN=Corp Inspection CA");
        using var otherCa = TestCertificates.CreateCa("CN=Unrelated CA");
        using var leaf = TestCertificates.CreateLeaf(ca);
        var extraRoots = new X509Certificate2Collection { otherCa };

        Assert.False(HttpClientFactory.ValidateWithAdditionalRoots(
            leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots));
    }

    [Fact]
    public void NameMismatchIsNeverAccepted()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca);
        var extraRoots = new X509Certificate2Collection { ca };

        Assert.False(HttpClientFactory.ValidateWithAdditionalRoots(
            leaf, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            extraRoots));
    }

    [Fact]
    public void MissingCertificateIsNeverAccepted()
    {
        var extraRoots = new X509Certificate2Collection();
        Assert.False(HttpClientFactory.ValidateWithAdditionalRoots(
            null, null, SslPolicyErrors.RemoteCertificateNotAvailable, extraRoots));
        Assert.False(HttpClientFactory.ValidateWithAdditionalRoots(
            null, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots));
    }
}
