// Infrastructure/SecretProvider.cs
// --------------------------------
// Resolves SECRET_* configuration values from either process environment
// variables (the default) or an Azure Key Vault (opt-in with USE_KEY_VAULT=true
// + KEY_VAULT_URI). The Key Vault secret name is derived from the env-var name:
// lowercased with '_' → '-' (Key Vault names allow only alphanumerics and
// hyphens), e.g. SECRET_HDFS_DELEGATION_TOKEN → secret-hdfs-delegation-token. On a
// fetch failure the lookup falls back to the environment variable if present.
// Successful vault fetches are cached for the process lifetime.

using System.Collections.Concurrent;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace HadoopConnector.Infrastructure;

public static class SecretProvider
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    private static readonly object ClientLock = new();
    private static volatile SecretClient? _client;

    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Test-only seam. When non-null, Key Vault fetches route through this
    /// delegate (argument = mapped vault secret name). A throwing delegate
    /// simulates a fetch failure.
    /// </summary>
    internal static Func<string, string?>? OverrideFetch;

    /// <summary>
    /// Return the value for <paramref name="envVarName"/>: a strict env-var
    /// pass-through unless USE_KEY_VAULT is truthy, in which case the value is
    /// resolved from Key Vault with env-var fallback on failure.
    /// </summary>
    public static string? GetSecret(string envVarName)
    {
        if (!EnvFlags.IsTrue("USE_KEY_VAULT"))
            return Environment.GetEnvironmentVariable(envVarName);

        // A missing vault URI is a configuration error, not a fetch failure —
        // it must surface rather than silently fall back.
        if (OverrideFetch is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEY_VAULT_URI")))
            throw new ArgumentException("Invalid configuration: Missing KEY_VAULT_URI");

        // Cache SUCCESSFUL fetches only: caching the failure-path fallback
        // would pin one transient vault hiccup for the process lifetime.
        if (Cache.TryGetValue(envVarName, out var cached))
            return cached;

        var (fetched, success) = FetchFromKeyVault(envVarName);
        if (success)
            Cache.TryAdd(envVarName, fetched);
        return fetched;
    }

    /// <summary>Map an env-var name to its Key Vault secret name: lowercase, '_'→'-'.</summary>
    internal static string ToSecretName(string envVarName) =>
        envVarName.ToLowerInvariant().Replace('_', '-');

    private static (string? Value, bool Success) FetchFromKeyVault(string envVarName)
    {
        var secretName = ToSecretName(envVarName);
        try
        {
            var fetch = OverrideFetch;
            if (fetch is not null)
                return (fetch(secretName), true);

            var client = GetClient();
            return (client.GetSecret(secretName).Value.Value, true);
        }
        catch (Exception ex)
        {
            var fallback = Environment.GetEnvironmentVariable(envVarName);
            if (fallback is not null)
            {
                Logger.Error(
                    $"Failed to fetch secret '{secretName}' from Key Vault; falling back to environment variable '{envVarName}'.",
                    ex);
                return (fallback, false);
            }

            Logger.Error(
                $"Failed to fetch secret '{secretName}' from Key Vault and no environment variable '{envVarName}' is set; returning null.",
                ex);
            return (null, false);
        }
    }

    private static SecretClient GetClient()
    {
        var client = _client;
        if (client is not null)
            return client;

        lock (ClientLock)
        {
            if (_client is not null)
                return _client;

            var uri = Environment.GetEnvironmentVariable("KEY_VAULT_URI");
            if (string.IsNullOrEmpty(uri))
                throw new ArgumentException("Invalid configuration: Missing KEY_VAULT_URI");

            _client = new SecretClient(new Uri(uri), new DefaultAzureCredential());
            return _client;
        }
    }

    /// <summary>Test-only reset seam.</summary>
    internal static void ResetForTests()
    {
        lock (ClientLock)
        {
            _client = null;
        }
        Cache.Clear();
        OverrideFetch = null;
    }
}
