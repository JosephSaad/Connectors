// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/SqlExecutor.cs
// -----------------------------
// SQL Server resilience + connection hardening (improvements #1 and #8) — the
// single seam every SQL caller goes through to open connections and run work.
//
// Two responsibilities:
//
//   * OpenHardened(Async): build the connection from the caller's connection
//     string, force Encrypt=True unless the string already sets Encrypt, and —
//     when SQL_USE_MANAGED_IDENTITY=true — switch to Entra (ActiveDirectoryDefault)
//     auth and strip any User ID/Password. TrustServerCertificate is left exactly
//     as the operator set it (never weakened here).
//
//   * Execute(Async): run the caller's body against a freshly opened hardened
//     connection with transient-fault retry. On an Always On availability-group
//     failover, throttling, deadlock or transient network blip (SqlException.IsTransient
//     or a known transient error number) the whole body is retried up to
//     SQL_MAX_RETRIES times with exponential backoff (base 1s, cap 30s) plus a
//     small ±20% jitter so HA nodes that failed together do not retry in lockstep.
//     Non-transient exceptions rethrow immediately.
//
// OFF-BY-DEFAULT: with no new env vars set, connections still open and every
// operation still runs exactly once on the happy path; the retry loop only ever
// engages on a transient SqlException, and Encrypt-forcing matches SqlClient 5.x
// which already defaults Encrypt=True. Managed-identity rewriting is inert unless
// SQL_USE_MANAGED_IDENTITY=true.

using System.Data;
using Microsoft.Data.SqlClient;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>
/// Hardened connection factory + transient-fault retry wrapper shared by every
/// SQL caller (SqlServerIdentityStore, SqlStateStore, HaCoordinator).
/// </summary>
public static class SqlExecutor
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>Backoff base: first retry waits ~1s.</summary>
    internal const double BackoffBaseSeconds = 1.0;

    /// <summary>Backoff ceiling: a single wait never exceeds ~30s.</summary>
    internal const double BackoffCapSeconds = 30.0;

    /// <summary>Jitter amplitude applied to a computed backoff: ±20%.</summary>
    internal const double JitterFraction = 0.20;

    /// <summary>Default transient-fault retry count (SQL_MAX_RETRIES).</summary>
    internal const int DefaultMaxRetries = 5;

    private static readonly Random Rng = new();

    /// <summary>
    /// SQL error numbers that mean "retryable" beyond what SqlException.IsTransient
    /// already flags: Always On AG failover / not-accepting-connections
    /// (40197, 40501, 40613, 49918, 49919, 49920, 10928, 10929, 4060, 4221),
    /// deadlock victim (1205), transport/TLS pre-login blips (233, 64), command
    /// timeout (-2) and severe-error connection resets (20). Superset of the
    /// docs/IMPROVEMENTS_CONTRACT.md list.
    /// </summary>
    internal static readonly IReadOnlySet<int> TransientErrorNumbers = new HashSet<int>
    {
        40197, 40501, 40613, 49918, 49919, 49920, 10928, 10929, 1205, 233, 64, -2, 20, 4060, 4221,
    };

    // ── Env configuration (single cached reader + test reset seam) ────────────

    private static int? _maxRetries;
    private static bool? _useManagedIdentity;

    /// <summary>
    /// Transient-fault retry count from SQL_MAX_RETRIES (default 5). A missing,
    /// non-numeric or negative value falls back to the default. Cached; resettable
    /// for tests via <see cref="ResetConfigCache"/>.
    /// </summary>
    internal static int MaxRetries
    {
        get
        {
            _maxRetries ??= ParseMaxRetries(Environment.GetEnvironmentVariable("SQL_MAX_RETRIES"));
            return _maxRetries.Value;
        }
    }

    /// <summary>True when SQL_USE_MANAGED_IDENTITY=true (case-insensitive). Cached; resettable for tests.</summary>
    internal static bool UseManagedIdentity
    {
        get
        {
            _useManagedIdentity ??= string.Equals(
                Environment.GetEnvironmentVariable("SQL_USE_MANAGED_IDENTITY"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            return _useManagedIdentity.Value;
        }
    }

    /// <summary>Test seam: re-read SQL_MAX_RETRIES / SQL_USE_MANAGED_IDENTITY on next use.</summary>
    internal static void ResetConfigCache()
    {
        _maxRetries = null;
        _useManagedIdentity = null;
    }

    /// <summary>Pure parse of SQL_MAX_RETRIES: numeric and &gt;= 0 wins, else the default.</summary>
    internal static int ParseMaxRetries(string? raw) =>
        int.TryParse(raw, out var value) && value >= 0 ? value : DefaultMaxRetries;

    // ── Connection hardening (#8) ─────────────────────────────────────────────

    /// <summary>
    /// Pure hardening of a connection string (unit-testable seam, no I/O): force
    /// <c>Encrypt=True</c> when the caller did not set Encrypt explicitly, and when
    /// <paramref name="useManagedIdentity"/> switch to <c>ActiveDirectoryDefault</c>
    /// auth and drop any User ID / Password. TrustServerCertificate is never touched.
    /// </summary>
    /// <returns>The hardened connection string and whether creds were stripped.</returns>
    internal static (string ConnectionString, bool StrippedCredentials) HardenConnectionString(
        string connectionString, bool useManagedIdentity)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        // #8: default to encryption in transit unless the operator was explicit.
        // ShouldSerialize* reports whether the keyword was actually present in the
        // input, so we only fill the gap and never override an explicit choice.
        if (!builder.ShouldSerialize("Encrypt"))
            builder.Encrypt = true;

        // TrustServerCertificate is intentionally left exactly as supplied — we do
        // not weaken (or strengthen) certificate validation here.

        var strippedCredentials = false;
        if (useManagedIdentity)
        {
            builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
            if (!string.IsNullOrEmpty(builder.UserID) || !string.IsNullOrEmpty(builder.Password))
                strippedCredentials = true;
            builder.UserID = string.Empty;
            builder.Password = string.Empty;
        }

        return (builder.ConnectionString, strippedCredentials);
    }

    /// <summary>Apply <see cref="HardenConnectionString"/> using the current env config, logging a one-line info when MI strips creds.</summary>
    private static string HardenForEnv(string connectionString)
    {
        var (hardened, stripped) = HardenConnectionString(connectionString, UseManagedIdentity);
        if (stripped)
            Logger.Info("[SQL] SQL_USE_MANAGED_IDENTITY=true — using Entra (ActiveDirectoryDefault) auth; ignoring User ID/Password from the connection string");
        return hardened;
    }

    /// <summary>
    /// Open a hardened <see cref="SqlConnection"/> (Encrypt forced on unless set,
    /// MI auth when configured) with transient-fault retry. The caller owns the
    /// returned open connection.
    /// </summary>
    public static SqlConnection OpenHardened(string connectionString)
    {
        var hardened = HardenForEnv(connectionString);
        return RunWithRetry(() =>
        {
            var connection = new SqlConnection(hardened);
            try
            {
                connection.Open();
            }
            catch
            {
                connection.Dispose();
                throw;
            }
            return connection;
        });
    }

    /// <summary>Async counterpart of <see cref="OpenHardened"/>. Honors <paramref name="cancellationToken"/>.</summary>
    public static Task<SqlConnection> OpenHardenedAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var hardened = HardenForEnv(connectionString);
        return RunWithRetryAsync(async () =>
        {
            var connection = new SqlConnection(hardened);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return connection;
        }, cancellationToken);
    }

    // ── Execute helpers (#1) ──────────────────────────────────────────────────
    // Each Execute opens a fresh hardened connection, runs the caller's body, and
    // retries the WHOLE unit (open + body) on a transient fault — so a failover
    // that kills the connection mid-op is retried against the new primary.

    /// <summary>Run <paramref name="body"/> against a hardened connection with retry, returning its result.</summary>
    public static T Execute<T>(string connectionString, Func<SqlConnection, T> body)
    {
        var hardened = HardenForEnv(connectionString);
        return RunWithRetry(() =>
        {
            using var connection = OpenRaw(hardened);
            return body(connection);
        });
    }

    /// <summary>Run <paramref name="body"/> (no result) against a hardened connection with retry.</summary>
    public static void Execute(string connectionString, Action<SqlConnection> body)
    {
        _ = Execute(connectionString, conn =>
        {
            body(conn);
            return true;
        });
    }

    /// <summary>Async: run <paramref name="body"/> against a hardened connection with retry, returning its result.</summary>
    public static Task<T> ExecuteAsync<T>(
        string connectionString,
        Func<SqlConnection, Task<T>> body,
        CancellationToken cancellationToken = default)
    {
        var hardened = HardenForEnv(connectionString);
        return RunWithRetryAsync(async () =>
        {
            var connection = OpenRaw(hardened, cancellationToken);
            await using (connection.ConfigureAwait(false))
            {
                return await body(connection).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    /// <summary>Async: run <paramref name="body"/> (no result) against a hardened connection with retry.</summary>
    public static Task ExecuteAsync(
        string connectionString,
        Func<SqlConnection, Task> body,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(connectionString, async conn =>
        {
            await body(conn).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    // Opens a connection whose string is already hardened (used inside Execute so
    // the string is hardened once per call, not once per retry attempt).
    private static SqlConnection OpenRaw(string hardenedConnectionString, CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(hardenedConnectionString);
        try
        {
            connection.Open();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return connection;
    }

    // ── Transient classification (#1, pure) ───────────────────────────────────

    /// <summary>True when <paramref name="number"/> is a known transient SQL error number.</summary>
    internal static bool IsTransientNumber(int number) => TransientErrorNumbers.Contains(number);

    /// <summary>
    /// Pure classification (unit-testable seam): an exception is transient when it
    /// is a <see cref="SqlException"/> whose <c>IsTransient</c> is set, or any error
    /// in its collection carries a known transient number, or (for timeouts surfaced
    /// as a plain <see cref="TimeoutException"/>) it is a timeout. Everything else is
    /// non-transient and must rethrow without retry.
    /// </summary>
    internal static bool IsTransient(Exception exception)
    {
        switch (exception)
        {
            case SqlException sqlException:
                if (sqlException.IsTransient)
                    return true;
                foreach (SqlError error in sqlException.Errors)
                {
                    if (IsTransientNumber(error.Number))
                        return true;
                }
                return IsTransientNumber(sqlException.Number);
            case TimeoutException:
                return true;
            default:
                return false;
        }
    }

    // ── Backoff computation (#1, pure) ────────────────────────────────────────

    /// <summary>
    /// Pure backoff computation (unit-testable seam, no sleeps): exponential
    /// <c>base·2^(attempt-1)</c> capped at <see cref="BackoffCapSeconds"/>, then a
    /// ±<see cref="JitterFraction"/> jitter driven by <paramref name="uniformSample"/>
    /// in [0, 1]. <paramref name="attempt"/> is 1-based (the delay taken before the
    /// 2nd try is attempt 1).
    /// </summary>
    internal static TimeSpan ComputeBackoff(int attempt, double uniformSample)
    {
        var exponent = Math.Max(0, attempt - 1);
        // Cap the exponent too, so 2^exponent cannot overflow to Infinity before Min.
        var raw = exponent >= 30
            ? BackoffCapSeconds
            : BackoffBaseSeconds * Math.Pow(2.0, exponent);
        var capped = Math.Min(BackoffCapSeconds, raw);
        var jittered = capped * (1.0 - JitterFraction + 2.0 * JitterFraction * uniformSample);
        return TimeSpan.FromSeconds(jittered);
    }

    /// <summary>Backoff for <paramref name="attempt"/> using a fresh uniform draw.</summary>
    private static TimeSpan NextBackoff(int attempt)
    {
        double sample;
        lock (Rng)
            sample = Rng.NextDouble();
        return ComputeBackoff(attempt, sample);
    }

    // ── Retry cores ───────────────────────────────────────────────────────────
    // maxRetries = number of RETRIES after the first attempt, so total tries is
    // maxRetries + 1. A non-transient failure — or a transient one after the last
    // retry — rethrows.

    private static T RunWithRetry<T>(Func<T> action)
    {
        var maxRetries = MaxRetries;
        var attempt = 0;
        while (true)
        {
            try
            {
                return action();
            }
            catch (Exception exception) when (IsTransient(exception) && attempt < maxRetries)
            {
                attempt++;
                var delay = NextBackoff(attempt);
                LogRetry(exception, attempt, maxRetries, delay);
                Thread.Sleep(delay);
            }
        }
    }

    private static async Task<T> RunWithRetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var maxRetries = MaxRetries;
        var attempt = 0;
        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception) && attempt < maxRetries)
            {
                attempt++;
                var delay = NextBackoff(attempt);
                LogRetry(exception, attempt, maxRetries, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void LogRetry(Exception exception, int attempt, int maxRetries, TimeSpan delay)
    {
        var number = exception is SqlException sql ? sql.Number : 0;
        Logger.Warning(
            $"[SQL] Transient error (number {number}) on attempt {attempt}/{maxRetries}; "
            + $"retrying in {delay.TotalSeconds:0.0}s: {exception.Message}");
    }
}
