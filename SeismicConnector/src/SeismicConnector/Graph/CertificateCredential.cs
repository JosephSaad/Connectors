// Graph/CertificateCredential.cs
// ------------------------------
// Certificate (client_assertion) credential for the Graph client-credentials
// flow — the enterprise alternative to SECRET_AAD_APP_CLIENT_SECRET
// (SECURITY.md documents rotation; docs/DEPLOYMENT_ENTERPRISE.md the rollout).
//
//   GRAPH_CLIENT_CERT_PATH        PFX/PKCS#12 or PEM file with the private key
//   GRAPH_CLIENT_CERT_PASSWORD    optional password (PFX / encrypted PEM);
//                                 resolvable via Key Vault like any secret
//   GRAPH_CLIENT_CERT_THUMBPRINT  alternative: hex thumbprint looked up in the
//                                 CurrentUser then LocalMachine "My" store
//                                 (Windows service deployments)
//
// When a certificate is configured it WINS over the client secret. The token
// request then carries client_assertion_type=jwt-bearer plus a self-signed
// RS256 JWT per RFC 7523 / Microsoft identity platform certificate
// credentials: header {alg,typ,x5t#S256}, claims {aud,iss,sub,jti,nbf,exp,iat}.
// Only the auth MODE is ever logged — never key material or the assertion.
//
// Misconfiguration (missing file, unknown thumbprint, cert without a private
// key) fails fast with ConfigException naming the setting.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Seismic;

namespace SeismicConnector.Graph;

public static class CertificateCredential
{
    public const string CertPathEnvVar = "GRAPH_CLIENT_CERT_PATH";
    public const string CertPasswordEnvVar = "GRAPH_CLIENT_CERT_PASSWORD";
    public const string CertThumbprintEnvVar = "GRAPH_CLIENT_CERT_THUMBPRINT";

    /// <summary>client_assertion_type value for the JWT bearer flow.</summary>
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Assertion lifetime. Microsoft identity platform accepts up to 10 minutes.</summary>
    internal static readonly TimeSpan AssertionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Clock-skew allowance applied backwards to nbf/iat.</summary>
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>True when the settings request certificate auth (path or thumbprint).</summary>
    public static bool IsConfigured(GraphSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ClientCertPath)
        || !string.IsNullOrWhiteSpace(settings.ClientCertThumbprint);

    /// <summary>
    /// Load the configured certificate, or null when certificate auth is not
    /// configured. Path wins over thumbprint when both are set. Throws
    /// <see cref="ConfigException"/> naming the setting on any failure.
    /// </summary>
    public static X509Certificate2? Load(GraphSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ClientCertPath))
            return LoadFromFile(settings.ClientCertPath!.Trim(), settings.ClientCertPassword);
        if (!string.IsNullOrWhiteSpace(settings.ClientCertThumbprint))
            return LoadFromStore(settings.ClientCertThumbprint!.Trim());
        return null;
    }

    private static X509Certificate2 LoadFromFile(string path, string? password)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException(
                $"Invalid configuration: {CertPathEnvVar} file not found: {path}");
        }
        // Ephemeral keys keep the private key out of persisted key stores;
        // macOS (dev machines) does not support the flag, so fall back there.
        var storageFlags = OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
        X509Certificate2 certificate;
        try
        {
            certificate = LooksLikePem(path)
                ? LoadPem(path, password)
                : X509CertificateLoader.LoadPkcs12FromFile(path, password, storageFlags);
        }
        catch (CryptographicException ex)
        {
            throw new ConfigException(
                $"Invalid configuration: {CertPathEnvVar} could not be loaded ({path}): {ex.Message}");
        }
        return RequirePrivateKey(certificate, CertPathEnvVar, path);
    }

    private static bool LooksLikePem(string path)
    {
        using var reader = new StreamReader(path);
        Span<char> head = stackalloc char[64];
        var read = reader.Read(head);
        return head[..Math.Max(read, 0)].ToString().Contains("-----BEGIN", StringComparison.Ordinal);
    }

    private static X509Certificate2 LoadPem(string path, string? password) =>
        string.IsNullOrEmpty(password)
            ? X509Certificate2.CreateFromPemFile(path)
            : X509Certificate2.CreateFromEncryptedPemFile(path, password);

    private static X509Certificate2 LoadFromStore(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", "", StringComparison.Ordinal);
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint, normalized, validOnly: false);
                if (matches.Count > 0)
                    return RequirePrivateKey(matches[0], CertThumbprintEnvVar, normalized);
            }
            catch (CryptographicException)
            {
                // Store unavailable on this platform/location — try the next.
            }
        }
        throw new ConfigException(
            $"Invalid configuration: {CertThumbprintEnvVar} '{normalized}' was not found in the "
            + "CurrentUser or LocalMachine 'My' certificate store.");
    }

    private static X509Certificate2 RequirePrivateKey(
        X509Certificate2 certificate, string setting, string source)
    {
        if (!certificate.HasPrivateKey)
        {
            throw new ConfigException(
                $"Invalid configuration: {setting} certificate has no private key ({source}). "
                + "Provide a PFX or PEM that includes the key.");
        }
        return certificate;
    }

    /// <summary>
    /// Build the signed RS256 client assertion for <paramref name="tokenEndpoint"/>.
    /// Header carries x5t#S256 (base64url SHA-256 of the certificate DER);
    /// claims: aud = token endpoint, iss = sub = client id, fresh jti,
    /// nbf/iat = now − skew, exp = now + lifetime.
    /// </summary>
    public static string BuildAssertion(
        X509Certificate2 certificate, string clientId, string tokenEndpoint, DateTimeOffset? now = null)
    {
        var rsa = certificate.GetRSAPrivateKey()
            ?? throw new ConfigException(
                "Invalid configuration: Graph client certificate does not carry an RSA private key "
                + "(only RSA/RS256 certificate credentials are supported).");

        var issuedAt = (now ?? DateTimeOffset.UtcNow) - ClockSkew;
        var header = new JsonObject
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["x5t#S256"] = Base64Url(SHA256.HashData(certificate.RawData)),
        };
        var claims = new JsonObject
        {
            ["aud"] = tokenEndpoint,
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = issuedAt.ToUnixTimeSeconds(),
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = (issuedAt + ClockSkew + AssertionLifetime).ToUnixTimeSeconds(),
        };

        var signingInput =
            Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()))
            + "."
            + Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>RFC 7515 base64url (no padding).</summary>
    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
