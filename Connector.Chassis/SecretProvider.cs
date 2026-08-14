// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Connector.Chassis;

/// <summary>
/// Resolves <c>SECRET_*</c> configuration values from either process environment
/// variables (the default) or an Azure Key Vault (opt-in).
///
/// <para>
/// Off by default: when <c>USE_KEY_VAULT</c> is unset (or not truthy), <see cref="GetSecret"/>
/// is a strict pass-through to <see cref="Environment.GetEnvironmentVariable(string)"/> — behaviour,
/// logs, and return values are byte-identical to reading the env var directly.
/// </para>
///
/// <para>
/// When <c>USE_KEY_VAULT</c> is truthy (<c>true</c>/<c>1</c>/<c>yes</c>, case-insensitive — matching
/// the codebase's <c>BoolEnv</c> semantics), <c>KEY_VAULT_URI</c> must be set (otherwise an
/// <see cref="ArgumentException"/> naming it is thrown). Secrets are fetched from that vault using a
/// <see cref="DefaultAzureCredential"/> (supporting Managed Identity, environment credentials, Azure
/// CLI, etc.). The Key Vault secret name is derived from the requested environment variable name:
/// <b>lowercased, with every underscore (<c>_</c>) replaced by a hyphen (<c>-</c>)</b>, because Key
/// Vault secret names may only contain alphanumerics and hyphens. For example
/// <c>SECRET_AAD_APP_CLIENT_SECRET</c> maps to the vault secret <c>secret-aad-app-client-secret</c>,
/// and <c>SECRET_SEISMIC_CLIENT_SECRET</c> maps to <c>secret-seismic-client-secret</c>.
/// </para>
///
/// <para>
/// On a Key Vault fetch failure the error is logged and the lookup falls back to the environment
/// variable if present, otherwise returns <c>null</c>. The <see cref="SecretClient"/> and the resolved
/// values are cached (thread-safe) for the process lifetime.
/// </para>
/// </summary>
public static class SecretProvider
{
    private static readonly IAppLogger Logger = Chassis.GetLogger(Chassis.Identity.BaseLoggerName);

    private static readonly object ClientLock = new();
    private static volatile SecretClient? _client;

    /// <summary>Cache of resolved Key Vault values, keyed by the requested env-var name.</summary>
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Test-only seam. When non-null, Key Vault fetches are routed through this delegate instead of a
    /// real <see cref="SecretClient"/>; the argument is the mapped Key Vault secret name (e.g.
    /// <c>secret-aad-app-client-secret</c>). Lets tests exercise the Key Vault code path — including
    /// the name mapping and fallback-on-failure behaviour — without touching Azure. A delegate that
    /// throws simulates a fetch failure.
    /// </summary>
    internal static Func<string, string?>? OverrideFetch;

    /// <summary>
    /// Return the value for <paramref name="envVarName"/>.
    ///
    /// <para>
    /// When <c>USE_KEY_VAULT</c> is not truthy this is exactly
    /// <c>Environment.GetEnvironmentVariable(envVarName)</c>. When it is truthy the value is resolved
    /// from Key Vault (secret name = <paramref name="envVarName"/> lowercased, <c>_</c>→<c>-</c>),
    /// falling back to the environment variable on failure.
    /// </para>
    /// </summary>
    /// <param name="envVarName">The environment-variable name (e.g. <c>SECRET_SEISMIC_CLIENT_SECRET</c>).</param>
    /// <returns>The resolved secret value, or <c>null</c> if it cannot be found.</returns>
    public static string? GetSecret(string envVarName)
    {
        if (!UseKeyVault())
            return Environment.GetEnvironmentVariable(envVarName);

        // Requiring the vault URI is a configuration error, not a fetch failure: it must surface
        // (naming the var) rather than fall back. Validate it here, outside FetchFromKeyVault's
        // try/catch. A live SecretClient still needs it; a test-injected OverrideFetch does not,
        // so only enforce it when there is no override.
        if (OverrideFetch is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEY_VAULT_URI")))
            throw new ArgumentException("Invalid configuration: Missing KEY_VAULT_URI");

        // Cache SUCCESSFUL fetches only: caching the failure-path fallback (or null)
        // would pin one transient Key Vault hiccup for the process lifetime — fatal
        // in --continuous/service mode, where the next cycle should retry the vault.
        if (Cache.TryGetValue(envVarName, out var cached))
            return cached;

        var (fetched, success) = FetchFromKeyVault(envVarName);
        if (success)
            Cache.TryAdd(envVarName, fetched);
        return fetched;
    }

    /// <summary>Map an env-var name to its Key Vault secret name: lowercase, <c>_</c>→<c>-</c>.</summary>
    /// <remarks>
    /// Key Vault secret names allow only alphanumeric characters and hyphens, so underscores — legal
    /// in environment variable names — must be translated.
    /// </remarks>
    internal static string ToSecretName(string envVarName) =>
        envVarName.ToLowerInvariant().Replace('_', '-');

    /// <returns>The value plus whether it came from a successful vault fetch
    /// (only successes are cache-eligible; fallbacks must be re-attempted next call).</returns>
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

    /// <summary>Whether Key Vault resolution is enabled.</summary>
    /// <remarks>
    /// Delegates to <see cref="EnvFlags.IsTrue"/> so the truthy vocabulary is
    /// defined once. The local copy this replaces did not trim, so
    /// USE_KEY_VAULT with a trailing space fell back to environment-variable
    /// secrets without a word — the opposite of the intended posture, and
    /// invisible until someone audited where secrets were actually coming from.
    /// </remarks>
    private static bool UseKeyVault() => EnvFlags.IsTrue("USE_KEY_VAULT");

    /// <summary>Lazily construct (and cache) the shared <see cref="SecretClient"/>. Thread-safe.</summary>
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

    /// <summary>
    /// Test-only reset seam: clears the cached client and resolved-value cache and removes any
    /// injected <see cref="OverrideFetch"/>, so a subsequent test starts from a clean state.
    /// </summary>
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
