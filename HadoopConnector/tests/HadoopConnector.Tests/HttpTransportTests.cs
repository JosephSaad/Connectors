// Outbound transport policy (Infrastructure/HttpTransport.cs): PROXY_URL /
// PROXY_BYPASS parsing and bypass matching, CA_BUNDLE_PATH fail-fast loading,
// and the additive private-CA trust callback — exercised with in-test
// self-signed certificates (an ephemeral root CA + a leaf it issued). Offline.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class HttpTransportProxyTests
{
    [Fact]
    public void BuildProxy_ParsesUrl()
    {
        var proxy = HttpTransport.BuildProxy("http://proxy.corp.local:8080", null);
        Assert.Equal(new Uri("http://proxy.corp.local:8080"), proxy.Address);
        Assert.False(proxy.BypassProxyOnLocal);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://proxy:21")]
    [InlineData("proxy.corp.local:8080")]  // scheme required
    public void BuildProxy_InvalidUrl_FailsFast_NamingSetting(string url)
    {
        var exc = Assert.Throws<ArgumentException>(() => HttpTransport.BuildProxy(url, null));
        Assert.Contains("PROXY_URL", exc.Message);
    }

    [Fact]
    public void BypassList_WildcardsAndExactHosts()
    {
        var proxy = HttpTransport.BuildProxy(
            "http://proxy.corp.local:8080", "*.hadoop.corp.local; namenode1, 10.*");

        Assert.True(proxy.IsBypassed(new Uri("https://nn01.hadoop.corp.local:9871/webhdfs/v1/")));
        Assert.True(proxy.IsBypassed(new Uri("http://namenode1:9870/webhdfs/v1/")));
        Assert.True(proxy.IsBypassed(new Uri("http://10.20.30.40:9870/")));

        Assert.False(proxy.IsBypassed(new Uri("https://graph.microsoft.com/v1.0/")));
        Assert.False(proxy.IsBypassed(new Uri("https://login.microsoftonline.com/")));
        // Anchored: "10.*" must not match a host merely CONTAINING "10.".
        Assert.False(proxy.IsBypassed(new Uri("https://x10.example.com/")));
        Assert.False(proxy.IsBypassed(new Uri("https://evil-namenode1.example.com/")));
    }

    [Fact]
    public void BypassList_EmptyOrUnset_ProxiesEverything()
    {
        Assert.Empty(HttpTransport.BuildBypassRegexes(null));
        Assert.Empty(HttpTransport.BuildBypassRegexes("  ;  ,  "));
        var proxy = HttpTransport.BuildProxy("http://proxy:8080", null);
        Assert.False(proxy.IsBypassed(new Uri("http://namenode1:9870/")));
    }

    [Fact]
    public void CreateHandler_NoKnobsSet_NoProxyNoCustomTls()
    {
        using var env = new EnvScope(
            ("PROXY_URL", null), ("PROXY_BYPASS", null), ("CA_BUNDLE_PATH", null));
        var handler = HttpTransport.CreateHandler();
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Null(sockets.Proxy);
        Assert.Null(sockets.SslOptions.RemoteCertificateValidationCallback);
        handler.Dispose();
    }

    [Fact]
    public void CreateHandler_ProxyConfigured_IsApplied()
    {
        using var env = new EnvScope(
            ("PROXY_URL", "http://proxy.corp.local:3128"),
            ("PROXY_BYPASS", "*.hadoop.corp.local"),
            ("CA_BUNDLE_PATH", null));
        var handler = HttpTransport.CreateHandler();
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.True(sockets.UseProxy);
        var proxy = Assert.IsType<System.Net.WebProxy>(sockets.Proxy);
        Assert.Equal(new Uri("http://proxy.corp.local:3128"), proxy.Address);
        Assert.True(proxy.IsBypassed(new Uri("https://nn01.hadoop.corp.local:9871/")));
        handler.Dispose();
    }
}

public class HttpTransportCaBundleTests
{
    /// <summary>Ephemeral root CA + a leaf certificate it issued (in-memory only).</summary>
    internal static (X509Certificate2 Root, X509Certificate2 Leaf) MakeChain(
        string leafCn = "CN=namenode1.hadoop.corp.local")
    {
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest(
            "CN=Test Enterprise Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        var root = rootReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest(
            leafCn, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        var leaf = leafReq.Create(
            root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
        return (root, leaf);
    }

    [Fact]
    public void LoadCaBundle_MissingFile_FailsFast_NamingSetting()
    {
        var exc = Assert.Throws<ArgumentException>(
            () => HttpTransport.LoadCaBundle("/nonexistent/private-ca.pem"));
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }

    [Fact]
    public void LoadCaBundle_NotPem_FailsFast_NamingSetting()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "garbage.pem");
        File.WriteAllText(path, "this is not a certificate");
        var exc = Assert.Throws<ArgumentException>(() => HttpTransport.LoadCaBundle(path));
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }

    [Fact]
    public void LoadCaBundle_ValidPem_LoadsAllCertificates()
    {
        var (root, leaf) = MakeChain();
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "bundle.pem");
        File.WriteAllText(path,
            root.ExportCertificatePem() + Environment.NewLine + leaf.ExportCertificatePem());
        var bundle = HttpTransport.LoadCaBundle(path);
        Assert.Equal(2, bundle.Count);
    }

    [Fact]
    public void Validate_SystemTrustSuccess_PassesWithoutBundleConsultation()
    {
        var bundle = new X509Certificate2Collection();  // empty on purpose
        Assert.True(HttpTransport.ValidateWithBundle(
            null, null, SslPolicyErrors.None, bundle));
    }

    [Fact]
    public void Validate_PrivateCaLeaf_ChainErrors_AcceptedViaBundle()
    {
        var (root, leaf) = MakeChain();
        var bundle = new X509Certificate2Collection { root };
        // System trust rejected the chain (RemoteCertificateChainErrors); the
        // bundle-rooted rebuild must accept the leaf the private CA issued.
        Assert.True(HttpTransport.ValidateWithBundle(
            leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, bundle));
    }

    [Fact]
    public void Validate_LeafFromDifferentCa_Rejected()
    {
        var (_, leaf) = MakeChain();
        var (otherRoot, _) = MakeChain("CN=other-leaf");
        var bundle = new X509Certificate2Collection { otherRoot };
        Assert.False(HttpTransport.ValidateWithBundle(
            leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, bundle));
    }

    [Fact]
    public void Validate_NameMismatch_RejectedEvenWithTrustedBundle()
    {
        var (root, leaf) = MakeChain();
        var bundle = new X509Certificate2Collection { root };
        Assert.False(HttpTransport.ValidateWithBundle(
            leaf, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            bundle));
    }

    [Fact]
    public void Validate_NoCertificatePresented_Rejected()
    {
        var (root, _) = MakeChain();
        var bundle = new X509Certificate2Collection { root };
        Assert.False(HttpTransport.ValidateWithBundle(
            null, null, SslPolicyErrors.RemoteCertificateNotAvailable, bundle));
    }

    [Fact]
    public void CreateHandler_CaBundleConfigured_InstallsValidationCallback()
    {
        var (root, _) = MakeChain();
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "ca.pem");
        File.WriteAllText(path, root.ExportCertificatePem());
        using var env = new EnvScope(("PROXY_URL", null), ("CA_BUNDLE_PATH", path));
        var handler = HttpTransport.CreateHandler();
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.NotNull(sockets.SslOptions.RemoteCertificateValidationCallback);
        handler.Dispose();
    }

    [Fact]
    public void CreateHandler_BadBundlePath_FailsFast()
    {
        using var env = new EnvScope(
            ("PROXY_URL", null), ("CA_BUNDLE_PATH", "/nonexistent/private-ca.pem"));
        var exc = Assert.Throws<ArgumentException>(() => HttpTransport.CreateHandler());
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }
}
