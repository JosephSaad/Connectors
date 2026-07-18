// Infrastructure/HttpTransport.cs
// -------------------------------
// Shared outbound HTTP transport policy for every HttpClient this process
// constructs without an injected test handler (WebHdfsClient, GraphClient,
// Alerting webhook). Two enterprise knobs, both OFF by default:
//
//   PROXY_URL (+ PROXY_BYPASS)
//     Route outbound HTTP(S) through an explicit corporate proxy.
//     PROXY_BYPASS is a semicolon/comma-separated list of host patterns that
//     go DIRECT (wildcards allowed: "*.corp.local;namenode1;10.*"). Typical
//     deployment: Graph traffic via the proxy, the on-prem WebHDFS namenodes
//     bypassed. Unset = system default proxy behaviour, unchanged.
//
//   CA_BUNDLE_PATH
//     PEM bundle of ADDITIVE trust anchors. WebHDFS endpoints (and TLS-
//     inspecting proxies) very often present certificates from a PRIVATE
//     enterprise CA; this trusts that CA for this process without touching
//     the machine store. Additive means: certificates that already chain to
//     the system store still pass — the bundle only widens trust, it never
//     narrows it, and hostname mismatches always fail regardless.
//
// Both knobs fail FAST at client construction, naming the setting
// ("Invalid configuration: PROXY_URL ..."), rather than failing every request
// later with an opaque transport error.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace HadoopConnector.Infrastructure;

public static class HttpTransport
{
    public const string ProxyUrlEnvVar = "PROXY_URL";
    public const string ProxyBypassEnvVar = "PROXY_BYPASS";
    public const string CaBundleEnvVar = "CA_BUNDLE_PATH";

    /// <summary>
    /// Build the standard message handler honouring PROXY_URL / PROXY_BYPASS /
    /// CA_BUNDLE_PATH. With none of them set the handler is equivalent to the
    /// bare <c>new HttpClient()</c> default (system proxy, system trust).
    /// </summary>
    public static HttpMessageHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            // Matches HttpClientHandler defaults the clients relied on before.
            AllowAutoRedirect = true,
            UseCookies = false,
        };

        var proxyUrl = Environment.GetEnvironmentVariable(ProxyUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = BuildProxy(
                proxyUrl, Environment.GetEnvironmentVariable(ProxyBypassEnvVar));
            handler.UseProxy = true;
        }

        var bundlePath = Environment.GetEnvironmentVariable(CaBundleEnvVar);
        if (!string.IsNullOrWhiteSpace(bundlePath))
        {
            var bundle = LoadCaBundle(bundlePath);
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    ValidateWithBundle(certificate as X509Certificate2, chain, errors, bundle),
            };
        }

        return handler;
    }

    // ── Proxy ────────────────────────────────────────────────────────────────

    /// <summary>Parse PROXY_URL/PROXY_BYPASS into a WebProxy; invalid input
    /// fails fast naming the setting.</summary>
    internal static WebProxy BuildProxy(string proxyUrl, string? bypassList)
    {
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"Invalid configuration: {ProxyUrlEnvVar} must be an absolute http(s) URL "
                + $"(got '{proxyUrl}').");
        }
        var proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
        var patterns = BuildBypassRegexes(bypassList);
        if (patterns.Count > 0)
            proxy.BypassList = patterns.ToArray();
        return proxy;
    }

    /// <summary>
    /// Convert the PROXY_BYPASS host-pattern list ("*.corp.local;namenode1")
    /// into the anchored host regexes WebProxy.BypassList expects. Entries are
    /// matched against the request HOST, case-insensitively; '*' is the only
    /// wildcard. A malformed entry (empty after trim) is skipped.
    /// </summary>
    internal static List<string> BuildBypassRegexes(string? bypassList)
    {
        var regexes = new List<string>();
        if (string.IsNullOrWhiteSpace(bypassList))
            return regexes;
        foreach (var raw in bypassList.Split(';', ','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
                continue;
            var escaped = Regex.Escape(entry).Replace(@"\*", ".*", StringComparison.Ordinal);
            // WebProxy matches the bypass regex against the scheme://host:port
            // string; anchoring on the host segment keeps "10.*" from matching
            // "x10.example.com" while still matching any scheme/port.
            regexes.Add($@"(?i)^[a-z][a-z0-9+.-]*://{escaped}(:\d+)?/?$");
        }
        return regexes;
    }

    // ── Custom CA bundle ─────────────────────────────────────────────────────

    /// <summary>
    /// Load the PEM CA bundle. Missing file, unreadable file, or a file with
    /// no CERTIFICATE blocks all fail fast, naming CA_BUNDLE_PATH — a typo'd
    /// path must not silently downgrade to system-trust-only and then fail
    /// every WebHDFS request with a bare TLS error.
    /// </summary>
    internal static X509Certificate2Collection LoadCaBundle(string path)
    {
        var bundle = new X509Certificate2Collection();
        try
        {
            bundle.ImportFromPemFile(path);
        }
        catch (Exception exc)
        {
            throw new ArgumentException(
                $"Invalid configuration: {CaBundleEnvVar} '{path}' could not be loaded as a PEM "
                + $"certificate bundle ({exc.GetType().Name}: {exc.Message}).");
        }
        if (bundle.Count == 0)
        {
            throw new ArgumentException(
                $"Invalid configuration: {CaBundleEnvVar} '{path}' contains no certificates.");
        }
        return bundle;
    }

    /// <summary>
    /// Additive-trust validation: default-chain success passes unchanged; a
    /// chain-only failure is retried against the bundle as a custom root trust
    /// (X509Chain CustomTrustStore). Hostname mismatches and missing
    /// certificates always fail — the bundle never bypasses name checks.
    /// </summary>
    internal static bool ValidateWithBundle(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        X509Certificate2Collection bundle)
    {
        if (errors == SslPolicyErrors.None)
            return true;  // system trust already accepts it — additive, not restrictive
        if (certificate is null)
            return false;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return false; // a private CA does not excuse presenting the wrong host
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return false;

        // Chain errors only: rebuild the chain with the bundle as the trusted
        // root set. Intermediates the server sent ride along via ExtraStore.
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(bundle);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // private CAs rarely publish CRLs
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements)
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        try
        {
            return custom.Build(certificate);
        }
        catch
        {
            return false;
        }
    }
}
