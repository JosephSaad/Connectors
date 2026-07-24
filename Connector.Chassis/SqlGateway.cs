// SqlGateway.cs
// -------------
// Instance SQL Server access seam for connectors whose state store and HA
// coordinator run against a mockable gateway (tests substitute an in-memory
// fake). Distinct from the static SqlExecutor in this chassis (which some
// connectors use as a connection-delegate with baked schema provisioning);
// SqlGateway is the parameterized-text-command seam. Both coexist so each
// connector keeps the SQL access model it was built around.
//
//   • Encrypt=True forced unless the connection string already sets Encrypt.
//   • Optional Entra Managed Identity auth (SQL_USE_MANAGED_IDENTITY=true) — an
//     access token for https://database.windows.net/.default is attached
//     instead of connection-string credentials.
//   • Transient-fault retry (SQL_MAX_RETRIES, default 5) with exponential
//     backoff, honouring the well-known transient/AG-failover error numbers.

using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;

namespace Connector.Chassis;

/// <summary>Minimal SQL gateway seam (parameterized text commands).</summary>
public interface ISqlGateway
{
    int Execute(string sql, params (string Name, object? Value)[] args);
    object? Scalar(string sql, params (string Name, object? Value)[] args);
    List<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] args);
}

public sealed class SqlGateway : ISqlGateway
{
    private static readonly IAppLogger Logger = Logging.GetLogger($"{Chassis.Identity.BaseLoggerName}.sql");

    /// <summary>Transient SQL error numbers worth retrying (throttling, failover, deadlock, timeouts).</summary>
    internal static readonly int[] TransientErrorNumbers =
    {
        -2,     // timeout
        20,     // instance does not support encryption / connection reset
        64,     // connection failed during login
        233,    // connection init error
        1205,   // deadlock victim
        4060,   // cannot open database (failover in progress)
        10928,  // resource limit
        10929,  // resource limit
        40197,  // service error processing request
        40501,  // service busy
        40613,  // database unavailable
        49918, 49919, 49920, // cannot process request
        11001,  // host not found (DNS during failover)
    };

    private readonly string _connectionString;
    private readonly bool _useManagedIdentity;
    private readonly int _maxRetries;
    private static readonly TokenCredential Credential = new DefaultAzureCredential();

    public SqlGateway(string? connectionString = null)
    {
        var raw = connectionString
            ?? Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
            ?? throw new ArgumentException("Invalid configuration: Missing SQL_CONNECTION_STRING");
        _connectionString = EnsureEncrypt(raw);
        _useManagedIdentity = EnvFlags.IsTrue("SQL_USE_MANAGED_IDENTITY");
        _maxRetries = Math.Max(0, EnvFlags.GetInt("SQL_MAX_RETRIES", 5));
    }

    /// <summary>Force Encrypt=True unless the string already sets Encrypt (testable).</summary>
    internal static string EnsureEncrypt(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("Encrypt", StringComparison.OrdinalIgnoreCase))
            builder.Encrypt = true;
        return builder.ConnectionString;
    }

    /// <summary>Exponential backoff delay for retry <paramref name="attempt"/> (0-based): 1, 2, 4, ... capped at 30s.</summary>
    internal static TimeSpan RetryDelayFor(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));

    internal static bool IsTransient(SqlException exc)
    {
        foreach (SqlError error in exc.Errors)
        {
            if (Array.IndexOf(TransientErrorNumbers, error.Number) >= 0)
                return true;
        }
        return false;
    }

    public int Execute(string sql, params (string Name, object? Value)[] args) =>
        WithRetry(sql, args, command => command.ExecuteNonQuery());

    public object? Scalar(string sql, params (string Name, object? Value)[] args) =>
        WithRetry(sql, args, command => command.ExecuteScalar());

    public List<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] args) =>
        WithRetry(sql, args, command =>
        {
            var rows = new List<Dictionary<string, object?>>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        });

    private T WithRetry<T>(string sql, (string Name, object? Value)[] args, Func<SqlCommand, T> run)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 60;
                foreach (var (name, value) in args)
                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                return run(command);
            }
            catch (SqlException exc) when (attempt < _maxRetries && IsTransient(exc))
            {
                var delay = RetryDelayFor(attempt);
                Logger.Warning(
                    $"Transient SQL error {exc.Number} (attempt {attempt + 1}/{_maxRetries}); retrying in {delay.TotalSeconds:0}s: {exc.Message}");
                Thread.Sleep(delay);
            }
        }
    }

    private SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(_connectionString);
        if (_useManagedIdentity)
        {
            var token = Credential.GetToken(
                new TokenRequestContext(new[] { "https://database.windows.net/.default" }),
                CancellationToken.None);
            connection.AccessToken = token.Token;
        }
        connection.Open();
        return connection;
    }
}
