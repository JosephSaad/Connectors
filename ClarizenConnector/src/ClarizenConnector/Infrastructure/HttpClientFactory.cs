// Infrastructure/HttpClientFactory.cs
// -----------------------------------
// Central outbound-HTTP handler construction: corporate proxy and custom
// trust-root (TLS-inspection) support for EVERY HttpClient the connector
// creates itself (ClarizenClient, GraphClient, Alerting webhook POSTs). Test
// fixtures that inject an HttpMessageHandler bypass this by design; the two
// inbound HttpListeners (health endpoint, webhook receiver) are servers and
// take no proxy.
//
//   PROXY_URL      http(s)://[user:pass@]host:port — routes all outbound HTTP
//                  through the proxy (credentials in the URL become basic
//                  proxy credentials; prefer an unauthenticated egress proxy).
//   PROXY_BYPASS   comma/semicolon/space list of hosts that go DIRECT:
//                  "graph.microsoft.com, *.clarizen.com, localhost".
//   CA_BUNDLE_PATH PEM bundle of ADDITIONAL trusted roots (TLS-inspecting
//                  proxies, private CAs). Additive: the platform trust store
//                  keeps working; a chain that fails platform trust is
//                  re-validated against the bundle via X509Chain
//                  CustomTrustStore. Only chain-trust errors are recoverable —
//                  a hostname mismatch or missing certificate stays fatal.
//
// All three fail fast at startup, naming the setting, on an invalid value
// (bad URL, missing file, unparseable PEM) — AppConfig.Load calls
// ValidateConfiguration(). See docs/DEPLOYMENT_ENTERPRISE.md.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ClarizenConnector.Infrastructure;

public static class HttpClientFactory
{
    public const string ProxyUrlEnvVar = "PROXY_URL";
    public const string ProxyBypassEnvVar = "PROXY_BYPASS";
    public const string CaBundleEnvVar = "CA_BUNDLE_PATH";

    /// <summary>
    /// Build the standard outbound handler honouring PROXY_URL / PROXY_BYPASS /
    /// CA_BUNDLE_PATH. With none of them set this is equivalent to the default
    /// handler. Throws ArgumentException naming the offending setting.
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

        var additionalRoots = LoadCaBundle();
        if (additionalRoots is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, chain, errors) =>
                    ValidateWithAdditionalRoots(certificate, chain, errors, additionalRoots);
        }

        return handler;
    }

    /// <summary>Fail fast on invalid PROXY_URL / CA_BUNDLE_PATH (called from AppConfig.Load).</summary>
    public static void ValidateConfiguration()
    {
        BuildProxy();
        LoadCaBundle();
    }

    // ── Proxy ────────────────────────────────────────────────────────────────

    /// <summary>Parse PROXY_URL (+ PROXY_BYPASS) into a proxy, or null when unset.</summary>
    internal static ConfiguredProxy? BuildProxy()
    {
        var raw = Environment.GetEnvironmentVariable(ProxyUrlEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"Invalid configuration: {ProxyUrlEnvVar} must be an absolute http(s) URL "
                + "(e.g. http://proxy.corp.local:8080).");
        }

        ICredentials? credentials = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }

        // Strip credentials from the address actually handed to the transport.
        var address = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;
        return new ConfiguredProxy(address, ParseBypassList(
            Environment.GetEnvironmentVariable(ProxyBypassEnvVar)))
        {
            Credentials = credentials,
        };
    }

    /// <summary>Split PROXY_BYPASS on commas/semicolons/whitespace into host entries.</summary>
    internal static string[] ParseBypassList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── Custom trust roots ───────────────────────────────────────────────────

    /// <summary>Load CA_BUNDLE_PATH as additional trusted roots, or null when unset.
    /// Bad path / bad PEM / empty bundle fail fast naming the setting.</summary>
    internal static X509Certificate2Collection? LoadCaBundle()
    {
        var path = Environment.GetEnvironmentVariable(CaBundleEnvVar);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path))
        {
            throw new ArgumentException(
                $"Invalid configuration: {CaBundleEnvVar} '{path}' does not exist.");
        }

        var roots = new X509Certificate2Collection();
        try
        {
            roots.ImportFromPemFile(path);
        }
        catch (Exception exc)
        {
            throw new ArgumentException(
                $"Invalid configuration: {CaBundleEnvVar} '{path}' is not a valid PEM "
                + $"certificate bundle ({exc.GetType().Name}: {exc.Message}).");
        }
        if (roots.Count == 0)
        {
            throw new ArgumentException(
                $"Invalid configuration: {CaBundleEnvVar} '{path}' contains no certificates.");
        }
        return roots;
    }

    /// <summary>
    /// Additive trust validation: platform trust first; a chain-trust failure
    /// (and ONLY a chain-trust failure — hostname mismatch or a missing
    /// certificate is never overridden) is re-validated against the additional
    /// roots with X509Chain CustomTrustStore. Revocation is not checked on the
    /// custom path (private roots rarely publish CRLs — documented in
    /// docs/DEPLOYMENT_ENTERPRISE.md).
    /// </summary>
    internal static bool ValidateWithAdditionalRoots(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        X509Certificate2Collection additionalRoots)
    {
        if (errors == SslPolicyErrors.None)
            return true;  // platform trust store accepted it — additive, not replacing
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            return false;  // name mismatch / not available: custom roots cannot fix that
        if (certificate is null)
            return false;

        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.CustomTrustStore.AddRange(additionalRoots);
        if (chain is not null)
        {
            // Carry over any intermediates the handshake presented.
            foreach (var element in chain.ChainElements)
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        }

        var leaf = certificate as X509Certificate2
            ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return custom.Build(leaf);
    }
}

/// <summary>
/// IWebProxy with deterministic, testable host-based bypass matching. An entry
/// matches its exact host, and "*.example.com" / ".example.com" /
/// "example.com" all match subdomains of example.com ("example.com" also
/// matches the apex). Comparison is case-insensitive.
/// </summary>
public sealed class ConfiguredProxy : IWebProxy
{
    private readonly Uri _address;
    private readonly string[] _bypass;

    public ConfiguredProxy(Uri address, string[] bypass)
    {
        _address = address;
        _bypass = bypass;
    }

    public Uri Address => _address;

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : _address;

    public bool IsBypassed(Uri host) =>
        _bypass.Any(entry => HostMatchesBypass(host.Host, entry));

    /// <summary>Pure bypass rule (unit-tested): exact host, or subdomain of a
    /// "*.suffix" / ".suffix" / bare-domain entry.</summary>
    internal static bool HostMatchesBypass(string host, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return false;
        var pattern = entry.Trim();
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            pattern = pattern[1..];  // "*.example.com" → ".example.com"

        if (pattern.StartsWith('.'))
            return host.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
    }
}
