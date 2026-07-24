// Infrastructure/HttpTransport.cs
// -------------------------------
// Shared outbound HTTP transport policy: corporate proxy + custom CA trust
// (docs/DEPLOYMENT_ENTERPRISE.md).
//
// Every OUTBOUND HttpClient the connector constructs (SeismicClient — OAuth2
// token + API, GraphClient — token + Graph API, Alerting webhook POSTs) routes
// through CreateHandler() unless a test injects its own HttpMessageHandler.
// The webhook receiver and health endpoint are inbound listeners — no proxy
// or client-side trust applies to them.
//
//   PROXY_URL       e.g. http://proxy.contoso.com:8080 — explicit forward
//                   proxy for all outbound calls. Unset → direct (the .NET
//                   default still honours HTTPS_PROXY/HTTP_PROXY env vars).
//   PROXY_BYPASS    semicolon/comma-separated host wildcards that skip the
//                   proxy, e.g. "*.contoso.local;10.*".
//   CA_BUNDLE_PATH  PEM bundle of ADDITIONAL trusted root/issuing CAs
//                   (TLS-inspection appliances, private PKI). Additive: the
//                   system store keeps working; a chain that fails system
//                   trust is re-evaluated against ONLY this bundle via
//                   X509Chain CustomRootTrust. Hostname-mismatch and
//                   missing-certificate failures are NEVER excused.
//
// Misconfiguration fails FAST at client construction, naming the setting
// (ConfigException) — a half-trusted transport must not limp into a crawl.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Chassis;

/// <summary>Builds the outbound HttpMessageHandler honouring PROXY_URL /
/// PROXY_BYPASS / CA_BUNDLE_PATH. See file header for the contract.</summary>
public static class HttpTransport
{
    public const string ProxyUrlEnvVar = "PROXY_URL";
    public const string ProxyBypassEnvVar = "PROXY_BYPASS";
    public const string CaBundleEnvVar = "CA_BUNDLE_PATH";

    private static readonly IAppLogger Logger = Logging.GetLogger($"{Chassis.Identity.BaseLoggerName}.transport");

    /// <summary>
    /// Create the production handler for an outbound HttpClient. Direct,
    /// unmodified SocketsHttpHandler when no transport env vars are set (the
    /// historical behaviour). Throws <see cref="ConfigException"/> naming the
    /// offending setting on a bad proxy URL or CA bundle.
    /// </summary>
    public static HttpMessageHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler();

        var proxy = BuildProxy();
        if (proxy is not null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        var extraRoots = LoadCustomRoots();
        if (extraRoots is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    ValidateWithAdditiveTrust(certificate as X509Certificate2, chain, errors, extraRoots),
            };
        }
        return handler;
    }

    /// <summary>
    /// PROXY_URL → WebProxy (with PROXY_BYPASS wildcard list); null when unset.
    /// Invalid URL → ConfigException naming PROXY_URL.
    /// </summary>
    internal static IWebProxy? BuildProxy()
    {
        var url = Environment.GetEnvironmentVariable(ProxyUrlEnvVar);
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConfigException(
                $"Invalid configuration: {ProxyUrlEnvVar} '{url}' is not an absolute http(s) URL.");
        }

        var bypass = (Environment.GetEnvironmentVariable(ProxyBypassEnvVar) ?? "")
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var proxy = new WebProxy(uri) { BypassProxyOnLocal = true };
        foreach (var pattern in bypass)
            proxy.BypassArrayList.Add(WildcardToRegex(pattern));
        Logger.Info($"Outbound HTTP via proxy {uri} ({bypass.Length} bypass rule(s)).");
        return proxy;
    }

    /// <summary>
    /// Convert a host wildcard ("*.contoso.local") to the regex WebProxy's
    /// bypass list expects. WebProxy matches its bypass regexes against
    /// "{scheme}://{host}[:port]", so the pattern anchors around the host with
    /// any scheme and an optional port.
    /// </summary>
    internal static string WildcardToRegex(string pattern) =>
        "^[a-zA-Z][a-zA-Z0-9+.-]*://"
        + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal)
        + "(:\\d+)?$";

    /// <summary>
    /// CA_BUNDLE_PATH → certificate collection; null when unset. Missing file,
    /// unparseable PEM, or a bundle with zero certificates fail fast naming
    /// CA_BUNDLE_PATH.
    /// </summary>
    internal static X509Certificate2Collection? LoadCustomRoots()
    {
        var path = Environment.GetEnvironmentVariable(CaBundleEnvVar);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = path.Trim();
        if (!File.Exists(path))
        {
            throw new ConfigException(
                $"Invalid configuration: {CaBundleEnvVar} file not found: {path}");
        }
        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPemFile(path);
        }
        catch (CryptographicException ex)
        {
            throw new ConfigException(
                $"Invalid configuration: {CaBundleEnvVar} is not a valid PEM certificate bundle ({path}): {ex.Message}");
        }
        if (collection.Count == 0)
        {
            throw new ConfigException(
                $"Invalid configuration: {CaBundleEnvVar} contains no certificates: {path}");
        }
        Logger.Info($"Custom CA trust: {collection.Count} additional root(s) from {path}.");
        return collection;
    }

    /// <summary>
    /// Additive trust decision. System-trusted chains pass untouched. A chain
    /// failing ONLY on trust (untrusted/partial root) is re-built against the
    /// custom bundle with <see cref="X509ChainTrustMode.CustomRootTrust"/>.
    /// Hostname mismatch or a missing peer certificate is ALWAYS fatal —
    /// a private CA never excuses connecting to the wrong host.
    /// </summary>
    internal static bool ValidateWithAdditiveTrust(
        X509Certificate2? certificate,
        X509Chain? presentedChain,
        SslPolicyErrors errors,
        X509Certificate2Collection extraRoots)
    {
        if (errors == SslPolicyErrors.None)
            return true;  // system trust already satisfied
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0 || certificate is null)
            return false;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return false;

        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(extraRoots);
        // Private/inspection CAs rarely publish reachable CRL/OCSP endpoints;
        // revocation for the custom-trust path is intentionally off (documented
        // in docs/THREAT_MODEL.md — the bundle itself is the revocation lever).
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presentedChain is not null)
        {
            foreach (var element in presentedChain.ChainElements)
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        return custom.Build(certificate);
    }
}
