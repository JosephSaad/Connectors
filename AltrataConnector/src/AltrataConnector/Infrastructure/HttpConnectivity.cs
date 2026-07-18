// Infrastructure/HttpConnectivity.cs
// ----------------------------------
// Outbound HTTP plumbing for locked-down enterprise networks:
//
//   * PROXY_URL       — explicit forward proxy for ALL connector HTTP
//                       (Graph, Entra token endpoint, Altrata API, alert
//                       webhook). Unset = direct / system default.
//   * PROXY_BYPASS    — semicolon/comma-separated .NET WebProxy bypass
//                       patterns (regexes) that go direct, e.g. an on-prem
//                       collector: `^https://otel\.corp\.local`.
//   * CA_BUNDLE_PATH  — PEM bundle of ADDITIONAL trust roots (TLS-inspection
//                       appliances, private PKI). ADDITIVE: the system store
//                       is consulted first and keeps working; only when the
//                       OS chain fails is a second chain built against the
//                       custom roots (X509Chain CustomRootTrust). A hostname
//                       mismatch is never forgiven.
//
// Bad input FAILS FAST naming the setting (ConfigurationError) from the Graph
// and Altrata client constructors — a half-applied proxy/trust config must
// never silently fall back to direct/untrusted connections. The alert webhook
// is the one exception (alerting must never break ingestion): it logs and
// falls back to a default handler.
//
// The OTLP trace exporter manages its own channel; standard OTEL_* /
// HTTPS_PROXY env vars configure it (docs/DEPLOYMENT_ENTERPRISE.md).

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AltrataConnector.Config;

namespace AltrataConnector.Infrastructure;

public static class HttpConnectivity
{
    public const string ProxyUrlEnvVar = "PROXY_URL";
    public const string ProxyBypassEnvVar = "PROXY_BYPASS";
    public const string CaBundleEnvVar = "CA_BUNDLE_PATH";

    /// <summary>
    /// Build the message handler every connector HttpClient sits on. Reads the
    /// env at construction time (once per client). Throws ConfigurationError
    /// naming the offending setting on bad input.
    /// </summary>
    public static HttpMessageHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler();

        var proxy = BuildProxy();
        if (proxy != null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        var extraRoots = LoadCaBundle();
        if (extraRoots != null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    ValidateWithAdditiveTrust(certificate, chain, errors, extraRoots),
            };
        }
        return handler;
    }

    /// <summary>PROXY_URL/PROXY_BYPASS → WebProxy, or null when unset.
    /// Fails fast naming the setting on an unparseable URL or bypass pattern.</summary>
    internal static IWebProxy? BuildProxy()
    {
        var raw = Environment.GetEnvironmentVariable(ProxyUrlEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConfigurationError(
                $"{ProxyUrlEnvVar} '{raw.Trim()}' is not a valid absolute http/https proxy URL");
        }

        var bypass = ParseBypassList();
        try
        {
            return bypass.Length == 0
                ? new WebProxy(uri)
                : new WebProxy(uri, BypassOnLocal: false, BypassList: bypass);
        }
        catch (ArgumentException exc)
        {
            // WebProxy compiles bypass entries as regexes — a bad pattern
            // surfaces here and must name the setting, not throw a bare regex error.
            throw new ConfigurationError(
                $"{ProxyBypassEnvVar} contains an invalid bypass pattern ({exc.Message}). " +
                "Entries are .NET WebProxy regex patterns, separated by ';' or ','.");
        }
    }

    internal static string[] ParseBypassList()
    {
        var raw = Environment.GetEnvironmentVariable(ProxyBypassEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();
        return raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>CA_BUNDLE_PATH → additional PEM trust roots, or null when unset.
    /// Fails fast naming the setting when the file is missing, unreadable, not
    /// PEM, or contains no certificates.</summary>
    internal static X509Certificate2Collection? LoadCaBundle()
    {
        var raw = Environment.GetEnvironmentVariable(CaBundleEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim();
        if (!File.Exists(path))
            throw new ConfigurationError($"{CaBundleEnvVar} '{path}' does not exist");

        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPemFile(path);
        }
        catch (Exception exc) when (exc is CryptographicException or ArgumentException or IOException)
        {
            throw new ConfigurationError(
                $"{CaBundleEnvVar} '{path}' could not be parsed as a PEM certificate bundle " +
                $"({exc.GetType().Name}: {exc.Message})");
        }
        if (collection.Count == 0)
            throw new ConfigurationError($"{CaBundleEnvVar} '{path}' contains no certificates");
        return collection;
    }

    /// <summary>
    /// Additive server-certificate validation:
    ///   * OS validation already passed → accept.
    ///   * Hostname mismatch (or no certificate) → reject, ALWAYS — extra trust
    ///     roots never excuse a certificate issued for a different name.
    ///   * Chain errors only → build a second chain whose ONLY trusted roots are
    ///     the CA_BUNDLE_PATH certs (plus any intermediates the server presented)
    ///     and accept iff it verifies. Revocation is not checked on the custom
    ///     chain (private PKI CRLs are rarely reachable — documented residual,
    ///     see docs/THREAT_MODEL.md).
    /// </summary>
    internal static bool ValidateWithAdditiveTrust(
        System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        X509Chain? chain, SslPolicyErrors errors, X509Certificate2Collection extraRoots)
    {
        if (errors == SslPolicyErrors.None)
            return true;  // system trust succeeded — additive means we never subtract
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0 || certificate == null)
            return false;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return false; // never forgiven

        // Remaining error is RemoteCertificateChainErrors — retry against the
        // custom roots.
        using var leaf = new X509Certificate2(certificate);
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(extraRoots);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        if (chain != null)
        {
            // Intermediates the server presented (skip the leaf itself).
            foreach (var element in chain.ChainElements)
            {
                if (!element.Certificate.RawData.AsSpan().SequenceEqual(leaf.RawData))
                    custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }
        return custom.Build(leaf);
    }

    /// <summary>One-line status for validate-config output.</summary>
    public static string StatusLine()
    {
        var proxy = Environment.GetEnvironmentVariable(ProxyUrlEnvVar);
        var bundle = Environment.GetEnvironmentVariable(CaBundleEnvVar);
        if (string.IsNullOrWhiteSpace(proxy) && string.IsNullOrWhiteSpace(bundle))
            return "http=direct (no PROXY_URL / CA_BUNDLE_PATH)";
        return $"http proxy={(string.IsNullOrWhiteSpace(proxy) ? "direct" : proxy.Trim())} " +
               $"custom-ca={(string.IsNullOrWhiteSpace(bundle) ? "none" : bundle.Trim())}";
    }
}
