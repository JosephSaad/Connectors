# Attachment content ingestion

By default the connector indexes attachment **metadata** only (name, size,
type, parent). With `ATTACHMENT_INGESTION=true` it also downloads the file
binary, extracts its **text**, and appends that text to the attachment's
externalItem content so Copilot / Microsoft Search can ground on what the file
actually says.

> **Off by default.** With `ATTACHMENT_INGESTION` unset the behaviour is
> unchanged — no downloads, no extraction, attachments stay metadata-only.

## Why enrich the existing item (not child items)

The `Attachment` object type is already a first-class ingested externalItem:
it inherits its parent's ACLs (`aclMode: projectMembers` + the `AttachedTo`
project field), it is recorded in the ingested-item inventory, and it is
withdrawn by the deletion sweep when the parent/attachment disappears. So
content ingestion is purely **additive** — the extracted text is appended to
that item's content body. No new item type, no separate ACL path, and deletion
sync / reconcile keep working with zero extra wiring.

## Configuration

Mark an object type as an attachment carrier in `config/schema.json`:

```json
{
  "objectName": "Attachment",
  "aclMode": "projectMembers",
  "projectField": "AttachedTo",
  "attachmentUrlField": "DownloadUrl",
  "attachmentNameField": "Name",
  "attachmentContentTypeField": "FileType",
  "attachmentSizeField": "FileSize",
  "selectedFields": { "...": "...", "DownloadUrl": "_cz_DownloadUrl" }
}
```

- `attachmentUrlField` — the Clarizen field holding the download URL. Its
  presence is what makes the object an attachment carrier. Add it to
  `selectedFields` (as a `_cz_` placeholder so it is queried but not indexed as
  a property).
- `attachmentNameField` (default `Name`) drives extension detection.
- `attachmentContentTypeField` / `attachmentSizeField` are optional hints; the
  size field lets oversize files be skipped **before** a download is spent.

Env knobs:

| Var | Default | Meaning |
|---|---|---|
| `ATTACHMENT_INGESTION` | `false` | Master switch. |
| `ATTACHMENT_MAX_BYTES` | `10485760` (10 MiB) | Per-file size cap. Enforced against the declared size and again as a hard streamed cap during download. |
| `ATTACHMENT_ALLOWED_TYPES` | `txt,csv,tsv,log,md,json,xml,htm,html,docx,xlsx,pptx,pdf` | Extension/mime allowlist. |

## Extraction (dependency-free)

Text extraction runs behind a pluggable `IContentExtractor`; the default
`ContentExtractor` uses only the BCL — no third-party parsers:

| Family | How |
|---|---|
| `txt` / `csv` / `tsv` / `log` / `md` / `json` / `xml` | UTF-8 (BOM-aware) with a Latin-1 fallback |
| `htm` / `html` | tags stripped, entities decoded |
| `docx` | unzip → `word/document.xml`, stream `<w:t>` runs via `XmlReader` |
| `xlsx` | shared strings + inline cell `<t>` |
| `pptx` | slide `<a:t>` runs, in slide-number order |
| `pdf` | best-effort text layer: inflate FlateDecode streams, pull `Tj`/`TJ` literals. **Image-only / scanned PDFs yield no text and are skipped — never OCR'd.** |

Anything else (images, archives, unknown binary) is skipped to metadata-only.
Extraction **never** throws — a parse failure is a skip with a reason, never a
failed crawl. Extracted text is whitespace-normalised and capped at 512 KiB per
file to keep the Graph payload bounded.

Decompression is bounded too: `ATTACHMENT_MAX_BYTES` caps the *download*, and a
separate inflation ceiling (4x the extracted-text cap) bounds what a compressed
payload may expand to *during* extraction — PDF FlateDecode streams are
truncated at the ceiling and OOXML text accumulation stops there. A
decompression bomb (a ~1 MB upload inflating to gigabytes) is truncated, never
buffered whole.

## What you see

Every attachment item gets an `AttachmentExtractionStatus` property:

- `extracted` — text was indexed;
- `skipped:type:<ext>` — extension not in the allowlist;
- `skipped:oversize[:<bytes>]` — over `ATTACHMENT_MAX_BYTES`;
- `skipped:download:<reason>` — download failed;
- `skipped:no-text` / `skipped:<reason>` — nothing extractable.

Metrics: `clarizen_connector_attachments_extracted_total` and
`clarizen_connector_attachments_skipped_total`. Skips are logged with the file
name and reason.

## Cost note

Each downloaded attachment spends one Clarizen API call from the daily budget
(`CLARIZEN_API_CALLS_PER_DAY`) in addition to the record query. Size caps and
the allowlist keep this bounded; disable the feature or narrow the allowlist if
your attachment volume is high relative to your quota.
