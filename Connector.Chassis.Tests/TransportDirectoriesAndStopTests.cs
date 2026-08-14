// TransportDirectoriesAndStopTests.cs
// -----------------------------------
// The three "everything else touches this" primitives of the chassis:
//
//   HttpTransport   — the ONLY outbound handler factory. Every connector's
//                     Graph client, source-system client and alert webhook is
//                     built on it, so a regression here is a fleet-wide
//                     regression: silently no proxy on a proxy-only host, or
//                     silently trusting/refusing a private CA.
//   SecureDirectory — creates logs/, data/ and the dead-letter store owner-only.
//                     Its contract is "never throws, always idempotent": every
//                     caller invokes it unguarded on the startup path.
//   ServiceStop     — the process-wide graceful-stop flag the SCM path sets and
//                     the schedulers poll. Must be idempotent and must never
//                     throw on the shutdown path.
//
// Offline by construction: no listener is ever bound, no request is ever sent.
// The TLS tests use an in-memory, ephemeral private PKI (a self-signed root and
// a leaf it issued); the CA-bundle tests write PEM into a temp subdirectory.
//
// PROCESS GLOBALS. Every test that touches PROXY_URL / PROXY_BYPASS /
// CA_BUNDLE_PATH or ServiceStop's internal source restores it in a finally/
// Dispose. The transport scopes clear ALL THREE variables even when a test only
// cares about one, so a developer machine that legitimately exports PROXY_URL
// cannot change a result. xunit.runner.json disables collection parallelism, so
// the restore only has to be exception-safe, not concurrency-safe.

using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Chassis.Tests;

// ---------------------------------------------------------------------------
// Shared scaffolding. Names are module-prefixed because the whole suite compiles
// into one assembly alongside every other chassis test file.
// ---------------------------------------------------------------------------

/// <summary>Sets env vars for the duration of a test and restores the previous
/// values (including "was not set") on dispose.</summary>
internal sealed class TransportDirStopEnvScope : IDisposable
{
    private readonly (string Name, string? Original)[] _saved;

    internal TransportDirStopEnvScope(params (string Name, string? Value)[] vars)
    {
        _saved = vars.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name))).ToArray();
        foreach (var (name, value) in vars)
            Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>All three transport knobs cleared, then the given overrides applied.
    /// Hermetic: an inherited PROXY_URL must not leak into a test.</summary>
    internal static TransportDirStopEnvScope Transport(params (string Name, string? Value)[] overrides)
    {
        var all = new List<(string, string?)>
        {
            (HttpTransport.ProxyUrlEnvVar, null),
            (HttpTransport.ProxyBypassEnvVar, null),
            (HttpTransport.CaBundleEnvVar, null),
        };
        all.AddRange(overrides.Select(o => (o.Name, o.Value)));
        return new TransportDirStopEnvScope(all.ToArray());
    }

    public void Dispose()
    {
        foreach (var (name, original) in _saved)
            Environment.SetEnvironmentVariable(name, original);
    }
}

/// <summary>A temp subdirectory under the OS temp root — the only filesystem the
/// suite is allowed to touch.</summary>
internal sealed class TransportDirStopTempDir : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("chassis-tds-").FullName;

    public void Dispose()
    {
        // Best-effort: on Windows a virus scanner or a still-open handle can hold
        // a child briefly, and a failed cleanup must not fail a green test.
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Ephemeral in-memory PKI for the additive-trust tests. Built once per
/// process (RSA keygen is the slowest thing in this file) and never disposed —
/// the certs live as long as the test host.</summary>
internal static class TransportDirStopPki
{
    private static readonly Lazy<(X509Certificate2 Root, X509Certificate2 Leaf)> PrivateCa =
        new(() => Issue("CN=Chassis Test Private Root CA", "CN=connector.corp.local"));

    private static readonly Lazy<(X509Certificate2 Root, X509Certificate2 Leaf)> ForeignCa =
        new(() => Issue("CN=Chassis Test Foreign Root CA", "CN=attacker.corp.local"));

    internal static X509Certificate2 PrivateRoot => PrivateCa.Value.Root;

    internal static X509Certificate2 PrivateLeaf => PrivateCa.Value.Leaf;

    internal static X509Certificate2 ForeignLeaf => ForeignCa.Value.Leaf;

    private static (X509Certificate2 Root, X509Certificate2 Leaf) Issue(string rootDn, string leafDn)
    {
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest(
            rootDn, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        var root = rootReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest(
            leafDn, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        var leaf = leafReq.Create(
            root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
        return (root, leaf);
    }
}

// ---------------------------------------------------------------------------
// HttpTransport — proxy
// ---------------------------------------------------------------------------

public class TransportDirStopProxyTests
{
    [Fact]
    public void CreateHandler_NoKnobsSet_IsAPlainDirectHandler()
    {
        // The historical default. Two invariants a caller depends on: no proxy is
        // invented (so .NET's own HTTPS_PROXY handling still applies), and NO
        // certificate-validation callback is installed — an accidental callback
        // here would replace platform trust for every outbound call in the fleet.
        using var env = TransportDirStopEnvScope.Transport();

        using var handler = Assert.IsType<SocketsHttpHandler>(HttpTransport.CreateHandler());

        Assert.Null(handler.Proxy);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        using var client = new HttpClient(handler, disposeHandler: false);
        Assert.NotNull(client);  // usable as an HttpClient handler, which is its only job
    }

    [Fact]
    public void CreateHandler_ReturnsAFreshHandlerEachCall()
    {
        // Callers hand the handler to an HttpClient that owns and disposes it. A
        // cached/shared handler would be disposed out from under the next caller.
        using var env = TransportDirStopEnvScope.Transport();

        using var first = HttpTransport.CreateHandler();
        using var second = HttpTransport.CreateHandler();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void CreateHandler_ProxyUrlSet_IsHonouredOnTheHandler()
    {
        // PROXY_URL only works if it lands on the handler AND UseProxy is on:
        // setting Proxy while UseProxy is false is the classic silent-direct bug.
        using var env = TransportDirStopEnvScope.Transport(
            (HttpTransport.ProxyUrlEnvVar, "http://proxy.contoso.com:8080"));

        using var handler = Assert.IsType<SocketsHttpHandler>(HttpTransport.CreateHandler());

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://proxy.contoso.com:8080"), proxy.Address);
        // Chassis policy (differs from the pre-chassis Hadoop copy): loopback and
        // dotless hostnames never go through the corporate proxy.
        Assert.True(proxy.BypassProxyOnLocal);
        Assert.Equal(
            new Uri("http://proxy.contoso.com:8080"),
            proxy.GetProxy(new Uri("https://graph.microsoft.com/v1.0/")));
    }

    [Fact]
    public void BuildProxy_SurroundingWhitespaceIsTrimmed()
    {
        // Env vars set from a .env file or a service definition routinely carry a
        // trailing space; rejecting those as "malformed" would be a support call.
        using var env = TransportDirStopEnvScope.Transport(
            (HttpTransport.ProxyUrlEnvVar, "  https://proxy.contoso.com:3128  "));

        var proxy = Assert.IsType<WebProxy>(HttpTransport.BuildProxy());

        Assert.Equal(new Uri("https://proxy.contoso.com:3128"), proxy.Address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildProxy_UnsetOrBlank_MeansDirect(string? value)
    {
        // Blank must mean "unset", not "malformed": clearing a variable by setting
        // it to "" is how operators disable the proxy in a service definition.
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.ProxyUrlEnvVar, value));

        Assert.Null(HttpTransport.BuildProxy());
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://proxy.contoso.com:21")]      // scheme must be http(s)
    [InlineData("socks5://proxy.contoso.com:1080")] // SocketsHttpHandler.Proxy cannot do SOCKS via WebProxy
    [InlineData("proxy.contoso.com:8080")]          // scheme required
    [InlineData("/relative/path")]
    public void BuildProxy_Malformed_FailsFastNamingTheSettingAndTheValue(string url)
    {
        // A bad proxy URL must be a startup failure with an actionable message,
        // never a handler that silently goes direct. The operator greps the log
        // for the variable name, so both the name and the offending value matter.
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.ProxyUrlEnvVar, url));

        var exc = Assert.Throws<ConfigException>(() => HttpTransport.BuildProxy());

        Assert.Contains(HttpTransport.ProxyUrlEnvVar, exc.Message, StringComparison.Ordinal);
        Assert.Contains(url, exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateHandler_MalformedProxyUrl_ThrowsAtConstructionNotAtFirstRequest()
    {
        // The failure has to surface where the connector can report it (client
        // construction), not deep inside the first Graph call.
        using var env = TransportDirStopEnvScope.Transport(
            (HttpTransport.ProxyUrlEnvVar, "ftp://proxy.contoso.com:21"));

        var exc = Assert.Throws<ConfigException>(() => HttpTransport.CreateHandler());

        Assert.Contains(HttpTransport.ProxyUrlEnvVar, exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProxy_BypassPatterns_MatchHostsAndNothingElse()
    {
        // The bypass list is a security-shaped setting: too loose and internal
        // traffic skips the inspecting proxy; too tight and the connector cannot
        // reach the on-prem source system at all.
        using var env = TransportDirStopEnvScope.Transport(
            (HttpTransport.ProxyUrlEnvVar, "http://proxy.contoso.com:8080"),
            (HttpTransport.ProxyBypassEnvVar, "*.contoso.local; 10.*,sf-gateway.example.com"));

        var proxy = Assert.IsType<WebProxy>(HttpTransport.BuildProxy());

        Assert.True(proxy.IsBypassed(new Uri("https://crm.contoso.local/services/data/")));
        Assert.True(proxy.IsBypassed(new Uri("http://10.20.30.40:8080/")));
        Assert.True(proxy.IsBypassed(new Uri("https://sf-gateway.example.com/")));
        // Anchored at both ends: a host merely CONTAINING a pattern is not bypassed.
        Assert.False(proxy.IsBypassed(new Uri("https://x10.example.com/")));
        Assert.False(proxy.IsBypassed(new Uri("https://sf-gateway.example.com.evil.net/")));
        Assert.False(proxy.IsBypassed(new Uri("https://graph.microsoft.com/v1.0/")));
        Assert.False(proxy.IsBypassed(new Uri("https://login.microsoftonline.com/")));
    }

    [Fact]
    public void BuildProxy_BypassListSeparatorsAndBlanksAreTolerated()
    {
        // Semicolons, commas, stray spaces and empty entries all appear in real
        // deployment templates; an empty entry must not become a match-everything
        // regex ("^scheme://$" would be harmless, but "" would not).
        using var env = TransportDirStopEnvScope.Transport(
            (HttpTransport.ProxyUrlEnvVar, "http://proxy.contoso.com:8080"),
            (HttpTransport.ProxyBypassEnvVar, " ; , *.contoso.local , ; "));

        var proxy = Assert.IsType<WebProxy>(HttpTransport.BuildProxy());

        Assert.True(proxy.IsBypassed(new Uri("https://crm.contoso.local/")));
        Assert.False(proxy.IsBypassed(new Uri("https://graph.microsoft.com/")));
    }

    [Fact]
    public void BuildProxy_BypassSetWithoutProxyUrl_IsInert()
    {
        // Documents the precedence: PROXY_BYPASS alone configures nothing at all,
        // so an operator who sets only the bypass list gets direct traffic rather
        // than a half-configured proxy.
        using var env = TransportDirStopEnvScope.Transport(
            (HttpTransport.ProxyBypassEnvVar, "*.contoso.local"));

        Assert.Null(HttpTransport.BuildProxy());
        using var handler = Assert.IsType<SocketsHttpHandler>(HttpTransport.CreateHandler());
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void WildcardToRegex_EscapesRegexMetacharacters()
    {
        // '.' in a hostname is a literal. Without Regex.Escape, "10.1.2.3" would
        // match "10x1y2z3" and quietly widen the bypass list.
        var regex = HttpTransport.WildcardToRegex("a.b.local");

        Assert.Matches(regex, "https://a.b.local");
        Assert.Matches(regex, "http://a.b.local:8080");
        Assert.DoesNotMatch(regex, "https://axbylocal");
        Assert.DoesNotMatch(regex, "https://prefix-a.b.local");
        Assert.DoesNotMatch(regex, "https://a.b.local/path");  // WebProxy matches scheme://host[:port] only
    }

    [Fact]
    public void WildcardToRegex_QuestionMarkIsASingleCharacterWildcard()
    {
        // Documented wildcard vocabulary: '*' many, '?' exactly one.
        var regex = HttpTransport.WildcardToRegex("node?.contoso.local");

        Assert.Matches(regex, "https://node1.contoso.local");
        Assert.DoesNotMatch(regex, "https://node.contoso.local");
        Assert.DoesNotMatch(regex, "https://node12.contoso.local");
    }

    [Fact]
    public void WildcardToRegex_MatchesAnySchemeAndOptionalPort()
    {
        // WebProxy tests its bypass regexes against "{scheme}://{host}[:port]";
        // an http-only pattern would leave every https call proxied.
        var regex = HttpTransport.WildcardToRegex("*.contoso.local");

        Assert.Matches(regex, "http://crm.contoso.local");
        Assert.Matches(regex, "https://crm.contoso.local");
        Assert.Matches(regex, "https://crm.contoso.local:8443");
        Assert.DoesNotMatch(regex, "crm.contoso.local");
        Assert.DoesNotMatch(regex, "https://crm.contoso.local:notaport");
    }

    [Fact]
    public void BuildProxy_LocalAndDotlessHostsBypassTheProxyWithoutBeingListed()
    {
        // Undocumented but load-bearing: the chassis sets BypassProxyOnLocal, so
        // loopback and DOTLESS hostnames go direct even when PROXY_BYPASS is empty.
        // On-prem source systems are routinely addressed by a short hostname, so a
        // deployment that mandates "all egress via the proxy" does not get it.
        // Pinned as actual behaviour and reported.
        using var env = TransportDirStopEnvScope.Transport(
            (HttpTransport.ProxyUrlEnvVar, "http://proxy.contoso.com:8080"));

        var proxy = Assert.IsType<WebProxy>(HttpTransport.BuildProxy());

        Assert.True(proxy.IsBypassed(new Uri("http://namenode1:9870/")));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:8080/")));
        Assert.False(proxy.IsBypassed(new Uri("https://crm.contoso.com/")));
    }
}

// ---------------------------------------------------------------------------
// HttpTransport — CA bundle loading
// ---------------------------------------------------------------------------

public class TransportDirStopCaBundleTests
{
    [Fact]
    public void LoadCustomRoots_Unset_MeansSystemTrustOnly()
    {
        using var env = TransportDirStopEnvScope.Transport();

        Assert.Null(HttpTransport.LoadCustomRoots());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LoadCustomRoots_Blank_MeansSystemTrustOnly(string value)
    {
        // Same "blank == unset" rule as PROXY_URL; a blank must not be read as a
        // path to a file called "" and fail startup.
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, value));

        Assert.Null(HttpTransport.LoadCustomRoots());
    }

    [Fact]
    public void LoadCustomRoots_MissingFile_FailsFastNamingTheSettingAndPath()
    {
        // A typo'd bundle path must not degrade to "system trust only" — the
        // connector would then fail every TLS handshake with a confusing error
        // instead of one that names the setting.
        using var dir = new TransportDirStopTempDir();
        var missing = Path.Combine(dir.Root, "no-such-ca.pem");
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, missing));

        var exc = Assert.Throws<ConfigException>(() => HttpTransport.LoadCustomRoots());

        Assert.Contains(HttpTransport.CaBundleEnvVar, exc.Message, StringComparison.Ordinal);
        Assert.Contains(missing, exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCustomRoots_EmptyFile_FailsFastRatherThanTrustingNothing()
    {
        // An empty bundle would install a validation callback whose custom trust
        // store is empty: every private-CA chain would be rejected at handshake
        // time. Failing at construction is the difference between one clear error
        // and an hour of "why is every request failing".
        using var dir = new TransportDirStopTempDir();
        var path = Path.Combine(dir.Root, "empty.pem");
        File.WriteAllText(path, string.Empty);
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, path));

        var exc = Assert.Throws<ConfigException>(() => HttpTransport.LoadCustomRoots());

        Assert.Contains(HttpTransport.CaBundleEnvVar, exc.Message, StringComparison.Ordinal);
        Assert.Contains("no certificates", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCustomRoots_TextWithNoPemBlocks_TakesTheNoCertificatesPath()
    {
        // Actual behaviour, worth pinning: ImportFromPemFile silently SKIPS
        // content that contains no PEM armour, so a bundle that is really an HTML
        // error page or a DER file surfaces as "contains no certificates" rather
        // than as the "not a valid PEM" branch. Either way it fails fast, which is
        // what the caller needs.
        using var dir = new TransportDirStopTempDir();
        var path = Path.Combine(dir.Root, "garbage.pem");
        File.WriteAllText(path, "<html><body>403 Forbidden</body></html>");
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, path));

        var exc = Assert.Throws<ConfigException>(() => HttpTransport.LoadCustomRoots());

        Assert.Contains("no certificates", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCustomRoots_CorruptPemBlock_FailsFastAsInvalidPem()
    {
        // Truncated/corrupted bundle (a half-copied file): the CryptographicException
        // from the import must be translated into ConfigException naming the
        // setting, not escape as a raw crypto error.
        using var dir = new TransportDirStopTempDir();
        var path = Path.Combine(dir.Root, "corrupt.pem");
        File.WriteAllText(path, "-----BEGIN CERTIFICATE-----\nZm9vYmFy\n-----END CERTIFICATE-----\n");
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, path));

        var exc = Assert.Throws<ConfigException>(() => HttpTransport.LoadCustomRoots());

        Assert.Contains(HttpTransport.CaBundleEnvVar, exc.Message, StringComparison.Ordinal);
        Assert.Contains(path, exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCustomRoots_MultiCertificateBundle_LoadsEveryCertificate()
    {
        // Real bundles carry root + issuing CA. Dropping all but the first would
        // break every chain that terminates at the intermediate.
        using var dir = new TransportDirStopTempDir();
        var path = Path.Combine(dir.Root, "bundle.pem");
        File.WriteAllText(
            path,
            TransportDirStopPki.PrivateRoot.ExportCertificatePem()
            + Environment.NewLine
            + TransportDirStopPki.PrivateLeaf.ExportCertificatePem());
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, path));

        var roots = HttpTransport.LoadCustomRoots();

        Assert.NotNull(roots);
        Assert.Equal(2, roots!.Count);
    }

    [Fact]
    public void LoadCustomRoots_PathWhitespaceIsTrimmed()
    {
        using var dir = new TransportDirStopTempDir();
        var path = Path.Combine(dir.Root, "ca.pem");
        File.WriteAllText(path, TransportDirStopPki.PrivateRoot.ExportCertificatePem());
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, $"  {path}\t"));

        var roots = HttpTransport.LoadCustomRoots();

        Assert.NotNull(roots);
        Assert.Single(roots!);
    }

    [Fact]
    public void CreateHandler_CaBundleSet_InstallsACallbackWiredToThatBundle()
    {
        // Not just "a callback exists": the callback must be closed over the certs
        // from THIS file. Invoking it proves the wiring, offline.
        using var dir = new TransportDirStopTempDir();
        var path = Path.Combine(dir.Root, "ca.pem");
        File.WriteAllText(path, TransportDirStopPki.PrivateRoot.ExportCertificatePem());
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, path));

        using var handler = Assert.IsType<SocketsHttpHandler>(HttpTransport.CreateHandler());

        var callback = handler.SslOptions.RemoteCertificateValidationCallback;
        Assert.NotNull(callback);
        Assert.True(callback!(
            this, TransportDirStopPki.PrivateLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(callback(
            this, TransportDirStopPki.ForeignLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void CreateHandler_MissingCaBundle_FailsFastNamingTheSetting()
    {
        using var dir = new TransportDirStopTempDir();
        var missing = Path.Combine(dir.Root, "absent.pem");
        using var env = TransportDirStopEnvScope.Transport((HttpTransport.CaBundleEnvVar, missing));

        var exc = Assert.Throws<ConfigException>(() => HttpTransport.CreateHandler());

        Assert.Contains(HttpTransport.CaBundleEnvVar, exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateHandler_ProxyAndCaBundleTogether_BothApply()
    {
        // The TLS-inspection deployment: corporate proxy AND its private root.
        // The two code paths are independent and must not clobber each other.
        using var dir = new TransportDirStopTempDir();
        var path = Path.Combine(dir.Root, "ca.pem");
        File.WriteAllText(path, TransportDirStopPki.PrivateRoot.ExportCertificatePem());
        using var env = TransportDirStopEnvScope.Transport(
            (HttpTransport.ProxyUrlEnvVar, "http://proxy.contoso.com:8080"),
            (HttpTransport.CaBundleEnvVar, path));

        using var handler = Assert.IsType<SocketsHttpHandler>(HttpTransport.CreateHandler());

        Assert.True(handler.UseProxy);
        Assert.Equal(new Uri("http://proxy.contoso.com:8080"), Assert.IsType<WebProxy>(handler.Proxy).Address);
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
    }
}

// ---------------------------------------------------------------------------
// HttpTransport — additive trust decision
// ---------------------------------------------------------------------------

public class TransportDirStopTrustTests
{
    [Fact]
    public void Validate_SystemTrustAlreadySatisfied_ShortCircuits()
    {
        // Public endpoints (graph.microsoft.com) must not be re-evaluated against
        // the private bundle, which would reject them.
        Assert.True(HttpTransport.ValidateWithAdditiveTrust(
            null, null, SslPolicyErrors.None, new X509Certificate2Collection()));
    }

    [Fact]
    public void Validate_PrivateCaLeaf_AcceptedViaTheBundle()
    {
        // The whole point of CA_BUNDLE_PATH: a chain the system store rejects is
        // re-anchored on the operator-supplied root.
        Assert.True(HttpTransport.ValidateWithAdditiveTrust(
            TransportDirStopPki.PrivateLeaf,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            new X509Certificate2Collection { TransportDirStopPki.PrivateRoot }));
    }

    [Fact]
    public void Validate_LeafFromAnotherCa_Rejected()
    {
        // Additive, not permissive: a bundle is not "trust anything self-signed".
        Assert.False(HttpTransport.ValidateWithAdditiveTrust(
            TransportDirStopPki.ForeignLeaf,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            new X509Certificate2Collection { TransportDirStopPki.PrivateRoot }));
    }

    [Fact]
    public void Validate_HostnameMismatch_RejectedEvenWhenTheBundleWouldAnchorTheChain()
    {
        // The MITM case the bundle must never excuse: right CA, wrong host.
        Assert.False(HttpTransport.ValidateWithAdditiveTrust(
            TransportDirStopPki.PrivateLeaf,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            new X509Certificate2Collection { TransportDirStopPki.PrivateRoot }));
    }

    [Fact]
    public void Validate_NoPeerCertificate_Rejected()
    {
        // Both spellings of "nothing to validate": the error flag, and a null cert
        // arriving with any other error set.
        var bundle = new X509Certificate2Collection { TransportDirStopPki.PrivateRoot };

        Assert.False(HttpTransport.ValidateWithAdditiveTrust(
            null, null, SslPolicyErrors.RemoteCertificateNotAvailable, bundle));
        Assert.False(HttpTransport.ValidateWithAdditiveTrust(
            null, null, SslPolicyErrors.RemoteCertificateChainErrors, bundle));
    }

    [Fact]
    public void Validate_EmptyBundle_RejectsEverythingThatSystemTrustRejected()
    {
        // Defence in depth behind the LoadCustomRoots fail-fast: even if an empty
        // collection reached the callback, it must not become "allow all".
        Assert.False(HttpTransport.ValidateWithAdditiveTrust(
            TransportDirStopPki.PrivateLeaf,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            new X509Certificate2Collection()));
    }

    [Fact]
    public void Validate_PresentedChainElementsAreUsedAsIntermediates()
    {
        // The server-sent chain is fed to ExtraStore so an intermediate the client
        // does not hold can still complete the path. Building the presented chain
        // offline (revocation off, unknown root tolerated) mirrors what SslStream
        // hands the callback.
        using var presented = new X509Chain();
        presented.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // Keeps the offline promise on Windows, where CryptoAPI would otherwise be
        // free to fetch a missing issuer over AIA while building this chain.
        presented.ChainPolicy.DisableCertificateDownloads = true;
        presented.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        presented.ChainPolicy.ExtraStore.Add(TransportDirStopPki.PrivateRoot);
        presented.Build(TransportDirStopPki.PrivateLeaf);

        Assert.True(HttpTransport.ValidateWithAdditiveTrust(
            TransportDirStopPki.PrivateLeaf,
            presented,
            SslPolicyErrors.RemoteCertificateChainErrors,
            new X509Certificate2Collection { TransportDirStopPki.PrivateRoot }));
    }
}

// ---------------------------------------------------------------------------
// SecureDirectory
// ---------------------------------------------------------------------------

public class TransportDirStopSecureDirectoryTests
{
    /// <summary>0700. Spelled out rather than compared to the const, so a change to
    /// the const is a test failure and not a silent relaxation.</summary>
    private const UnixFileMode Rwx =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void OwnerOnly_IsExactlyZero700()
    {
        Assert.Equal(Rwx, SecureDirectory.OwnerOnly);
    }

    [Fact]
    public void EnsureHardened_CreatesTheDirectoryAndIsIdempotent()
    {
        // Every connector calls this unguarded on each startup, so the second call
        // on an already-hardened directory must be a no-op, not a throw.
        using var temp = new TransportDirStopTempDir();
        var target = Path.Combine(temp.Root, "state");

        SecureDirectory.EnsureHardened(target);
        SecureDirectory.EnsureHardened(target);

        Assert.True(Directory.Exists(target));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(Rwx, File.GetUnixFileMode(target));
        // Windows: the ACL tighten is explicitly best-effort (a locked-down host
        // may deny SetAccessControl), so only "created, did not throw" is asserted.
    }

    [Fact]
    public void EnsureHardened_ExistingWorldReadableDirectory_IsTightened()
    {
        // The upgrade path: directories created by an older build (or by an
        // operator with mkdir) must be pulled back to owner-only, not left as-is.
        using var temp = new TransportDirStopTempDir();
        var target = Path.Combine(temp.Root, "legacy-logs");
        Directory.CreateDirectory(target);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                target,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        SecureDirectory.EnsureHardened(target);

        Assert.True(Directory.Exists(target));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(Rwx, File.GetUnixFileMode(target));
    }

    [Fact]
    public void EnsureHardened_DoesNotTouchContents()
    {
        // Hardening is directory-level and NOT recursive. A caller must not assume
        // pre-existing files inside get tightened too — 0700 on the directory is
        // what keeps them unreachable by other users.
        using var temp = new TransportDirStopTempDir();
        var target = Path.Combine(temp.Root, "data");
        Directory.CreateDirectory(target);
        var checkpoint = Path.Combine(target, "checkpoint.json");
        File.WriteAllText(checkpoint, "{\"cursor\":1}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                checkpoint,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        SecureDirectory.EnsureHardened(target);

        Assert.Equal("{\"cursor\":1}", File.ReadAllText(checkpoint));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(checkpoint));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureHardened_BlankPath_IsASilentNoOp(string? path)
    {
        // Callers pass an optional configured path straight through. A blank must
        // not throw (startup would die on an unset optional setting) and must not
        // create anything.
        SecureDirectory.EnsureHardened(path!);
    }

    [Fact]
    public void EnsureHardened_MissingParents_CreatesTheWholeChainWithAnOwnerOnlyLeaf()
    {
        // Connectors pass nested paths such as <state>/logs/2026-08. The leaf — the
        // directory that actually holds the sensitive files — must be 0700.
        // The intermediate parents are created with the process umask default and
        // are NOT hardened (see the reported finding); their mode is deliberately
        // not asserted here because umask varies by host and CI image.
        using var temp = new TransportDirStopTempDir();
        var leaf = Path.Combine(temp.Root, "state", "logs", "run");

        SecureDirectory.EnsureHardened(leaf);

        Assert.True(Directory.Exists(leaf));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(Rwx, File.GetUnixFileMode(leaf));
    }

    [Fact]
    public void EnsureHardened_TrailingSeparator_IsAccepted()
    {
        // Paths assembled by string concatenation in connector config routinely
        // carry a trailing separator.
        using var temp = new TransportDirStopTempDir();
        var target = Path.Combine(temp.Root, "with-sep");

        SecureDirectory.EnsureHardened(target + Path.DirectorySeparatorChar);

        Assert.True(Directory.Exists(target));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(Rwx, File.GetUnixFileMode(target));
    }

    [Fact]
    public void EnsureHardened_PathOccupiedByAFile_BehavesDifferentlyPerOs()
    {
        // Documents ACTUAL, OS-divergent behaviour (reported as a finding, not
        // fixed here). The XML doc promises "creation failures propagate", and on
        // Windows they do. On POSIX the broad catch around the create-with-mode
        // call swallows the IOException, the fallback plain create throws again
        // into an empty catch, and EnsureHardened returns normally having created
        // nothing — the caller then fails later, opening a file under a directory
        // that does not exist. Guarded by OS because the two paths genuinely
        // differ in the source.
        using var temp = new TransportDirStopTempDir();
        var occupied = Path.Combine(temp.Root, "not-a-directory");
        File.WriteAllText(occupied, "i am a file");

        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsAny<IOException>(() => SecureDirectory.EnsureHardened(occupied));
        }
        else
        {
            SecureDirectory.EnsureHardened(occupied);  // swallowed: logs a warning only
            Assert.False(Directory.Exists(occupied));
            Assert.True(File.Exists(occupied));
        }
    }
}

// ---------------------------------------------------------------------------
// ServiceStop
// ---------------------------------------------------------------------------

/// <summary>Snapshots ServiceStop's private source and restores a usable one, so
/// a test that cancels or poisons the global signal cannot leave the rest of the
/// assembly running against a cancelled/disposed token.</summary>
internal sealed class TransportDirStopServiceStopScope : IDisposable
{
    private static readonly FieldInfo CtsField =
        typeof(ServiceStop).GetField("_cts", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "ServiceStop._cts not found — the disposed-source test can no longer reach the field it "
            + "must poison, and the state restore below would silently do nothing.");

    private readonly CancellationTokenSource _original;

    internal TransportDirStopServiceStopScope() => _original = Current;

    internal static CancellationTokenSource Current => (CancellationTokenSource)CtsField.GetValue(null)!;

    internal static void Install(CancellationTokenSource cts) => CtsField.SetValue(null, cts);

    public void Dispose() => Install(IsArmed(_original) ? _original : new CancellationTokenSource());

    private static bool IsArmed(CancellationTokenSource cts)
    {
        try
        {
            return !cts.Token.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}

public class TransportDirStopServiceStopTests
{
    [Fact]
    public void Request_SetsRequestedAndCancelsTheTokenCallersAlreadyHold()
    {
        // The schedulers capture ServiceStop.Token ONCE into a field and pass it to
        // Task.Delay. If Request() swapped in a new source instead of cancelling
        // the existing one, a stop would not interrupt a multi-hour delay.
        using var scope = new TransportDirStopServiceStopScope();
        ServiceStop.Reset();
        var held = ServiceStop.Token;
        Assert.False(ServiceStop.Requested);
        Assert.False(held.IsCancellationRequested);

        ServiceStop.Request();

        Assert.True(ServiceStop.Requested);
        Assert.True(held.IsCancellationRequested);
        Assert.True(ServiceStop.Token.IsCancellationRequested);
    }

    [Fact]
    public void Request_IsIdempotent_AndFiresRegistrationsExactlyOnce()
    {
        // The SCM can deliver a stop while the dashboard's Ctrl+X is already in
        // flight; a second Request must not throw and must not re-run shutdown
        // callbacks (double-flush, double-checkpoint).
        using var scope = new TransportDirStopServiceStopScope();
        ServiceStop.Reset();
        var fired = 0;
        using var registration = ServiceStop.Token.Register(() => Interlocked.Increment(ref fired));

        ServiceStop.Request();
        ServiceStop.Request();
        ServiceStop.Request();

        Assert.True(ServiceStop.Requested);
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public void Request_FromManyThreadsAtOnce_NeitherThrowsNorLosesTheSignal()
    {
        using var scope = new TransportDirStopServiceStopScope();
        ServiceStop.Reset();

        Parallel.For(0, 64, _ => ServiceStop.Request());

        Assert.True(ServiceStop.Requested);
        Assert.True(ServiceStop.Token.IsCancellationRequested);
    }

    [Fact]
    public void Reset_ReArms_AndTheOldTokenStaysCancelled()
    {
        // Reset is the between-test seam. A stale token captured before the reset
        // must NOT come back to life — code still holding it should keep winding
        // down rather than resume mid-shutdown.
        using var scope = new TransportDirStopServiceStopScope();
        ServiceStop.Reset();
        ServiceStop.Request();
        var stale = ServiceStop.Token;

        ServiceStop.Reset();

        Assert.False(ServiceStop.Requested);
        Assert.False(ServiceStop.Token.IsCancellationRequested);
        Assert.True(stale.IsCancellationRequested);
        Assert.NotEqual(stale, ServiceStop.Token);
    }

    [Fact]
    public void Reset_WhenNothingWasRequested_KeepsTheSameLiveToken()
    {
        // Reset on an un-cancelled signal must not churn the source: anything that
        // already registered a shutdown callback would otherwise be orphaned.
        using var scope = new TransportDirStopServiceStopScope();
        ServiceStop.Reset();
        var before = ServiceStop.Token;

        ServiceStop.Reset();

        Assert.Equal(before, ServiceStop.Token);
        Assert.False(ServiceStop.Requested);
    }

    [Fact]
    public void Request_OnAnAlreadyDisposedSource_DoesNotThrow()
    {
        // The ported-from-Altrata ObjectDisposedException catch. The window is real:
        // Reset() disposes the previous source, and an SCM stop landing just after
        // teardown calls Cancel() on it. Nothing useful can be done with the throw
        // on the shutdown path, so it is swallowed. If a future tidy-up removes the
        // catch, this test fails and says why.
        using var scope = new TransportDirStopServiceStopScope();
        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        TransportDirStopServiceStopScope.Install(disposed);

        ServiceStop.Request();  // must be a no-op, not an ObjectDisposedException

        // Requested stays readable on a disposed source (IsCancellationRequested is
        // not dispose-guarded) and reports the pre-dispose state.
        Assert.False(ServiceStop.Requested);
    }

    [Fact]
    public void Token_OnAnAlreadyDisposedSource_Throws_UnlikeRequestAndRequested()
    {
        // Actual behaviour, pinned because it is asymmetric and surprising:
        // Request() and Requested survive a disposed source, but the Token getter
        // is dispose-guarded by CancellationTokenSource and throws. Anything on the
        // shutdown path that re-reads ServiceStop.Token (rather than a cached copy)
        // inside that window gets an ObjectDisposedException the catch in Request()
        // does not cover. Reported as a finding.
        using var scope = new TransportDirStopServiceStopScope();
        var disposed = new CancellationTokenSource();
        disposed.Cancel();
        disposed.Dispose();
        TransportDirStopServiceStopScope.Install(disposed);

        Assert.True(ServiceStop.Requested);
        Assert.Throws<ObjectDisposedException>(() => ServiceStop.Token);
    }

    [Fact]
    public void Scope_RestoresAUsableSignalForEveryLaterTest()
    {
        // Guards the guard: if the restore in TransportDirStopServiceStopScope ever
        // stops working, every later test in the assembly would run against a
        // cancelled or disposed global and fail in unrelated places.
        using (var scope = new TransportDirStopServiceStopScope())
        {
            var disposed = new CancellationTokenSource();
            disposed.Dispose();
            TransportDirStopServiceStopScope.Install(disposed);
        }

        Assert.False(ServiceStop.Requested);
        Assert.False(ServiceStop.Token.IsCancellationRequested);
    }
}
