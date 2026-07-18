// Fakes.cs
// --------
// In-harness test doubles (no real network), mirroring the seams the unit
// suite uses (FakeBdhSource / FakeGraphClient / MockHttpHandler) but sized for
// heavy synthetic volume. Kept separate from the test assembly because the
// harness references only src/.

using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;

namespace StressHarness;

/// <summary>Config factory (no config/ assets needed — everything is in code).</summary>
internal static class HarnessConfig
{
    public static AppConfig Make(
        string connectorId = "StressHarness",
        int ingestChunkSize = 500,
        int graphBatchSize = 20,
        int graphBatchWorkers = 8,
        int graphMaxRetries = 4,
        double backoffBase = 1.0,
        int lagHours = 0,
        int maxRecordsPerObject = 500_000,
        long maxFileBytes = 8L * 1024 * 1024 * 1024,
        bool allowFullScan = false) => new()
    {
        ConnectorId = connectorId,
        ConnectorName = "BDH Stress Harness",
        ConnectorDescription = "Synthetic stress harness (no real Graph/HDFS).",
        HdfsMode = "localpath",
        HdfsNamenodeUrl = null,
        HdfsUser = "svc-bdh",
        BdhExportPath = null,
        BdhRootPath = "/data/bdh",
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
        GraphBatchWorkers = graphBatchWorkers,
    };
}

/// <summary>A lazily-generated JSONL stream: yields <c>rowCount</c> rows without
/// ever holding them all in memory.</summary>
internal sealed class LazyJsonlStream : Stream
{
    private readonly int _rowCount;
    private readonly Func<int, string> _rowFactory;
    private byte[] _buffer = Array.Empty<byte>();
    private int _bufferPos;
    private int _nextRow;

    public LazyJsonlStream(int rowCount, Func<int, string> rowFactory)
    {
        _rowCount = rowCount;
        _rowFactory = rowFactory;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_bufferPos >= _buffer.Length)
        {
            if (_nextRow >= _rowCount)
                return 0;
            _buffer = Encoding.UTF8.GetBytes(_rowFactory(_nextRow++) + "\n");
            _bufferPos = 0;
        }
        var n = Math.Min(count, _buffer.Length - _bufferPos);
        Array.Copy(_buffer, _bufferPos, buffer, offset, n);
        _bufferPos += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Synthetic BDH source with a Hive-partitioned layout generated on the fly:
///   {object}/region={R}/dt=YYYY-MM-DD/part-0000.jsonl
/// Files are generated lazily (one row at a time) so 10^6 rows never materialize.
/// Instruments list/open counts so pruning can be proven (zero opens on pruned dirs).
/// </summary>
internal sealed class SyntheticBdhSource : IBdhSource
{
    private readonly string _object;
    private readonly string[] _regions;
    private readonly List<string> _dts;
    private readonly int _rowsPerFile;
    private readonly Func<string, string, int, string> _rowFactory; // region, dt, rowIndex → json

    private int _opens;
    private int _lists;
    public int OpenCalls => Volatile.Read(ref _opens);
    public int ListCalls => Volatile.Read(ref _lists);

    public SyntheticBdhSource(
        string obj, string[] regions, List<string> dts, int rowsPerFile,
        Func<string, string, int, string> rowFactory)
    {
        _object = obj;
        _regions = regions;
        _dts = dts;
        _rowsPerFile = rowsPerFile;
        _rowFactory = rowFactory;
    }

    public string Description => "synthetic";

    public Task<List<HdfsFileStatus>> ListAsync(string relativePath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _lists);
        var p = relativePath.Trim('/');
        if (p == _object)
            return Task.FromResult(_regions.Select(r => new HdfsFileStatus($"region={r}", true, 0, 0)).ToList());
        foreach (var r in _regions)
        {
            if (p == $"{_object}/region={r}")
                return Task.FromResult(_dts.Select(dt => new HdfsFileStatus($"dt={dt}", true, 0, 0)).ToList());
            foreach (var dt in _dts)
            {
                if (p == $"{_object}/region={r}/dt={dt}")
                {
                    return Task.FromResult(new List<HdfsFileStatus>
                    {
                        new("part-0000.jsonl", false, 4L * 1024 * 1024, 0),
                    });
                }
            }
        }
        throw new HdfsException($"Directory not found: '{relativePath}'.") { StatusCode = 404 };
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<Stream> OpenAsync(string relativePath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _opens);
        var p = relativePath.Trim('/');
        // .../region={R}/dt={D}/part-0000.jsonl
        var parts = p.Split('/');
        var region = parts[1]["region=".Length..];
        var dt = parts[2]["dt=".Length..];
        return Task.FromResult<Stream>(
            new LazyJsonlStream(_rowsPerFile, i => _rowFactory(region, dt, i)));
    }

    public int TotalRows => _regions.Length * _dts.Count * _rowsPerFile;

    public void Dispose() { }
}

/// <summary>
/// Lazy in-memory BDH source with per-file control: each registered file has
/// its own row count, REPORTED length (so oversize files can lie small or big
/// independently of content) and row factory. Directory structure is inferred
/// from the registered relative paths — arbitrary Hive layouts, gaps and
/// malformed dt names included. Rows are generated lazily.
/// </summary>
internal sealed class ScatterBdhSource : IBdhSource
{
    private sealed record Entry(int Rows, long ReportedBytes, Func<int, string> RowFactory);

    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);
    private int _opens;
    private int _lists;

    public int OpenCalls => Volatile.Read(ref _opens);
    public int ListCalls => Volatile.Read(ref _lists);

    public string Description => "scatter";

    public ScatterBdhSource AddFile(
        string relativePath, int rows, long reportedBytes, Func<int, string> rowFactory)
    {
        _files[relativePath.Trim('/')] = new Entry(rows, reportedBytes, rowFactory);
        return this;
    }

    public Task<List<HdfsFileStatus>> ListAsync(string relativePath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _lists);
        var prefix = relativePath.Trim('/');
        var entries = new Dictionary<string, HdfsFileStatus>(StringComparer.Ordinal);
        var found = false;
        foreach (var (path, entry) in _files)
        {
            if (prefix.Length > 0 && !path.StartsWith(prefix + "/", StringComparison.Ordinal))
                continue;
            found = true;
            var remainder = prefix.Length == 0 ? path : path[(prefix.Length + 1)..];
            var slash = remainder.IndexOf('/');
            if (slash < 0)
                entries[remainder] = new HdfsFileStatus(remainder, false, entry.ReportedBytes, 1700000000000);
            else
                entries.TryAdd(remainder[..slash], new HdfsFileStatus(remainder[..slash], true, 0, 0));
        }
        if (!found && prefix.Length > 0)
            throw new HdfsException($"Directory not found: '{relativePath}'.") { StatusCode = 404 };
        return Task.FromResult(entries.Values.ToList());
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
    {
        var prefix = relativePath.Trim('/');
        return Task.FromResult(prefix.Length == 0
            ? _files.Count > 0
            : _files.Keys.Any(k => k.StartsWith(prefix + "/", StringComparison.Ordinal)));
    }

    public Task<Stream> OpenAsync(string relativePath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _opens);
        if (!_files.TryGetValue(relativePath.Trim('/'), out var entry))
            throw new HdfsException($"File not found: '{relativePath}'.") { StatusCode = 404 };
        return Task.FromResult<Stream>(new LazyJsonlStream(entry.Rows, entry.RowFactory));
    }

    public void Dispose() { }
}

/// <summary>IBdhSource whose inner source can be swapped between syncs (a
/// nightly export changing shape run-over-run).</summary>
internal sealed class MutableBdhSource : IBdhSource
{
    public IBdhSource Inner { get; set; } = new ScatterBdhSource();
    public string Description => "mutable";
    public Task<List<HdfsFileStatus>> ListAsync(string p, CancellationToken ct = default) =>
        Inner.ListAsync(p, ct);
    public Task<bool> ExistsAsync(string p, CancellationToken ct = default) => Inner.ExistsAsync(p, ct);
    public Task<Stream> OpenAsync(string p, CancellationToken ct = default) => Inner.OpenAsync(p, ct);
    public void Dispose() { }
}

/// <summary>Thread-safe scripted HTTP handler keyed on request path with a
/// per-path attempt counter — drives WebHdfsClient retry ladders under many
/// concurrent OPENs.</summary>
internal sealed class FlappingHandler : HttpMessageHandler
{
    private readonly Func<string, int, HttpResponseMessage> _script;
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private int _requests;

    public FlappingHandler(Func<string, int, HttpResponseMessage> script) => _script = script;

    public int Requests => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var path = request.RequestUri!.AbsolutePath;
        int attempt;
        lock (_lock)
        {
            _attempts.TryGetValue(path, out attempt);
            _attempts[path] = attempt + 1;
        }
        return Task.FromResult(_script(path, attempt));
    }
}

/// <summary>In-memory identity store recording every upsert.</summary>
internal sealed class RecordingIdentityStore : IIdentityStore
{
    private readonly Dictionary<string, PrincipalMapping> _rows = new(StringComparer.Ordinal);
    public int UpsertCalls { get; private set; }

    public void Upsert(PrincipalMapping mapping)
    {
        UpsertCalls++;
        _rows[mapping.SourceId] = mapping;
    }

    public PrincipalMapping? Find(string sourceId) => _rows.GetValueOrDefault(sourceId);
    public List<PrincipalMapping> All() => _rows.Values.ToList();
    public int ResolvedCount() => _rows.Values.Count(m => m.EntraId is not null);
    public int Count() => _rows.Count;
    public void Clear() => _rows.Clear();
    public void Dispose() { }
}
