// Infrastructure/SecretProvider.cs
// --------------------------------
// Resolution of SECRET_* values.
//
//   Default:            read from the process environment (populated from
//                       env/.env.local.user by the env loader).
//   USE_KEY_VAULT=true: SECRET_<NAME> resolves from Azure Key Vault
//                       (KEY_VAULT_URI required). The vault secret name is the
//                       env name lowered with '_' → '-' (SECRET_AAD_APP_CLIENT_SECRET
//                       → secret-aad-app-client-secret). Environment values still
//                       win when present, so local overrides remain possible.
//
// Resolved values are cached for the process lifetime.

using System.Collections.Concurrent;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace AltrataConnector.Infrastructure;

public interface ISecretProvider
{
    /// <summary>Resolve a SECRET_* variable; null when not found anywhere.</summary>
    string? Get(string envName);
}

public sealed class SecretProvider : ISecretProvider
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.secrets");

    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _keyVaultFetch;

    public SecretProvider(Func<string, string?>? keyVaultFetch = null)
    {
        _keyVaultFetch = keyVaultFetch ?? FetchFromKeyVault;
    }

    public static bool KeyVaultEnabled => EnvFlags.IsTrue("USE_KEY_VAULT");

    public static string? KeyVaultUri
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("KEY_VAULT_URI");
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
    }

    /// <summary>SECRET_AAD_APP_CLIENT_SECRET → secret-aad-app-client-secret.</summary>
    public static string ToVaultSecretName(string envName) =>
        envName.ToLowerInvariant().Replace('_', '-');

    public string? Get(string envName)
    {
        return _cache.GetOrAdd(envName, name =>
        {
            var fromEnv = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(fromEnv))
                return fromEnv;
            if (!KeyVaultEnabled)
                return null;
            return _keyVaultFetch(name);
        });
    }

    private static string? FetchFromKeyVault(string envName)
    {
        var uri = KeyVaultUri;
        if (uri == null)
        {
            Logger.Warning("USE_KEY_VAULT=true but KEY_VAULT_URI is not set");
            return null;
        }
        try
        {
            var client = new SecretClient(new Uri(uri), new DefaultAzureCredential());
            var secret = client.GetSecret(ToVaultSecretName(envName));
            return secret.Value.Value;
        }
        catch (Exception exc)
        {
            Logger.Warning($"Key Vault lookup failed for {envName}: {exc.Message}");
            return null;
        }
    }
}

/// <summary>Shared boolean env-flag parsing: true / 1 / yes (case-insensitive).</summary>
public static class EnvFlags
{
    public static bool IsTrue(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw != null &&
               (raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("1", StringComparison.Ordinal)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Explicitly false: false / 0 / no (case-insensitive). Used for
    /// default-ON flags where unset means enabled.</summary>
    public static bool IsFalse(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw != null &&
               (raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("0", StringComparison.Ordinal)
                || raw.Equals("no", StringComparison.OrdinalIgnoreCase));
    }
}
