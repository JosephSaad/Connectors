// Clarizen/IAttachmentDownloader.cs
// ---------------------------------
// Fetches attachment binaries with a hard size cap. Abstracted so the
// attachment enricher is testable without real HTTP; ClarizenClient is the
// production implementation.

namespace ClarizenConnector.Clarizen;

public sealed record DownloadResult(
    bool Ok,
    byte[] Content,
    string Reason,
    bool Oversize = false)
{
    public static DownloadResult Success(byte[] content) => new(true, content, "ok");
    public static DownloadResult Failed(string reason) => new(false, Array.Empty<byte>(), reason);
    public static DownloadResult TooLarge() =>
        new(false, Array.Empty<byte>(), "oversize", Oversize: true);
}

public interface IAttachmentDownloader
{
    /// <summary>
    /// Download the bytes at <paramref name="url"/>, reading at most
    /// <paramref name="maxBytes"/>. Returns <see cref="DownloadResult.TooLarge"/>
    /// (without buffering the whole file) when the stream exceeds the cap.
    /// Never throws — transport failures come back as a failed result.
    /// </summary>
    Task<DownloadResult> DownloadAttachmentAsync(string url, long maxBytes, CancellationToken ct = default);
}
