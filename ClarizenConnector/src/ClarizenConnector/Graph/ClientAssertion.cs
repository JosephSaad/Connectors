// Graph/ClientAssertion.cs
// ------------------------
// Certificate credential for the Graph client-credentials flow: builds the
// signed client_assertion JWT (RS256) Entra ID expects instead of a client
// secret, and resolves the signing certificate from config.
//
//   GRAPH_CLIENT_CERT_PATH        PFX/PKCS#12 or PEM (cert + key) file
//   GRAPH_CLIENT_CERT_PASSWORD    password for an encrypted PFX/PEM (optional)
//   GRAPH_CLIENT_CERT_THUMBPRINT  Windows certificate store lookup
//                                 (CurrentUser\My, then LocalMachine\My)
//
// The certificate credential WINS over SECRET_AAD_APP_CLIENT_SECRET when both
// are configured; between the two cert sources, PATH wins over THUMBPRINT.
// Assertion shape (offline-tested):
//   header  {alg: RS256, typ: JWT, x5t#S256: base64url(SHA-256(cert DER))}
//   payload {aud: token endpoint, iss/sub: client id, jti: random GUID,
//            nbf: now-60s, iat: now, exp: now+10min}
// Clarizen-side session auth is unrelated and unchanged.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Config;

namespace ClarizenConnector.Graph;

public static class ClientAssertion
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Assertion lifetime; short-lived by design (one per token request).</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Build the signed RS256 client_assertion for <paramref name="clientId"/>
    /// against <paramref name="tokenEndpoint"/>. <paramref name="now"/> and
    /// <paramref name="jti"/> are test seams.
    /// </summary>
    public static string Build(
        X509Certificate2 certificate,
        string clientId,
        string tokenEndpoint,
        DateTimeOffset? now = null,
        string? jti = null)
    {
        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException(
                "The configured Graph client certificate has no RSA private key "
                + "(an RSA certificate with its private key is required for RS256).");

        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var header = new JsonObject
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            // x5t#S256: base64url SHA-256 of the DER-encoded certificate.
            ["x5t#S256"] = Base64Url.EncodeToString(SHA256.HashData(certificate.RawData)),
        };
        var payload = new JsonObject
        {
            ["aud"] = tokenEndpoint,
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = jti ?? Guid.NewGuid().ToString(),
            ["nbf"] = issuedAt.AddSeconds(-60).ToUnixTimeSeconds(),
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = issuedAt.Add(Lifetime).ToUnixTimeSeconds(),
        };

        var signingInput =
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString()))
            + "."
            + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    // ── Certificate resolution ───────────────────────────────────────────────

    /// <summary>Resolve the configured certificate: PATH wins over THUMBPRINT.</summary>
    public static X509Certificate2 Resolve(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.GraphClientCertPath))
            return LoadFromPath(config.GraphClientCertPath!, config.GraphClientCertPassword);
        if (!string.IsNullOrWhiteSpace(config.GraphClientCertThumbprint))
            return LoadFromStore(config.GraphClientCertThumbprint!);
        throw new InvalidOperationException(
            "No Graph client certificate configured "
            + "(GRAPH_CLIENT_CERT_PATH or GRAPH_CLIENT_CERT_THUMBPRINT).");
    }

    /// <summary>Load a PFX/PKCS#12 or PEM (cert + key) certificate file.
    /// Failures name GRAPH_CLIENT_CERT_PATH; the error never includes key material.</summary>
    internal static X509Certificate2 LoadFromPath(string path, string? password)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException(
                $"Invalid configuration: GRAPH_CLIENT_CERT_PATH '{path}' does not exist.");
        }
        try
        {
            if (LooksLikePem(path))
            {
                return string.IsNullOrEmpty(password)
                    ? X509Certificate2.CreateFromPemFile(path)
                    : X509Certificate2.CreateFromEncryptedPemFile(path, password);
            }
            // DefaultKeySet: EphemeralKeySet is unsupported on macOS and the
            // default behaves correctly on every platform the connector targets.
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (Exception exc)
        {
            throw new ArgumentException(
                $"Invalid configuration: GRAPH_CLIENT_CERT_PATH '{path}' could not be loaded "
                + $"({exc.GetType().Name}: {exc.Message}). Provide a PFX/PKCS#12 or a PEM "
                + "containing the certificate and its private key "
                + "(GRAPH_CLIENT_CERT_PASSWORD for encrypted files).");
        }
    }

    private static bool LooksLikePem(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[256];
        var read = reader.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, Math.Max(read, 0)).Contains("-----BEGIN", StringComparison.Ordinal);
    }

    /// <summary>Find the certificate by thumbprint in the Windows store
    /// (CurrentUser\My, then LocalMachine\My). Windows-only by nature.</summary>
    internal static X509Certificate2 LoadFromStore(string thumbprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ArgumentException(
                "Invalid configuration: GRAPH_CLIENT_CERT_THUMBPRINT requires the Windows "
                + "certificate store — use GRAPH_CLIENT_CERT_PATH on this platform.");
        }

        var normalized = thumbprint.Replace(" ", string.Empty).Replace(":", string.Empty);
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException)
            {
                continue;  // store absent/inaccessible — try the next location
            }
            var matches = store.Certificates.Find(
                X509FindType.FindByThumbprint, normalized, validOnly: false);
            if (matches.Count > 0)
                return matches[0];
        }
        throw new ArgumentException(
            $"Invalid configuration: GRAPH_CLIENT_CERT_THUMBPRINT '{thumbprint}' was not found "
            + @"in CurrentUser\My or LocalMachine\My.");
    }
}
