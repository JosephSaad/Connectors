// Graph/CertificateCredential.cs
// ------------------------------
// Certificate-based Graph authentication (client_assertion) as the enterprise
// alternative to the client secret. Entra app certificates rotate on longer
// cycles, never transit config files as secret VALUES, and satisfy "no shared
// secrets" policies.
//
//   GRAPH_CLIENT_CERT_PATH        PFX/P12 (with GRAPH_CLIENT_CERT_PASSWORD) or
//                                 PEM containing certificate + private key.
//   GRAPH_CLIENT_CERT_THUMBPRINT  load from the CurrentUser/LocalMachine "My"
//                                 store instead (Windows service deployments;
//                                 the key never touches disk as a file).
//
// Precedence: certificate WINS over SECRET_AAD_APP_CLIENT_SECRET when both are
// configured; PATH wins over THUMBPRINT. The active auth MODE is logged once
// (mode + public thumbprint only — never key material, never the assertion).
//
// The client assertion is a standard AAD RS256 JWT:
//   header  { alg: RS256, typ: JWT, x5t#S256: base64url(SHA-256(cert DER)) }
//   claims  { aud: <token endpoint>, iss/sub: <client id>, jti: <unique>,
//             nbf/iat: now (60 s skew grace), exp: now + 9 min }
// signed with the certificate's RSA private key (RSASSA-PKCS1-v1_5 + SHA-256 —
// FIPS-approved; see docs/THREAT_MODEL.md).

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AltrataConnector.Config;

namespace AltrataConnector.Graph;

public static class CertificateCredential
{
    public const string CertPathEnvVar = "GRAPH_CLIENT_CERT_PATH";
    public const string CertPasswordEnvVar = "GRAPH_CLIENT_CERT_PASSWORD";
    public const string ThumbprintEnvVar = "GRAPH_CLIENT_CERT_THUMBPRINT";

    public const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Assertion lifetime (AAD accepts up to 10 minutes; stay under).</summary>
    internal static readonly TimeSpan AssertionLifetime = TimeSpan.FromMinutes(9);

    /// <summary>Clock-skew grace applied to nbf.</summary>
    internal static readonly TimeSpan NotBeforeSkew = TimeSpan.FromSeconds(60);

    private static string? CertPath =>
        Trimmed(Environment.GetEnvironmentVariable(CertPathEnvVar));

    private static string? Thumbprint =>
        Trimmed(Environment.GetEnvironmentVariable(ThumbprintEnvVar));

    private static string? Trimmed(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>True when a certificate credential is configured — it then WINS
    /// over the client secret.</summary>
    public static bool Configured => CertPath != null || Thumbprint != null;

    /// <summary>Auth-mode description for the one-time log line (mode only —
    /// never key material).</summary>
    public static string ModeDescription =>
        CertPath != null ? $"certificate ({CertPathEnvVar})"
        : Thumbprint != null ? $"certificate ({ThumbprintEnvVar}, machine/user store)"
        : "client secret";

    /// <summary>
    /// Load the configured certificate (with private key). Fails fast with a
    /// ConfigurationError naming the setting on every bad input: missing file,
    /// wrong password, PEM without a key, unknown thumbprint, non-RSA key.
    /// </summary>
    public static X509Certificate2 Load()
    {
        var path = CertPath;
        if (path != null)
            return Validate(LoadFromFile(path), CertPathEnvVar, path);

        var thumbprint = Thumbprint
            ?? throw new ConfigurationError(
                $"Certificate credential not configured — set {CertPathEnvVar} or {ThumbprintEnvVar}");
        return Validate(LoadFromStore(thumbprint), ThumbprintEnvVar, thumbprint);
    }

    private static X509Certificate2 LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new ConfigurationError($"{CertPathEnvVar} '{path}' does not exist");
        var password = Environment.GetEnvironmentVariable(CertPasswordEnvVar);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            // Ephemeral keys (never persisted to a key store) where the
            // platform supports it; macOS requires the default behaviour.
            var flags = OperatingSystem.IsMacOS()
                ? X509KeyStorageFlags.DefaultKeySet
                : X509KeyStorageFlags.EphemeralKeySet;
            return extension is ".pfx" or ".p12"
                ? X509CertificateLoader.LoadPkcs12FromFile(path, password, flags)
                : X509Certificate2.CreateFromPemFile(path);
        }
        catch (Exception exc) when (exc is CryptographicException or ArgumentException or IOException)
        {
            throw new ConfigurationError(
                $"{CertPathEnvVar} '{path}' could not be loaded as a client certificate " +
                $"({exc.GetType().Name}: {exc.Message}). PFX/P12 needs {CertPasswordEnvVar} when " +
                "password-protected; PEM must contain the certificate AND its private key.");
        }
    }

    private static X509Certificate2 LoadFromStore(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", "").ToUpperInvariant();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
                if (found.Count > 0)
                    return found[0];
            }
            catch (CryptographicException)
            {
                // Store unavailable at this location (common off-Windows) — try the next.
            }
        }
        throw new ConfigurationError(
            $"{ThumbprintEnvVar} '{thumbprint}' was not found in the CurrentUser or LocalMachine " +
            "'My' certificate store (service deployments usually install to LocalMachine\\My; " +
            "grant the service account read access to the private key)");
    }

    private static X509Certificate2 Validate(X509Certificate2 cert, string envVar, string value)
    {
        if (!cert.HasPrivateKey)
            throw new ConfigurationError(
                $"{envVar} '{value}' resolved certificate {cert.Thumbprint} has NO private key — " +
                "the client assertion cannot be signed");
        if (cert.GetRSAPrivateKey() == null)
            throw new ConfigurationError(
                $"{envVar} '{value}' resolved certificate {cert.Thumbprint} does not carry an RSA " +
                "private key — the AAD client assertion is RS256 (RSA + SHA-256)");
        return cert;
    }

    /// <summary>
    /// Build the signed client assertion for the token request. Deterministic
    /// given (cert, clientId, audience, utcNow, jti) — offline-testable.
    /// </summary>
    public static string BuildClientAssertion(X509Certificate2 cert, string clientId,
        string audience, DateTime? utcNow = null, string? jti = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var header = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            // x5t#S256: base64url SHA-256 of the DER certificate — how AAD
            // matches the assertion to the registered certificate.
            ["x5t#S256"] = Base64Url(SHA256.HashData(cert.RawData)),
        });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
            ["nbf"] = ToUnixSeconds(now - NotBeforeSkew),
            ["iat"] = ToUnixSeconds(now),
            ["exp"] = ToUnixSeconds(now + AssertionLifetime),
        });

        var signingInput = $"{Base64Url(Encoding.UTF8.GetBytes(header))}.{Base64Url(Encoding.UTF8.GetBytes(payload))}";
        using var rsa = cert.GetRSAPrivateKey()
            ?? throw new ConfigurationError(
                $"Certificate {cert.Thumbprint} does not carry an RSA private key (RS256 required)");
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    internal static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
