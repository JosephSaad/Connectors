// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/GraphAuth.cs
// ------------------
// Credential selection for the Graph client, adding a certificate option to
// the existing client-secret flow:
//
//   (default)                    DefaultAzureCredential — env client secret
//                                (AZURE_CLIENT_ID/TENANT_ID/CLIENT_SECRET,
//                                aliased from AAD_APP_* / SECRET_* by
//                                Settings.LoadLocalEnvironment), managed
//                                identity, Azure CLI, ... Unchanged behavior.
//   GRAPH_CLIENT_CERT_PATH       PFX file (GRAPH_CLIENT_CERT_PASSWORD optional)
//   GRAPH_CLIENT_CERT_THUMBPRINT Windows cert store lookup, CurrentUser\My then
//                                LocalMachine\My
//
// When a certificate is configured, token requests authenticate with a
// client_assertion — an RS256-signed JWT carrying the x5t#S256 thumbprint
// header and aud = the tenant token endpoint — instead of client_secret
// (docs: RFC 7523 / Microsoft identity platform certificate credentials).
// Certificate wins when both cert and secret are configured. The chosen mode
// is logged at startup; key material never is.
//
// The assertion itself is built locally (ClientAssertionJwt) so its shape is
// unit-testable offline; Azure.Identity's ClientAssertionCredential handles
// the token endpoint exchange, caching and retries.

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Graph;

public static class GraphAuth
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    public const string CertPathEnvVar = "GRAPH_CLIENT_CERT_PATH";
    public const string CertPasswordEnvVar = "GRAPH_CLIENT_CERT_PASSWORD";
    public const string CertThumbprintEnvVar = "GRAPH_CLIENT_CERT_THUMBPRINT";

    /// <summary>True when either certificate env var is set (cert wins over secret).</summary>
    internal static bool CertificateConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CertPathEnvVar)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CertThumbprintEnvVar));

    // The mode is logged once per process ("at startup"), not once per
    // GraphClient — commands and shards construct several clients per run.
    private static int _modeLogged;

    private static void LogModeOnce(string message)
    {
        if (Interlocked.Exchange(ref _modeLogged, 1) == 0)
            Logger.Info(message);
    }

    /// <summary>
    /// Build the credential for Graph token requests. Certificate mode when
    /// configured (fail-fast on a broken cert config, naming the setting);
    /// otherwise the unchanged <see cref="DefaultAzureCredential"/> chain.
    /// </summary>
    public static TokenCredential CreateCredential()
    {
        if (!CertificateConfigured)
        {
            LogModeOnce("Graph auth mode: default credential chain (client secret / managed identity / CLI)");
            return new DefaultAzureCredential();
        }

        var tenantId = RequireEnvForCert("AZURE_TENANT_ID");
        var clientId = RequireEnvForCert("AZURE_CLIENT_ID");
        var certificate = LoadConfiguredCertificate(out var sourceDescription);
        LogModeOnce(
            $"Graph auth mode: client certificate ({sourceDescription}, subject '{certificate.Subject}', " +
            $"expires {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}) — client_assertion flow; " +
            "client secret ignored");

        // ClientAssertionCredential POSTs our signed JWT to the token endpoint.
        // AZURE_AUTHORITY_HOST (sovereign clouds) is honored by both the
        // credential and the assertion audience below.
        return new ClientAssertionCredential(
            tenantId,
            clientId,
            () => ClientAssertionJwt.Build(certificate, tenantId, clientId, AuthorityHost()));
    }

    private static string RequireEnvForCert(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Invalid configuration: {name} is required when {CertPathEnvVar}/{CertThumbprintEnvVar} is set");
        return value;
    }

    /// <summary>Authority host for the assertion audience (default public cloud).</summary>
    internal static string AuthorityHost()
    {
        var host = Environment.GetEnvironmentVariable("AZURE_AUTHORITY_HOST");
        return string.IsNullOrWhiteSpace(host)
            ? "https://login.microsoftonline.com"
            : host.Trim().TrimEnd('/');
    }

    /// <summary>
    /// Load the configured certificate. Path (PFX) wins over thumbprint when both
    /// are set. Every failure is fail-fast and names the env var; messages never
    /// include key material (paths/thumbprints only).
    /// </summary>
    internal static X509Certificate2 LoadConfiguredCertificate(out string sourceDescription)
    {
        var path = Environment.GetEnvironmentVariable(CertPathEnvVar);
        if (!string.IsNullOrWhiteSpace(path))
        {
            sourceDescription = $"{CertPathEnvVar}={path.Trim()}";
            return LoadPfx(path.Trim(), Environment.GetEnvironmentVariable(CertPasswordEnvVar));
        }

        var thumbprint = Environment.GetEnvironmentVariable(CertThumbprintEnvVar)!.Trim();
        sourceDescription = $"{CertThumbprintEnvVar}={thumbprint} (cert store My)";
        return FindInStores(thumbprint);
    }

    /// <summary>Load a PFX/PKCS#12 file (password optional).</summary>
    internal static X509Certificate2 LoadPfx(string path, string? password)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Invalid configuration: {CertPathEnvVar} file not found: '{path}'");
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (CryptographicException exc)
        {
            throw new InvalidOperationException(
                $"Invalid configuration: {CertPathEnvVar} file '{path}' could not be loaded " +
                $"(wrong {CertPasswordEnvVar}, or not a PFX?): {exc.Message}",
                exc);
        }
        return EnsurePrivateKey(certificate, $"{CertPathEnvVar} '{path}'");
    }

    /// <summary>CurrentUser\My first, then LocalMachine\My (Windows service accounts).</summary>
    internal static X509Certificate2 FindInStores(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
                if (matches.Count > 0)
                    return EnsurePrivateKey(matches[0], $"{CertThumbprintEnvVar} {normalized} ({location}\\My)");
            }
            catch (CryptographicException)
            {
                // Store missing/inaccessible on this platform — try the next one.
            }
        }
        throw new InvalidOperationException(
            $"Invalid configuration: no certificate with thumbprint {normalized} in CurrentUser\\My or " +
            $"LocalMachine\\My ({CertThumbprintEnvVar})");
    }

    private static X509Certificate2 EnsurePrivateKey(X509Certificate2 certificate, string source)
    {
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException(
                $"Invalid configuration: certificate from {source} has no private key — " +
                "the connector must be able to sign the client assertion");
        return certificate;
    }
}

/// <summary>
/// Builds the OAuth2 <c>client_assertion</c> JWT for certificate credentials:
/// RS256 signature, <c>x5t#S256</c> certificate binding, <c>aud</c> = the tenant
/// token endpoint, unique <c>jti</c>, and a short nbf/exp window.
/// </summary>
public static class ClientAssertionJwt
{
    /// <summary>Assertion validity window. Short-lived on purpose; a fresh one is built per token request.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Build and sign the assertion for <paramref name="certificate"/> (must hold an RSA private key).</summary>
    public static string Build(
        X509Certificate2 certificate,
        string tenantId,
        string clientId,
        string authorityHost,
        DateTimeOffset? now = null)
    {
        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException(
                "Certificate does not contain an RSA private key (RS256 signing requires RSA)");

        var issuedAt = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var header = new Dictionary<string, object>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            // x5t#S256: base64url SHA-256 of the DER certificate (RFC 7515 §4.1.8).
            ["x5t#S256"] = Base64Url(SHA256.HashData(certificate.RawData)),
        };
        var payload = new Dictionary<string, object>
        {
            ["aud"] = $"{authorityHost}/{tenantId}/oauth2/v2.0/token",
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = issuedAt,
            ["iat"] = issuedAt,
            ["exp"] = issuedAt + (long)Lifetime.TotalSeconds,
        };

        var signingInput =
            Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
            Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>RFC 7515 base64url: no padding, '-'/'_' alphabet.</summary>
    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
