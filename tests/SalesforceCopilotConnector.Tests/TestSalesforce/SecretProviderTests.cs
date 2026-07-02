// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

/// <summary>
/// Tests for <see cref="SecretProvider"/> — Key Vault / env-var secret resolution (#7).
///
/// Joins the shared "EnvVars" collection because it mutates process-global environment
/// variables and the process-global <see cref="SecretProvider"/> seams; every mutation is
/// saved and restored.
/// </summary>
[Collection("EnvVars")]
public class SecretProviderTests
{
    /// <summary>
    /// Snapshot + restore the env vars and <see cref="SecretProvider"/> static seams this suite
    /// touches, so tests never leak state into each other or into <see cref="SettingsTests"/>.
    /// </summary>
    private sealed class SecretEnvScope : IDisposable
    {
        private static readonly string[] Names =
        {
            "USE_KEY_VAULT",
            "KEY_VAULT_URI",
            "SECRET_AAD_APP_CLIENT_SECRET",
            "SECRET_SALESFORCE_CLIENT_SECRET",
        };

        private readonly Dictionary<string, string?> _saved = new();

        public SecretEnvScope()
        {
            foreach (var name in Names)
            {
                _saved[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
            SecretProvider.ResetForTests();
        }

        public void Dispose()
        {
            SecretProvider.ResetForTests();
            foreach (var (name, value) in _saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    // ── USE_KEY_VAULT unset → pure env pass-through (parity) ───────────────────

    [Fact]
    public void GetSecretReadsEnvWhenKeyVaultDisabled()
    {
        using var scope = new SecretEnvScope();
        Environment.SetEnvironmentVariable("SECRET_SALESFORCE_CLIENT_SECRET", "env-value");

        Assert.Equal("env-value", SecretProvider.GetSecret("SECRET_SALESFORCE_CLIENT_SECRET"));
    }

    [Fact]
    public void GetSecretReturnsNullWhenEnvMissingAndKeyVaultDisabled()
    {
        using var scope = new SecretEnvScope();

        // Exact parity with Environment.GetEnvironmentVariable for an unset variable.
        Assert.Null(SecretProvider.GetSecret("SECRET_SALESFORCE_CLIENT_SECRET"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    public void GetSecretTreatsNonTruthyUseKeyVaultAsDisabled(string useKeyVault)
    {
        using var scope = new SecretEnvScope();
        Environment.SetEnvironmentVariable("USE_KEY_VAULT", useKeyVault);
        Environment.SetEnvironmentVariable("SECRET_AAD_APP_CLIENT_SECRET", "env-value");
        // A non-truthy flag must NOT require KEY_VAULT_URI and must read the env var.
        var thrower = new Func<string, string?>(_ => throw new InvalidOperationException("should not fetch"));
        SecretProvider.OverrideFetch = thrower;

        Assert.Equal("env-value", SecretProvider.GetSecret("SECRET_AAD_APP_CLIENT_SECRET"));
    }

    // ── USE_KEY_VAULT=true, missing KEY_VAULT_URI → throws naming the var ───────

    [Fact]
    public void GetSecretThrowsWhenKeyVaultUriMissing()
    {
        using var scope = new SecretEnvScope();
        Environment.SetEnvironmentVariable("USE_KEY_VAULT", "true");
        // No KEY_VAULT_URI, no OverrideFetch → configuration error must surface.

        var ex = Assert.Throws<ArgumentException>(
            () => SecretProvider.GetSecret("SECRET_SALESFORCE_CLIENT_SECRET"));
        Assert.Contains("KEY_VAULT_URI", ex.Message);
    }

    // ── name mapping via injected fake fetch ───────────────────────────────────

    [Fact]
    public void GetSecretMapsEnvNameToKeyVaultSecretName()
    {
        using var scope = new SecretEnvScope();
        Environment.SetEnvironmentVariable("USE_KEY_VAULT", "true");
        Environment.SetEnvironmentVariable("KEY_VAULT_URI", "https://example.vault.azure.net/");

        string? requestedName = null;
        SecretProvider.OverrideFetch = name =>
        {
            requestedName = name;
            return "kv-value";
        };

        var result = SecretProvider.GetSecret("SECRET_AAD_APP_CLIENT_SECRET");

        Assert.Equal("secret-aad-app-client-secret", requestedName);
        Assert.Equal("kv-value", result);
    }

    [Fact]
    public void ToSecretNameLowercasesAndReplacesUnderscores()
    {
        Assert.Equal("secret-salesforce-client-secret",
            SecretProvider.ToSecretName("SECRET_SALESFORCE_CLIENT_SECRET"));
        Assert.Equal("secret-aad-app-client-secret",
            SecretProvider.ToSecretName("SECRET_AAD_APP_CLIENT_SECRET"));
    }

    // ── KV value returned when set ─────────────────────────────────────────────

    [Fact]
    public void GetSecretReturnsKeyVaultValueWhenSet()
    {
        using var scope = new SecretEnvScope();
        Environment.SetEnvironmentVariable("USE_KEY_VAULT", "true");
        Environment.SetEnvironmentVariable("KEY_VAULT_URI", "https://example.vault.azure.net/");
        // Env var also set to a different value — Key Vault must win when enabled.
        Environment.SetEnvironmentVariable("SECRET_SALESFORCE_CLIENT_SECRET", "env-value");
        SecretProvider.OverrideFetch = _ => "kv-value";

        Assert.Equal("kv-value", SecretProvider.GetSecret("SECRET_SALESFORCE_CLIENT_SECRET"));
    }

    // ── fallback to env on fetch failure ───────────────────────────────────────

    [Fact]
    public void GetSecretFallsBackToEnvWhenFetchFails()
    {
        using var scope = new SecretEnvScope();
        Environment.SetEnvironmentVariable("USE_KEY_VAULT", "true");
        Environment.SetEnvironmentVariable("KEY_VAULT_URI", "https://example.vault.azure.net/");
        Environment.SetEnvironmentVariable("SECRET_SALESFORCE_CLIENT_SECRET", "env-fallback");
        SecretProvider.OverrideFetch = _ => throw new InvalidOperationException("boom");

        Assert.Equal("env-fallback", SecretProvider.GetSecret("SECRET_SALESFORCE_CLIENT_SECRET"));
    }

    [Fact]
    public void GetSecretReturnsNullWhenFetchFailsAndNoEnvFallback()
    {
        using var scope = new SecretEnvScope();
        Environment.SetEnvironmentVariable("USE_KEY_VAULT", "true");
        Environment.SetEnvironmentVariable("KEY_VAULT_URI", "https://example.vault.azure.net/");
        // No SECRET_* env var set → fetch failure yields null.
        SecretProvider.OverrideFetch = _ => throw new InvalidOperationException("boom");

        Assert.Null(SecretProvider.GetSecret("SECRET_SALESFORCE_CLIENT_SECRET"));
    }
}
