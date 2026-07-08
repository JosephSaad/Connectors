// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Unit tests for Infrastructure/SqlExecutor — the SQL resilience + connection
// hardening seam (improvements #1 and #8). These exercise the PURE seams only
// (transient classification, backoff computation, connection-string hardening,
// env-config parsing): no real SQL Server, no sleeps. The delay is verified by
// asserting the computed TimeSpan, and jitter is driven by an injected uniform
// sample rather than the RNG. The SQL integration tests (SqlStateStoreTests,
// SqlServerIdentityStoreTests) continue to self-skip without a server.

using Microsoft.Data.SqlClient;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests;

// ── Transient classification (pure) ──────────────────────────────────────────

public class SqlExecutorClassificationTests
{
    [Theory]
    // AG failover / not-accepting-connections, throttling, deadlock, timeout, transport.
    [InlineData(40197)]
    [InlineData(40501)]
    [InlineData(40613)]
    [InlineData(49918)]
    [InlineData(49919)]
    [InlineData(49920)]
    [InlineData(10928)]
    [InlineData(10929)]
    [InlineData(1205)]
    [InlineData(233)]
    [InlineData(64)]
    [InlineData(-2)]
    [InlineData(20)]
    [InlineData(4060)]
    [InlineData(4221)]
    public void KnownTransientNumbersAreTransient(int number)
    {
        Assert.True(SqlExecutor.IsTransientNumber(number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(18456)]  // login failed — NOT transient, must not retry
    [InlineData(2627)]   // PK violation — NOT transient
    [InlineData(547)]    // FK/constraint — NOT transient
    [InlineData(207)]    // invalid column name — NOT transient
    public void OtherNumbersAreNotTransient(int number)
    {
        Assert.False(SqlExecutor.IsTransientNumber(number));
    }

    [Fact]
    public void TransientNumberViaExceptionRetries()
    {
        // A SqlException carrying a known transient number classifies transient
        // even when IsTransient is not set by the driver.
        var ex = MakeSqlException(40613);
        Assert.True(SqlExecutor.IsTransient(ex));
    }

    [Fact]
    public void DeadlockExceptionIsTransient()
    {
        Assert.True(SqlExecutor.IsTransient(MakeSqlException(1205)));
    }

    [Fact]
    public void NonTransientSqlExceptionDoesNotRetry()
    {
        // 18456 (login failed) is a hard error — no retry.
        Assert.False(SqlExecutor.IsTransient(MakeSqlException(18456)));
    }

    [Fact]
    public void ArbitraryExceptionIsNotTransient()
    {
        Assert.False(SqlExecutor.IsTransient(new InvalidOperationException("boom")));
        Assert.False(SqlExecutor.IsTransient(new ArgumentException("bad")));
    }

    [Fact]
    public void TimeoutExceptionIsTransient()
    {
        Assert.True(SqlExecutor.IsTransient(new TimeoutException("timed out")));
    }

    /// <summary>
    /// Build a real <see cref="SqlException"/> carrying a single <see cref="SqlError"/>
    /// with the chosen error number, via the driver's internal factory (public
    /// constructors are unavailable). Targets the concrete Microsoft.Data.SqlClient
    /// 5.2.x internal signatures verified by reflection:
    ///   SqlError(int infoNumber, byte errorState, byte errorClass, string server,
    ///            string errorMessage, string procedure, int lineNumber, Exception exception)
    ///   SqlException.CreateException(SqlErrorCollection, string serverVersion)
    /// so the classification predicate can be driven with a specific number without
    /// a live connection.
    /// </summary>
    private static SqlException MakeSqlException(int number)
    {
        const System.Reflection.BindingFlags npInstance =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var errors = (SqlErrorCollection)typeof(SqlErrorCollection)
            .GetConstructor(npInstance, binder: null, Type.EmptyTypes, modifiers: null)!
            .Invoke(null);

        var errorCtor = typeof(SqlError).GetConstructor(
            npInstance,
            binder: null,
            new[]
            {
                typeof(int), typeof(byte), typeof(byte), typeof(string),
                typeof(string), typeof(string), typeof(int), typeof(Exception),
            },
            modifiers: null)!;
        var error = (SqlError)errorCtor.Invoke(new object?[]
        {
            number, (byte)1, (byte)20, "testserver",
            $"Simulated transient error {number}", "testproc", 0, null,
        });

        typeof(SqlErrorCollection)
            .GetMethod("Add", npInstance)!
            .Invoke(errors, new object[] { error });

        var create = typeof(SqlException).GetMethod(
            "CreateException",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            binder: null,
            new[] { typeof(SqlErrorCollection), typeof(string) },
            modifiers: null)!;
        return (SqlException)create.Invoke(null, new object[] { errors, "16.0.0" })!;
    }
}

// ── Backoff computation (pure, no sleeps) ────────────────────────────────────

public class SqlExecutorBackoffTests
{
    [Fact]
    public void ExponentialGrowthWithoutJitter()
    {
        // uniformSample 0.5 → the jitter multiplier is exactly 1.0, so we see the
        // raw exponential: base·2^(attempt-1) = 1, 2, 4, 8, 16 seconds.
        Assert.Equal(1.0, SqlExecutor.ComputeBackoff(1, 0.5).TotalSeconds, precision: 6);
        Assert.Equal(2.0, SqlExecutor.ComputeBackoff(2, 0.5).TotalSeconds, precision: 6);
        Assert.Equal(4.0, SqlExecutor.ComputeBackoff(3, 0.5).TotalSeconds, precision: 6);
        Assert.Equal(8.0, SqlExecutor.ComputeBackoff(4, 0.5).TotalSeconds, precision: 6);
        Assert.Equal(16.0, SqlExecutor.ComputeBackoff(5, 0.5).TotalSeconds, precision: 6);
    }

    [Fact]
    public void DelayIsCappedAtThirtySeconds()
    {
        // 2^6 = 64s would exceed the cap; it must clamp to 30s (before jitter).
        Assert.Equal(30.0, SqlExecutor.ComputeBackoff(7, 0.5).TotalSeconds, precision: 6);
        Assert.Equal(30.0, SqlExecutor.ComputeBackoff(50, 0.5).TotalSeconds, precision: 6);
        // Even a huge attempt number must not overflow to Infinity/NaN.
        Assert.Equal(30.0, SqlExecutor.ComputeBackoff(1000, 0.5).TotalSeconds, precision: 6);
    }

    [Fact]
    public void JitterExtremesMapToPlusMinusTwentyPercent()
    {
        // attempt 5 → 16s base. sample 0 → −20% (12.8s); sample 1 → +20% (19.2s).
        Assert.Equal(12.8, SqlExecutor.ComputeBackoff(5, 0.0).TotalSeconds, precision: 6);
        Assert.Equal(16.0, SqlExecutor.ComputeBackoff(5, 0.5).TotalSeconds, precision: 6);
        Assert.Equal(19.2, SqlExecutor.ComputeBackoff(5, 1.0).TotalSeconds, precision: 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void JitteredDelayStaysWithinBoundsForAnySample(int attempt)
    {
        var mid = SqlExecutor.ComputeBackoff(attempt, 0.5).TotalSeconds;
        for (var i = 0; i <= 100; i++)
        {
            var sample = i / 100.0;
            var delay = SqlExecutor.ComputeBackoff(attempt, sample).TotalSeconds;
            const double eps = 1e-9;
            Assert.InRange(delay, mid * 0.8 - eps, mid * 1.2 + eps);
        }
    }

    [Fact]
    public void AttemptZeroDoesNotThrowAndIsBaseDelay()
    {
        // Defensive: attempt 0 clamps the exponent to 0 (base delay), never negative.
        Assert.Equal(1.0, SqlExecutor.ComputeBackoff(0, 0.5).TotalSeconds, precision: 6);
    }
}

// ── SQL_MAX_RETRIES parsing (pure) ───────────────────────────────────────────

public class SqlExecutorMaxRetriesParseTests
{
    [Fact]
    public void DefaultsToFiveWhenUnsetOrJunk()
    {
        Assert.Equal(5, SqlExecutor.ParseMaxRetries(null));
        Assert.Equal(5, SqlExecutor.ParseMaxRetries(""));
        Assert.Equal(5, SqlExecutor.ParseMaxRetries("banana"));
        Assert.Equal(5, SqlExecutor.ParseMaxRetries("-1"));  // negative → default
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("3", 3)]
    [InlineData("10", 10)]
    public void ParsesNonNegativeIntegers(string raw, int expected)
    {
        Assert.Equal(expected, SqlExecutor.ParseMaxRetries(raw));
    }
}

// ── Connection-string hardening (pure) ───────────────────────────────────────

public class SqlExecutorHardenTests
{
    [Fact]
    public void EncryptForcedOnWhenNotSpecified()
    {
        var (hardened, stripped) = SqlExecutor.HardenConnectionString(
            "Server=listener;Database=SalesforceConnector;User Id=app;Password=p@ss;",
            useManagedIdentity: false);

        var builder = new SqlConnectionStringBuilder(hardened);
        Assert.True(builder.Encrypt);
        Assert.False(stripped);
        // Creds untouched when MI is off.
        Assert.Equal("app", builder.UserID);
        Assert.Equal("p@ss", builder.Password);
    }

    [Fact]
    public void ExplicitEncryptFalseIsPreserved()
    {
        // The operator's explicit choice must never be overridden.
        var (hardened, _) = SqlExecutor.HardenConnectionString(
            "Server=listener;Database=db;Encrypt=False;",
            useManagedIdentity: false);
        Assert.False(new SqlConnectionStringBuilder(hardened).Encrypt);
    }

    [Fact]
    public void ExplicitEncryptTrueIsPreserved()
    {
        var (hardened, _) = SqlExecutor.HardenConnectionString(
            "Server=listener;Database=db;Encrypt=True;",
            useManagedIdentity: false);
        Assert.True(new SqlConnectionStringBuilder(hardened).Encrypt);
    }

    [Fact]
    public void TrustServerCertificateIsNotWeakenedOrChanged()
    {
        // Left exactly as supplied — both when present true and when absent.
        var (withTrust, _) = SqlExecutor.HardenConnectionString(
            "Server=s;Database=d;TrustServerCertificate=True;", useManagedIdentity: false);
        Assert.True(new SqlConnectionStringBuilder(withTrust).TrustServerCertificate);

        var (withoutTrust, _) = SqlExecutor.HardenConnectionString(
            "Server=s;Database=d;", useManagedIdentity: false);
        // Absent stays at the driver default (false) — we never turn it on.
        Assert.False(new SqlConnectionStringBuilder(withoutTrust).TrustServerCertificate);
    }

    [Fact]
    public void ManagedIdentityStripsCredentialsAndSetsEntraAuth()
    {
        var (hardened, stripped) = SqlExecutor.HardenConnectionString(
            "Server=listener;Database=SalesforceConnector;User Id=app;Password=p@ss;",
            useManagedIdentity: true);

        var builder = new SqlConnectionStringBuilder(hardened);
        Assert.True(stripped);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryDefault, builder.Authentication);
        Assert.True(string.IsNullOrEmpty(builder.UserID));
        Assert.True(string.IsNullOrEmpty(builder.Password));
        // Still hardened for transport.
        Assert.True(builder.Encrypt);
    }

    [Fact]
    public void ManagedIdentityWithoutCredentialsReportsNoStrip()
    {
        // Integrated / no-cred connection strings: MI still sets the auth mode but
        // there were no creds to strip, so the info log is not emitted.
        var (hardened, stripped) = SqlExecutor.HardenConnectionString(
            "Server=listener;Database=SalesforceConnector;",
            useManagedIdentity: true);

        Assert.False(stripped);
        Assert.Equal(
            SqlAuthenticationMethod.ActiveDirectoryDefault,
            new SqlConnectionStringBuilder(hardened).Authentication);
    }
}

// ── Cancellation on the open path (pure, via a fake) ─────────────────────────

/// <summary>
/// SqlExecutor observes the caller's CancellationToken right after a successful
/// Open (OpenRaw → OpenWithCancellation). On that path the just-opened
/// connection must be disposed BEFORE the OperationCanceledException propagates
/// (no leaked connection), and the cancellation must never be classified
/// transient (no retry). Driven through the internal seam with a fake
/// disposable — no SQL Server, no sleeps.
/// </summary>
public class SqlExecutorOpenCancellationTests
{
    private sealed class FakeConnection : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void CancellationAfterOpenDisposesTheJustOpenedConnection()
    {
        var fake = new FakeConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => SqlExecutor.OpenWithCancellation(() => fake, cts.Token));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void NoCancellationReturnsTheConnectionUndisposed()
    {
        var fake = new FakeConnection();

        var returned = SqlExecutor.OpenWithCancellation(() => fake, CancellationToken.None);

        Assert.Same(fake, returned);
        Assert.False(fake.Disposed);
    }

    [Fact]
    public void OpenFailurePropagatesUnchanged()
    {
        // A failed open (factory throws) propagates as-is — the factory owns
        // disposal on that path; the cancellation guard must not mask it.
        Assert.Throws<InvalidOperationException>(
            () => SqlExecutor.OpenWithCancellation<FakeConnection>(
                () => throw new InvalidOperationException("open failed"),
                CancellationToken.None));
    }

    [Fact]
    public void CancellationIsNotClassifiedTransient()
    {
        // A cancellation thrown from the open path (or anywhere in the body)
        // must rethrow immediately — never retried like a transient SQL fault.
        Assert.False(SqlExecutor.IsTransient(new OperationCanceledException()));
        Assert.False(SqlExecutor.IsTransient(new TaskCanceledException()));
    }
}

// ── Env-driven config (cached + resettable) ──────────────────────────────────

/// <summary>
/// SqlExecutor.MaxRetries / UseManagedIdentity read process env once and cache;
/// these flip env vars, so they join the "EnvVars" collection and restore state.
/// </summary>
[Collection("EnvVars")]
public sealed class SqlExecutorEnvConfigTests : IDisposable
{
    private readonly string? _savedMax;
    private readonly string? _savedMi;

    public SqlExecutorEnvConfigTests()
    {
        _savedMax = Environment.GetEnvironmentVariable("SQL_MAX_RETRIES");
        _savedMi = Environment.GetEnvironmentVariable("SQL_USE_MANAGED_IDENTITY");
        Environment.SetEnvironmentVariable("SQL_MAX_RETRIES", null);
        Environment.SetEnvironmentVariable("SQL_USE_MANAGED_IDENTITY", null);
        SqlExecutor.ResetConfigCache();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQL_MAX_RETRIES", _savedMax);
        Environment.SetEnvironmentVariable("SQL_USE_MANAGED_IDENTITY", _savedMi);
        SqlExecutor.ResetConfigCache();
    }

    [Fact]
    public void MaxRetriesDefaultsToFive()
    {
        Assert.Equal(5, SqlExecutor.MaxRetries);
    }

    [Fact]
    public void MaxRetriesReadsEnvAfterReset()
    {
        Environment.SetEnvironmentVariable("SQL_MAX_RETRIES", "9");
        SqlExecutor.ResetConfigCache();
        Assert.Equal(9, SqlExecutor.MaxRetries);
    }

    [Fact]
    public void ManagedIdentityDefaultsOff()
    {
        Assert.False(SqlExecutor.UseManagedIdentity);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("banana", false)]
    public void ManagedIdentityReadsEnvAfterReset(string value, bool expected)
    {
        Environment.SetEnvironmentVariable("SQL_USE_MANAGED_IDENTITY", value);
        SqlExecutor.ResetConfigCache();
        Assert.Equal(expected, SqlExecutor.UseManagedIdentity);
    }
}
