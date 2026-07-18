// Graph/ClientAssertion.cs
// ------------------------
// Certificate (client-assertion) credential for the Graph token flow.
//
// Enterprise tenants commonly forbid client secrets on production app
// registrations. With a certificate configured the connector builds a signed
// JWT (RFC 7523 client_assertion) per token request instead of sending
// SECRET_AAD_APP_CLIENT_SECRET:
//
//   GRAPH_CLIENT_CERT_PATH        PFX/PKCS#12 (or PEM cert+key) file
//   GRAPH_CLIENT_CERT_PASSWORD    PFX password (SECRET-style; Key Vault-able)
//   GRAPH_CLIENT_CERT_THUMBPRINT  Windows cert store lookup (CurrentUser\My,
//                                 then LocalMachine\My) — the usual shape on
//                                 Windows Server where the key never touches disk
//
// The assertion is exactly what Entra ID validates: header {alg:RS256, typ:JWT,
// x5t#S256:<base64url(SHA-256(cert DER))>}, claims {aud:<token endpoint>,
// iss/sub:<client id>, jti:<unique>, nbf:<now>, exp:<now+10m>}, signed
// RSA-PKCS#1-SHA256 with the certificate's private key. SHA-256 throughout —
// the legacy SHA-1 x5t header is deliberately not emitted (FIPS posture).
//
// When BOTH a certificate and a client secret are configured the certificate
// wins; only the MODE is ever logged, never key material.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Graph;

public static class ClientAssertion
{
    /// <summary>Assertion lifetime — short-lived by design; a fresh one is built per token request.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>
    /// Build the signed client-assertion JWT for <paramref name="clientId"/>
    /// against <paramref name="tokenEndpoint"/> (the aud claim is the token
    /// endpoint itself, per Entra ID's validation rules).
    /// </summary>
    public static string Build(
        X509Certificate2 certificate,
        string clientId,
        string tokenEndpoint,
        DateTimeOffset? nowUtc = null,
        string? jti = null)
    {
        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new ArgumentException(
                "Invalid configuration: the Graph client certificate has no RSA private key "
                + "(GRAPH_CLIENT_CERT_PATH / GRAPH_CLIENT_CERT_THUMBPRINT).");

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var header = new JsonObject
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            // SHA-256 certificate thumbprint claim (never the legacy SHA-1 x5t).
            ["x5t#S256"] = Base64Url(SHA256.HashData(certificate.RawData)),
        };
        var payload = new JsonObject
        {
            ["aud"] = tokenEndpoint,
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = jti ?? Guid.NewGuid().ToString(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(Lifetime).ToUnixTimeSeconds(),
        };

        var signingInput =
            Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()))
            + "."
            + Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>RFC 7515 base64url: '+'→'-', '/'→'_', no padding.</summary>
    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Certificate resolution ───────────────────────────────────────────────

    /// <summary>
    /// Load the configured Graph client certificate: file path first
    /// (GRAPH_CLIENT_CERT_PATH — PFX, or PEM when the file starts with a PEM
    /// header), otherwise Windows store by thumbprint. Every failure names the
    /// setting; returns null only when neither knob is set.
    /// </summary>
    public static X509Certificate2? LoadConfigured(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.GraphClientCertPath))
            return LoadFromFile(config.GraphClientCertPath!, config.GraphClientCertPassword);
        if (!string.IsNullOrWhiteSpace(config.GraphClientCertThumbprint))
            return LoadFromStore(config.GraphClientCertThumbprint!);
        return null;
    }

    internal static X509Certificate2 LoadFromFile(string path, string? password)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException(
                $"Invalid configuration: GRAPH_CLIENT_CERT_PATH '{path}' does not exist.");
        }
        try
        {
            if (IsPemFile(path))
                return X509Certificate2.CreateFromPemFile(path);
            // Ephemeral keys keep the private key out of the OS key store; the
            // macOS keychain PAL cannot do that, so dev machines fall back to
            // the default (Windows Server / Linux prod hosts stay ephemeral).
            var flags = OperatingSystem.IsMacOS()
                ? X509KeyStorageFlags.DefaultKeySet
                : X509KeyStorageFlags.EphemeralKeySet;
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
        }
        catch (Exception exc) when (exc is not ArgumentException)
        {
            throw new ArgumentException(
                $"Invalid configuration: GRAPH_CLIENT_CERT_PATH '{path}' could not be loaded "
                + $"({exc.GetType().Name}: {exc.Message}). For a password-protected PFX set "
                + "GRAPH_CLIENT_CERT_PASSWORD (SECRET-style; Key Vault supported).");
        }
    }

    private static bool IsPemFile(string path)
    {
        // Sniff the content, not the extension — ops teams ship .crt/.cer PEMs.
        using var reader = new StreamReader(path);
        Span<char> head = stackalloc char[64];
        var read = reader.ReadBlock(head);
        return head[..read].ToString().Contains("-----BEGIN", StringComparison.Ordinal);
    }

    internal static X509Certificate2 LoadFromStore(string thumbprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ArgumentException(
                "Invalid configuration: GRAPH_CLIENT_CERT_THUMBPRINT requires the Windows "
                + "certificate store; use GRAPH_CLIENT_CERT_PATH on this platform.");
        }
        var normalized = thumbprint.Replace(" ", string.Empty).ToUpperInvariant();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint, normalized, validOnly: false);
                if (matches.Count > 0)
                    return matches[0];
            }
            catch (Exception exc) when (exc is not ArgumentException)
            {
                Logging.GetLogger("hadoop_connector.graph").Warning(
                    $"Certificate store {location}\\My could not be searched "
                    + $"({exc.GetType().Name}: {exc.Message}); trying the next location.");
            }
        }
        throw new ArgumentException(
            $"Invalid configuration: GRAPH_CLIENT_CERT_THUMBPRINT '{normalized}' was not found in "
            + "CurrentUser\\My or LocalMachine\\My. Confirm the certificate is installed WITH its "
            + "private key and that the service account can read it.");
    }
}
