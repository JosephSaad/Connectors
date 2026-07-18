// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/HttpClientFactory.cs
// -----------------------------------
// One place that builds every outbound HTTP handler, so enterprise egress
// controls apply uniformly to the Salesforce client, the Graph client, the
// sharing-model session and the alert webhook poster:
//
//   PROXY_URL       explicit forward proxy (http[s]://host:port, optional
//                   user:pass@). Unset → .NET's default behavior, which
//                   already honors HTTPS_PROXY / HTTP_PROXY / NO_PROXY and
//                   the OS proxy — PROXY_URL exists for fleets that configure
//                   per-app proxies rather than machine env.
//   PROXY_BYPASS    semicolon/comma-separated host suffixes that skip the
//                   proxy (e.g. "login.microsoftonline.com;.internal.contoso.com").
//                   Only meaningful together with PROXY_URL.
//   CA_BUNDLE_PATH  PEM file of ADDITIONAL trusted roots for TLS-inspection
//                   (SSL-intercepting proxy) environments. Additive trust: the
//                   system store keeps working; server chains that terminate
//                   in either store pass. A missing/unparseable file fails
//                   fast at client construction, naming the setting.
//
// Azure SDK clients (Key Vault, DefaultAzureCredential token requests) use the
// SDK's own transport, which honors HTTPS_PROXY/HTTP_PROXY out of the box —
// set those alongside PROXY_URL when Entra/Key Vault traffic must also be
// proxied. docs/DEPLOYMENT_ENTERPRISE.md covers the full setup.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SalesforceCopilotConnector.Infrastructure;

public static class HttpClientFactory
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>
    /// Build a <see cref="SocketsHttpHandler"/> with the enterprise egress
    /// settings applied. Every production HttpClient in this codebase is
    /// constructed over this handler.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// PROXY_URL is not an absolute URI, or CA_BUNDLE_PATH is missing/unparseable.
    /// </exception>
    public static SocketsHttpHandler CreateHandler(int? maxConnectionsPerServer = null)
    {
        var handler = new SocketsHttpHandler();
        if (maxConnectionsPerServer is int max)
            handler.MaxConnectionsPerServer = max;

        var proxy = BuildProxyFromEnvironment();
        if (proxy != null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        // else: leave the handler's defaults — .NET resolves HTTPS_PROXY /
        // HTTP_PROXY / NO_PROXY and platform proxy settings on its own.

        var extraRoots = LoadCaBundleFromEnvironment();
        if (extraRoots != null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, chain, errors) =>
                    ValidateWithAdditionalRoots(certificate, chain, errors, extraRoots);
        }

        return handler;
    }

    // ── Proxy ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="WebProxy"/> from <c>PROXY_URL</c>/<c>PROXY_BYPASS</c>, or null when
    /// <c>PROXY_URL</c> is unset (default proxy behavior applies).
    /// </summary>
    internal static WebProxy? BuildProxyFromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("PROXY_URL");
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Invalid configuration: PROXY_URL is not an absolute URL: '{url}'");

        var proxy = new WebProxy(uri);
        // user:pass@ in PROXY_URL → explicit proxy credentials.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        var bypass = ParseBypassList(Environment.GetEnvironmentVariable("PROXY_BYPASS"));
        if (bypass.Length > 0)
            proxy.BypassList = bypass;
        return proxy;
    }

    /// <summary>
    /// Split <c>PROXY_BYPASS</c> on <c>;</c>/<c>,</c> and turn each host suffix into the
    /// regex form <see cref="WebProxy.BypassList"/> expects. Entries that already look
    /// like regexes (contain a backslash) pass through untouched.
    /// </summary>
    internal static string[] ParseBypassList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();
        return raw
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Contains('\\')
                ? entry
                : "^https?://([^/]*\\.)?" + System.Text.RegularExpressions.Regex.Escape(entry.TrimStart('.')) + "(:\\d+)?/?")
            .ToArray();
    }

    // ── Custom CA bundle ─────────────────────────────────────────────────────

    /// <summary>
    /// Load the PEM bundle named by <c>CA_BUNDLE_PATH</c>, or null when unset.
    /// Fail-fast contract: a set-but-broken value must abort the run with a
    /// message naming the setting — silently trusting nothing (or everything)
    /// in a TLS-inspection environment is worse than not starting.
    /// </summary>
    internal static X509Certificate2Collection? LoadCaBundleFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("CA_BUNDLE_PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return LoadCaBundle(path.Trim());
    }

    /// <summary>Load a PEM file of one or more CA certificates.</summary>
    internal static X509Certificate2Collection LoadCaBundle(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Invalid configuration: CA_BUNDLE_PATH file not found: '{path}'");
        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPemFile(path);
        }
        catch (Exception exc) when (exc is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Invalid configuration: CA_BUNDLE_PATH file '{path}' is not a parseable PEM certificate bundle: {exc.Message}",
                exc);
        }
        if (collection.Count == 0)
            throw new InvalidOperationException(
                $"Invalid configuration: CA_BUNDLE_PATH file '{path}' contains no certificates");
        return collection;
    }

    /// <summary>
    /// Certificate validation with ADDITIVE custom trust:
    ///   * default OS validation passed → accept (system store still valid);
    ///   * hostname mismatch / no certificate → always reject;
    ///   * untrusted chain → rebuild against CustomRootTrust seeded with the
    ///     CA_BUNDLE_PATH roots (server-presented intermediates in ExtraStore)
    ///     and accept iff that chain builds.
    /// </summary>
    internal static bool ValidateWithAdditionalRoots(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        X509Certificate2Collection extraRoots)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        // A name mismatch or absent certificate is never fixed by extra roots.
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0 || certificate is null)
            return false;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return false;

        try
        {
            using var leaf = new X509Certificate2(certificate);
            using var customChain = new X509Chain();
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.AddRange(extraRoots);
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (chain != null)
            {
                foreach (var element in chain.ChainElements)
                    customChain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
            return customChain.Build(leaf);
        }
        catch
        {
            // Chain building must never throw into the TLS stack; treat as invalid.
            return false;
        }
    }

    /// <summary>
    /// Preflight for both settings — called once at process start so a broken
    /// PROXY_URL / CA_BUNDLE_PATH aborts with a clean one-line error instead of
    /// a <see cref="TypeInitializationException"/> from a static HttpClient field.
    /// No-op (and no log line) when neither variable is set.
    /// </summary>
    public static void ValidateStartupConfiguration()
    {
        var proxy = BuildProxyFromEnvironment();
        if (proxy != null)
            Logger.Info($"Outbound proxy: {proxy.Address} (PROXY_URL; bypass entries: {proxy.BypassList.Length})");
        var roots = LoadCaBundleFromEnvironment();
        if (roots != null)
            Logger.Info($"TLS trust: {roots.Count} additional root(s) loaded from CA_BUNDLE_PATH (additive to the system store)");
    }
}
