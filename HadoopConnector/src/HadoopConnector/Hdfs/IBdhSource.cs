// Hdfs/IBdhSource.cs
// ------------------
// Source-access abstraction over the BDH data mart (HDFS_MODE):
//
//   webhdfs   — WebHdfsSource → WebHDFS REST (LISTSTATUS/OPEN) against the
//               namenode (or a Knox gateway URL). Default.
//   localpath — LocalPathSource → BDH_EXPORT_PATH local/SMB-mounted directory
//               with the same Hive-partitioned layout (dev/DR).
//
// All paths are RELATIVE to the configured BDH root; sources translate them.

namespace HadoopConnector.Hdfs;

/// <summary>One directory entry from a listing (file or sub-directory).</summary>
public sealed record HdfsFileStatus(
    string PathSuffix,
    bool IsDirectory,
    long Length,
    /// <summary>Modification time (Unix ms) as reported by the source; 0 when unknown.</summary>
    long ModificationTimeMs);

public interface IBdhSource : IDisposable
{
    /// <summary>Human-readable description for logs ("webhdfs http://..." / "localpath /mnt/bdh").</summary>
    string Description { get; }

    /// <summary>List one directory (relative path, "" = root). Throws
    /// <see cref="HdfsException"/> when the path does not exist or the
    /// listing fails; CircuitOpenException during a sustained outage.</summary>
    Task<List<HdfsFileStatus>> ListAsync(string relativePath, CancellationToken ct = default);

    /// <summary>True when the directory exists (no throw on absence).</summary>
    Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Open one file for streaming read.</summary>
    Task<Stream> OpenAsync(string relativePath, CancellationToken ct = default);
}

/// <summary>Source-access failure (missing path, RemoteException, transport error).</summary>
public sealed class HdfsException : Exception
{
    public HdfsException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    public int StatusCode { get; init; }
}

/// <summary>Local/SMB-mounted BDH export directory (HDFS_MODE=localpath, or dev/DR).</summary>
public sealed class LocalPathSource : IBdhSource
{
    private readonly string _root;

    public LocalPathSource(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public string Description => $"localpath {_root}";

    /// <summary>Resolve a relative path under the root, refusing traversal escapes.</summary>
    internal string Resolve(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_root, relativePath.TrimStart('/', '\\')));
        if (!combined.StartsWith(_root, StringComparison.Ordinal))
            throw new HdfsException($"Path '{relativePath}' escapes the BDH export root.");
        return combined;
    }

    public Task<List<HdfsFileStatus>> ListAsync(string relativePath, CancellationToken ct = default)
    {
        var dir = Resolve(relativePath);
        if (!Directory.Exists(dir))
            throw new HdfsException($"Directory not found: '{relativePath}' (under {_root}).") { StatusCode = 404 };
        var entries = new List<HdfsFileStatus>();
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var isDir = (info.Attributes & FileAttributes.Directory) != 0;
            var length = isDir ? 0 : ((FileInfo)info).Length;
            entries.Add(new HdfsFileStatus(
                info.Name, isDir, length,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
        return Task.FromResult(entries);
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default) =>
        Task.FromResult(Directory.Exists(Resolve(relativePath)));

    public Task<Stream> OpenAsync(string relativePath, CancellationToken ct = default)
    {
        var file = Resolve(relativePath);
        if (!File.Exists(file))
            throw new HdfsException($"File not found: '{relativePath}' (under {_root}).") { StatusCode = 404 };
        return Task.FromResult<Stream>(
            new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    public void Dispose()
    {
    }
}
