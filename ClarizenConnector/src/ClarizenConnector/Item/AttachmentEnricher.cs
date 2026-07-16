// Item/AttachmentEnricher.cs
// --------------------------
// Enriches an attachment record's externalItem with extracted text content.
//
// The Attachment object type is already a first-class ingested item — it
// inherits its parent's ACLs (projectMembers + AttachedTo), is recorded in
// the ingested-item inventory, and is withdrawn by the deletion sweep when
// the parent/attachment is gone. So content ingestion is purely additive:
// download the binary, extract text, and append it to the item's content
// body. No new item type, no separate ACL path.
//
// Gated by ATTACHMENT_INGESTION (default off). For each attachment record:
//   1. type allowlist  (ATTACHMENT_ALLOWED_TYPES, by extension/mime)
//   2. size cap        (declared FileSize, then a hard streamed cap)
//   3. download + extract (IContentExtractor, dependency-free)
// Every skip is logged with a reason, counted (attachments_skipped_total),
// and stamped on the item as AttachmentExtractionStatus so operators can see
// why a file was not indexed. Extraction never fails a crawl — the worst case
// is a metadata-only attachment item, exactly as before this feature.

using System.Globalization;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Item;

public sealed class AttachmentEnricher
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.attachment");

    /// <summary>Graph property recording why (or that) attachment text was indexed.</summary>
    public const string StatusProperty = "AttachmentExtractionStatus";

    private readonly AppConfig _config;
    private readonly IAttachmentDownloader _downloader;
    private readonly IContentExtractor _extractor;

    public AttachmentEnricher(
        AppConfig config, IAttachmentDownloader downloader, IContentExtractor? extractor = null)
    {
        _config = config;
        _downloader = downloader;
        _extractor = extractor ?? new ContentExtractor();
    }

    /// <summary>True when this object type carries downloadable attachments and
    /// ingestion is enabled.</summary>
    public bool ShouldEnrich(ObjectConfig objectConfig) =>
        _config.AttachmentIngestion && objectConfig.IsAttachment;

    /// <summary>
    /// Enrich <paramref name="item"/> in place from <paramref name="record"/>.
    /// Returns the status stamped on the item. No-op (returns "disabled") when
    /// the object is not an attachment carrier or ingestion is off.
    /// </summary>
    public async Task<string> EnrichAsync(
        ExternalItem item, ClarizenRecord record, ObjectConfig objectConfig, CancellationToken ct = default)
    {
        if (!ShouldEnrich(objectConfig))
            return "disabled";

        var fileName = record.GetString(
            string.IsNullOrWhiteSpace(objectConfig.AttachmentNameField)
                ? "Name"
                : objectConfig.AttachmentNameField)
            ?? record.LocalId;
        var contentType = string.IsNullOrWhiteSpace(objectConfig.AttachmentContentTypeField)
            ? null
            : record.GetString(objectConfig.AttachmentContentTypeField);
        var url = record.GetString(objectConfig.AttachmentUrlField)
            ?? record.GetReferenceId(objectConfig.AttachmentUrlField);

        string status;

        // 1. Type allowlist (extension or mime → extension).
        var ext = ContentExtractor.ExtensionOf(fileName)
            ?? ContentExtractor.ContentTypeToExtension(contentType);
        if (ext is null || !_config.AttachmentAllowedTypes.Contains(ext))
        {
            status = $"skipped:type:{ext ?? "unknown"}";
            return Skip(item, record, status, $"type '{ext ?? "unknown"}' not in allowlist");
        }
        if (!_extractor.CanExtract(fileName, contentType))
        {
            status = $"skipped:unsupported:{ext}";
            return Skip(item, record, status, $"no extractor for '{ext}'");
        }

        // 2. Declared size cap (skip before spending a download).
        var declaredSize = ParseSize(record, objectConfig);
        if (declaredSize is { } size && size > _config.AttachmentMaxBytes)
        {
            status = $"skipped:oversize:{size}";
            return Skip(item, record, status,
                $"declared size {size} > cap {_config.AttachmentMaxBytes}");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            status = "skipped:no-url";
            return Skip(item, record, status, "no download URL");
        }

        // 3. Download (hard streamed cap) + extract.
        var download = await _downloader
            .DownloadAttachmentAsync(url!, _config.AttachmentMaxBytes, ct)
            .ConfigureAwait(false);
        if (download.Oversize)
        {
            status = "skipped:oversize";
            return Skip(item, record, status, $"exceeded cap {_config.AttachmentMaxBytes} while downloading");
        }
        if (!download.Ok)
        {
            status = $"skipped:download:{download.Reason}";
            return Skip(item, record, status, $"download failed: {download.Reason}");
        }

        var result = _extractor.Extract(download.Content, fileName, contentType);
        if (!result.Extracted)
        {
            status = $"skipped:{result.Reason}";
            return Skip(item, record, status, $"no text extracted ({result.Reason})");
        }

        AppendContent(item, fileName, result.Text);
        item.Properties[StatusProperty] = "extracted";
        Metrics.IncAttachmentsExtracted();
        Logger.Info(
            $"Extracted {result.Text.Length} chars from attachment '{fileName}' ({record.ItemId}).");
        return "extracted";
    }

    private static string Skip(
        ExternalItem item, ClarizenRecord record, string status, string reason)
    {
        item.Properties[StatusProperty] = status;
        Metrics.IncAttachmentsSkipped();
        Logger.Info($"Attachment '{record.ItemId}' content not indexed — {reason}.");
        return status;
    }

    internal static void AppendContent(ExternalItem item, string fileName, string extracted)
    {
        var header = $"Attachment: {fileName}";
        item.Content = string.IsNullOrEmpty(item.Content)
            ? $"{header}\n{extracted}"
            : $"{item.Content}\n\n{header}\n{extracted}";
    }

    internal static long? ParseSize(ClarizenRecord record, ObjectConfig objectConfig)
    {
        if (string.IsNullOrWhiteSpace(objectConfig.AttachmentSizeField))
            return null;
        var raw = record.GetString(objectConfig.AttachmentSizeField);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : null;
    }
}
