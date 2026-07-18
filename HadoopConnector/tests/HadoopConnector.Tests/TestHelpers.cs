// Shared test fixtures: env-var scopes, temp directories, config factories and
// a scriptable HttpMessageHandler (no real network calls anywhere in the suite).

using System.Net;
using System.Text;
using HadoopConnector.Config;

namespace HadoopConnector.Tests;

/// <summary>Sets env vars for the scope and restores the previous values on dispose.</summary>
public sealed class EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new();

    public EnvScope(params (string Name, string? Value)[] vars)
    {
        foreach (var (name, value) in vars)
            Set(name, value);
    }

    public void Set(string name, string? value)
    {
        if (!_previous.ContainsKey(name))
            _previous[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>Creates (and deletes on dispose) a unique temp directory.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "clzconn_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>Redirects SyncState.LogsDir to a temp dir for the scope.</summary>
public sealed class SyncStateScope : IDisposable
{
    private readonly string _previousLogsDir;
    private readonly TempDir _dir = new();

    public SyncStateScope()
    {
        _previousLogsDir = SyncState.LogsDir;
        SyncState.LogsDir = _dir.Path;
        SyncState.ResetProviderCache();
    }

    public string LogsDir => _dir.Path;

    public void Dispose()
    {
        SyncState.LogsDir = _previousLogsDir;
        SyncState.ResetProviderCache();
        _dir.Dispose();
    }
}

public static class TestConfig
{
    public static AppConfig Make(
        string connectorId = "BdhHadoopMart",
        int graphMaxRetries = 4,
        double backoffBase = 2.0,
        int ingestChunkSize = 200,
        int graphBatchSize = 20,
        string hdfsMode = "webhdfs",
        string? namenodeUrl = "http://namenode.example:9870/webhdfs/v1",
        string? exportPath = null,
        string rootPath = "/data/bdh",
        int lagHours = 24,
        int maxRecordsPerObject = 500_000,
        long maxFileBytes = 1024L * 1024 * 1024,
        bool allowFullScan = false,
        bool classification = false,
        bool classificationManifest = false) => new()
    {
        ConnectorId = connectorId,
        ConnectorName = "BDH Hadoop Data Mart",
        ConnectorDescription = "Test connector",
        HdfsMode = hdfsMode,
        HdfsNamenodeUrl = namenodeUrl,
        HdfsUser = "svc-bdh",
        BdhExportPath = exportPath,
        BdhRootPath = rootPath,
        BdhLagHours = lagHours,
        BdhMaxRecordsPerObject = maxRecordsPerObject,
        BdhMaxFileBytes = maxFileBytes,
        AllowFullScan = allowFullScan,
        AadTenantId = "11111111-1111-1111-1111-111111111111",
        AadClientId = "22222222-2222-2222-2222-222222222222",
        AadClientSecret = "secret",
        GraphMaxRetries = graphMaxRetries,
        GraphRetryBackoffBase = backoffBase,
        IngestChunkSize = ingestChunkSize,
        GraphBatchSize = graphBatchSize,
        Classification = classification,
        ClassificationManifest = classificationManifest,
    };
}

/// <summary>An in-memory IBdhSource backed by a dictionary of relative paths.
/// Directories are inferred; file bodies are UTF-8 strings.</summary>
public sealed class FakeBdhSource : HadoopConnector.Hdfs.IBdhSource
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>Optional per-path failure injection (path → exception factory).</summary>
    public Func<string, Exception?>? FailOn { get; set; }

    public int OpenCalls { get; private set; }
    public int ListCalls { get; private set; }

    public string Description => "fake";

    public FakeBdhSource Add(string relativePath, string content)
    {
        _files[relativePath.Trim('/')] = content;
        return this;
    }

    private static string Normalize(string p) => p.Trim('/');

    public Task<List<HadoopConnector.Hdfs.HdfsFileStatus>> ListAsync(
        string relativePath, CancellationToken ct = default)
    {
        ListCalls++;
        if (FailOn?.Invoke(relativePath) is { } exc)
            throw exc;
        var prefix = Normalize(relativePath);
        var entries = new Dictionary<string, HadoopConnector.Hdfs.HdfsFileStatus>(StringComparer.Ordinal);
        var found = false;
        foreach (var (path, content) in _files)
        {
            if (prefix.Length > 0 && !path.StartsWith(prefix + "/", StringComparison.Ordinal))
                continue;
            found = true;
            var remainder = prefix.Length == 0 ? path : path[(prefix.Length + 1)..];
            var slash = remainder.IndexOf('/');
            if (slash < 0)
            {
                entries[remainder] = new HadoopConnector.Hdfs.HdfsFileStatus(
                    remainder, false, System.Text.Encoding.UTF8.GetByteCount(content), 1700000000000);
            }
            else
            {
                var dir = remainder[..slash];
                entries.TryAdd(dir, new HadoopConnector.Hdfs.HdfsFileStatus(dir, true, 0, 0));
            }
        }
        if (!found && prefix.Length > 0)
            throw new HadoopConnector.Hdfs.HdfsException($"Directory not found: '{relativePath}'.") { StatusCode = 404 };
        return Task.FromResult(entries.Values.ToList());
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
    {
        var prefix = Normalize(relativePath);
        return Task.FromResult(prefix.Length == 0
            ? _files.Count > 0
            : _files.Keys.Any(k => k.StartsWith(prefix + "/", StringComparison.Ordinal)));
    }

    public Task<Stream> OpenAsync(string relativePath, CancellationToken ct = default)
    {
        OpenCalls++;
        if (FailOn?.Invoke(relativePath) is { } exc)
            throw exc;
        var path = Normalize(relativePath);
        if (!_files.TryGetValue(path, out var content))
            throw new HadoopConnector.Hdfs.HdfsException($"File not found: '{relativePath}'.") { StatusCode = 404 };
        return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));
    }

    public void Dispose()
    {
    }
}

/// <summary>Scriptable HTTP handler: routes each request through a delegate.</summary>
public sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public List<(string Url, string Body)> Requests { get; } = new();

    public MockHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.ToString(), body));
        return _responder(request, body);
    }
}
