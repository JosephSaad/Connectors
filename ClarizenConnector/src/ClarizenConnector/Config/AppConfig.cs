// Config/AppConfig.cs
// -------------------
// Central configuration object, populated from environment variables (after
// EnvLoader has layered env/.env.local + env/.env.local.user). Required
// variables fail fast with "Invalid configuration: Missing <NAME>".

using System.Globalization;
using System.Text.RegularExpressions;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Config;

public sealed partial class AppConfig
{
    // ── Connector identity ───────────────────────────────────────────────────
    public required string ConnectorId { get; init; }
    public required string ConnectorName { get; init; }
    public required string ConnectorDescription { get; init; }

    // ── Clarizen ─────────────────────────────────────────────────────────────
    public required string ClarizenBaseUrl { get; init; }
    public required string ClarizenUsername { get; init; }
    public required string ClarizenPassword { get; init; }
    public int ClarizenApiCallsPerDay { get; init; } = 25_000;
    public int ClarizenPageSize { get; init; } = 500;
    public int ClarizenQueryLimit { get; init; }  // 0 = no limit (full pagination)
    public string? TdwExportPath { get; init; }

    // ── Microsoft Graph / Entra ──────────────────────────────────────────────
    public required string AadTenantId { get; init; }
    public required string AadClientId { get; init; }
    /// <summary>Client secret; optional when a certificate credential is configured.</summary>
    public string? AadClientSecret { get; init; }
    /// <summary>PFX/PEM certificate file for the Graph client-credential flow
    /// (GRAPH_CLIENT_CERT_PATH). The certificate credential wins over the secret.</summary>
    public string? GraphClientCertPath { get; init; }
    /// <summary>Password for an encrypted GRAPH_CLIENT_CERT_PATH file (optional).</summary>
    public string? GraphClientCertPassword { get; init; }
    /// <summary>Windows-store thumbprint lookup (GRAPH_CLIENT_CERT_THUMBPRINT).</summary>
    public string? GraphClientCertThumbprint { get; init; }

    /// <summary>True when a certificate credential is configured (wins over the secret).</summary>
    public bool UseGraphCertificate =>
        !string.IsNullOrWhiteSpace(GraphClientCertPath)
        || !string.IsNullOrWhiteSpace(GraphClientCertThumbprint);
    /// <summary>Token authority host (AAD_APP_OAUTH_AUTHORITY_HOST) — sovereign clouds
    /// use e.g. https://login.microsoftonline.us.</summary>
    public string AadAuthorityHost { get; init; } = "https://login.microsoftonline.com";
    public string GraphBaseUrl { get; init; } = "https://graph.microsoft.com";
    public string GraphApiVersion { get; init; } = "v1.0";
    public string? GraphScope { get; init; }
    public int GraphMaxRetries { get; init; } = 4;
    public double GraphRetryBackoffBase { get; init; } = 2.0;

    // ── Batching & parallelism ───────────────────────────────────────────────
    public int IngestChunkSize { get; init; } = 200;
    public int GraphBatchSize { get; init; } = 20;   // hard Graph $batch cap
    /// <summary>Max concurrent Graph $batch workers (GRAPH_CONCURRENT_BATCHES wins
    /// over GRAPH_BATCH_WORKERS; default 8). Adaptive: dialled 1..max on 429s.</summary>
    public int GraphBatchWorkers { get; init; } = 8;

    // ── Provisioning ─────────────────────────────────────────────────────────
    public int ConnectionTimeoutSeconds { get; init; } = 600;
    public int ConnectionRetryIntervalSeconds { get; init; } = 15;

    // ── Financial-field classification ───────────────────────────────────────
    public string? FinancialDataGroupId { get; init; }
    /// <summary>tag | filter | acl — default <c>filter</c> (protective: financial
    /// values are redacted). See docs/OBSERVABILITY.md and README.</summary>
    public string FinancialDataMode { get; init; } = "filter";

    // ── Attachment content ingestion (docs/ATTACHMENTS.md) ───────────────────
    /// <summary>ATTACHMENT_INGESTION — download + extract attachment text. Default OFF.</summary>
    public bool AttachmentIngestion { get; init; }
    /// <summary>ATTACHMENT_MAX_BYTES — per-file size cap (default 10 MiB).</summary>
    public long AttachmentMaxBytes { get; init; } = 10L * 1024 * 1024;
    /// <summary>ATTACHMENT_ALLOWED_TYPES — extension allowlist (lower-case, no dot).</summary>
    public IReadOnlySet<string> AttachmentAllowedTypes { get; init; } =
        DefaultAttachmentTypes;

    // ── Unified data classification & sensitivity labeling (docs/CLASSIFICATION.md) ─
    /// <summary>CLASSIFICATION — derive SensitivityLabel + DetectedCategories. Default OFF.
    /// NOTE: SensitivityLabel is a connector-applied ADVISORY classification tag,
    /// NOT a Microsoft Purview-enforced sensitivity label.</summary>
    public bool Classification { get; init; }
    /// <summary>CLASSIFICATION_MANIFEST — write a per-crawl classification JSONL. Default OFF.</summary>
    public bool ClassificationManifest { get; init; }
    /// <summary>CLASSIFICATION_ENFORCE_ACL — when true (and a group is configured),
    /// top-tier (Restricted) items get their ACL grants replaced with a single
    /// grant to <see cref="ClassificationRestrictedGroupId"/> (denies preserved).
    /// Default OFF (the classification tag stays advisory). Requires CLASSIFICATION.</summary>
    public bool ClassificationEnforceAcl { get; init; }
    /// <summary>CLASSIFICATION_RESTRICTED_GROUP_ID — Entra group that Restricted
    /// items are locked to when CLASSIFICATION_ENFORCE_ACL is on.</summary>
    public string? ClassificationRestrictedGroupId { get; init; }

    // ── Stale-index expiry (docs/DELETION_SYNC.md) ───────────────────────────
    /// <summary>GRAPH_ITEM_TTL_DAYS — when &gt; 0, ingested items are stamped with
    /// expirationDateTime = now + TTL so the search index self-expires if crawling
    /// stops (defense-in-depth after an outage). 0 (default/unset) = no expiry.</summary>
    public int GraphItemTtlDays { get; init; }

    internal static readonly IReadOnlySet<string> DefaultAttachmentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "txt", "csv", "tsv", "log", "md", "json", "xml", "htm", "html",
            "docx", "xlsx", "pptx", "pdf",
        };

    /// <summary>Reserved Graph connection-id prefixes (Microsoft first-party).</summary>
    internal static readonly string[] ReservedPrefixes =
    {
        "Microsoft", "SharePoint", "Teams", "Exchange", "OneDriveBusiness",
        "LinkedIn", "Yammer", "Connectors", "PowerBI", "Office365", "Viva",
    };

    [GeneratedRegex("^[A-Za-z0-9]{3,32}$")]
    private static partial Regex ConnectorIdPattern();

    /// <summary>Validate a Graph external connection id; returns an error message or null when valid.</summary>
    public static string? ValidateConnectorId(string connectorId)
    {
        if (!ConnectorIdPattern().IsMatch(connectorId))
            return "CONNECTOR_ID must be 3-32 alphanumeric characters (no spaces or punctuation).";
        foreach (var prefix in ReservedPrefixes)
        {
            if (connectorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return $"CONNECTOR_ID must not start with the reserved prefix '{prefix}'.";
        }
        return null;
    }

    /// <summary>Load config from the environment, throwing on missing/invalid required values.</summary>
    public static AppConfig Load()
    {
        var connectorId = Require("CONNECTOR_ID");
        var idError = ValidateConnectorId(connectorId);
        if (idError is not null)
            throw new ArgumentException($"Invalid configuration: {idError}");

        // FINANCIAL_DATA_MODE default is `filter` (protective: financial values
        // are redacted). `tag` classifies but does NOT restrict — an operator
        // must opt into it deliberately.
        var mode = EnvFlags.GetString("FINANCIAL_DATA_MODE", "filter").ToLowerInvariant();
        if (mode is not ("tag" or "filter" or "acl"))
            throw new ArgumentException(
                "Invalid configuration: FINANCIAL_DATA_MODE must be one of tag | filter | acl.");

        var financialGroup = Environment.GetEnvironmentVariable("FINANCIAL_DATA_GROUP_ID");
        if (mode == "acl" && string.IsNullOrWhiteSpace(financialGroup))
            throw new ArgumentException(
                "Invalid configuration: FINANCIAL_DATA_MODE=acl requires FINANCIAL_DATA_GROUP_ID.");

        // Classification ACL enforcement needs a target group — otherwise there
        // is nothing to lock Restricted items to (a silent no-op would be a
        // governance surprise).
        var classificationEnforceAcl = EnvFlags.IsTrue("CLASSIFICATION_ENFORCE_ACL");
        var classificationRestrictedGroup =
            Environment.GetEnvironmentVariable("CLASSIFICATION_RESTRICTED_GROUP_ID");
        if (classificationEnforceAcl && string.IsNullOrWhiteSpace(classificationRestrictedGroup))
            throw new ArgumentException(
                "Invalid configuration: CLASSIFICATION_ENFORCE_ACL=true requires "
                + "CLASSIFICATION_RESTRICTED_GROUP_ID.");

        // DEADLETTER_PAYLOAD_MODE must be an exact known value — a typo silently
        // meaning "full" would be a data-governance hazard (docs/THREAT_MODEL.md).
        // The shipped default is `redacted` (protective by default).
        var deadLetterMode = EnvFlags.GetString(
            Infrastructure.DeadLetterRedactor.ModeEnvVar, "redacted").ToLowerInvariant();
        if (deadLetterMode is not ("full" or "redacted"))
            throw new ArgumentException(
                $"Invalid configuration: {Infrastructure.DeadLetterRedactor.ModeEnvVar} "
                + "must be one of full | redacted.");

        // Proxy / custom trust roots fail fast here, naming the setting, rather
        // than surfacing later as an opaque TLS or connect error mid-crawl.
        Infrastructure.HttpClientFactory.ValidateConfiguration();

        // Graph credential: certificate (path or Windows-store thumbprint) wins
        // over the client secret; at least one of the two must be configured.
        var certPath = Environment.GetEnvironmentVariable("GRAPH_CLIENT_CERT_PATH");
        var certThumbprint = Environment.GetEnvironmentVariable("GRAPH_CLIENT_CERT_THUMBPRINT");
        if (!string.IsNullOrWhiteSpace(certPath) && !File.Exists(certPath))
            throw new ArgumentException(
                $"Invalid configuration: GRAPH_CLIENT_CERT_PATH '{certPath}' does not exist.");
        if (string.IsNullOrWhiteSpace(certPath)
            && !string.IsNullOrWhiteSpace(certThumbprint)
            && !OperatingSystem.IsWindows())
        {
            throw new ArgumentException(
                "Invalid configuration: GRAPH_CLIENT_CERT_THUMBPRINT requires the Windows "
                + "certificate store — use GRAPH_CLIENT_CERT_PATH on this platform.");
        }
        var clientSecret = SecretProvider.GetSecret("SECRET_AAD_APP_CLIENT_SECRET");
        if (string.IsNullOrEmpty(clientSecret)
            && string.IsNullOrWhiteSpace(certPath)
            && string.IsNullOrWhiteSpace(certThumbprint))
        {
            throw new ArgumentException(
                "Invalid configuration: Missing SECRET_AAD_APP_CLIENT_SECRET "
                + "(or configure GRAPH_CLIENT_CERT_PATH / GRAPH_CLIENT_CERT_THUMBPRINT "
                + "for certificate auth).");
        }

        return new AppConfig
        {
            ConnectorId = connectorId,
            ConnectorName = EnvFlags.GetString("CONNECTOR_NAME", "Clarizen AdaptiveWork"),
            ConnectorDescription = EnvFlags.GetString(
                "CONNECTOR_DESCRIPTION",
                "Planview AdaptiveWork (Clarizen) work items synced to Microsoft 365 Copilot."),

            ClarizenBaseUrl = EnvFlags.GetString(
                "CLARIZEN_BASE_URL", "https://api.clarizen.com/v2.0/services").TrimEnd('/'),
            ClarizenUsername = Require("CLARIZEN_USERNAME"),
            ClarizenPassword = SecretProvider.GetSecret("SECRET_CLARIZEN_PASSWORD")
                ?? throw Missing("SECRET_CLARIZEN_PASSWORD"),
            ClarizenApiCallsPerDay = EnvFlags.GetInt("CLARIZEN_API_CALLS_PER_DAY", 25_000),
            ClarizenPageSize = Math.Clamp(EnvFlags.GetInt("CLARIZEN_PAGE_SIZE", 500), 1, 5000),
            ClarizenQueryLimit = Math.Max(0, EnvFlags.GetInt("CLARIZEN_QUERY_LIMIT", 0)),
            TdwExportPath = Environment.GetEnvironmentVariable("TDW_EXPORT_PATH"),

            AadTenantId = Require("AAD_APP_TENANT_ID"),
            AadClientId = Require("AAD_APP_CLIENT_ID"),
            AadClientSecret = clientSecret,
            GraphClientCertPath = certPath,
            GraphClientCertPassword = SecretProvider.GetSecret("GRAPH_CLIENT_CERT_PASSWORD"),
            GraphClientCertThumbprint = certThumbprint,
            AadAuthorityHost = EnvFlags.GetString(
                "AAD_APP_OAUTH_AUTHORITY_HOST", "https://login.microsoftonline.com").TrimEnd('/'),
            GraphBaseUrl = EnvFlags.GetString("GRAPH_BASE_URL", "https://graph.microsoft.com").TrimEnd('/'),
            GraphApiVersion = EnvFlags.GetString("GRAPH_API_VERSION", "v1.0"),
            GraphScope = Environment.GetEnvironmentVariable("GRAPH_SCOPE"),
            GraphMaxRetries = Math.Max(0, EnvFlags.GetInt("GRAPH_MAX_RETRIES", 4)),
            GraphRetryBackoffBase = ParseDouble("GRAPH_RETRY_BACKOFF_BASE", 2.0),

            IngestChunkSize = Math.Clamp(EnvFlags.GetInt("INGEST_CHUNK_SIZE", 200), 1, 5000),
            // INGEST_GRAPH_BATCH_SIZE is the documented knob; GRAPH_BATCH_SIZE is
            // an accepted alias (SF parity). Explicit INGEST_GRAPH_BATCH_SIZE wins.
            GraphBatchSize = Math.Clamp(
                EnvFlags.GetInt("INGEST_GRAPH_BATCH_SIZE", EnvFlags.GetInt("GRAPH_BATCH_SIZE", 20)),
                1, 20),
            // GRAPH_CONCURRENT_BATCHES is the historical name and WINS when both
            // are set; GRAPH_BATCH_WORKERS is the documented alias. Default 8.
            GraphBatchWorkers = Math.Clamp(
                EnvFlags.GetInt("GRAPH_CONCURRENT_BATCHES", EnvFlags.GetInt("GRAPH_BATCH_WORKERS", 8)),
                1, 64),

            ConnectionTimeoutSeconds = EnvFlags.GetInt("CONNECTION_TIMEOUT_SECONDS", 600),
            ConnectionRetryIntervalSeconds = EnvFlags.GetInt("CONNECTION_RETRY_INTERVAL_SECONDS", 15),

            FinancialDataGroupId = financialGroup,
            FinancialDataMode = mode,

            AttachmentIngestion = EnvFlags.IsTrue("ATTACHMENT_INGESTION"),
            AttachmentMaxBytes = Math.Max(1, ParseLong("ATTACHMENT_MAX_BYTES", 10L * 1024 * 1024)),
            AttachmentAllowedTypes = ParseAllowedTypes(
                Environment.GetEnvironmentVariable("ATTACHMENT_ALLOWED_TYPES")),

            Classification = EnvFlags.IsTrue("CLASSIFICATION"),
            ClassificationManifest = EnvFlags.IsTrue("CLASSIFICATION_MANIFEST"),
            ClassificationEnforceAcl = classificationEnforceAcl,
            ClassificationRestrictedGroupId = classificationRestrictedGroup,

            GraphItemTtlDays = Math.Max(0, EnvFlags.GetInt("GRAPH_ITEM_TTL_DAYS", 0)),
        };
    }

    /// <summary>Parse ATTACHMENT_ALLOWED_TYPES (comma/space list of extensions or
    /// mime types) into an extension allowlist; unset → the built-in default.</summary>
    internal static IReadOnlySet<string> ParseAllowedTypes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultAttachmentTypes;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = token.Trim().TrimStart('.').ToLowerInvariant();
            if (value.Length == 0)
                continue;
            // Accept mime types too, mapping them to their extension.
            if (value.Contains('/'))
            {
                var ext = Content.ContentExtractor.ContentTypeToExtension(value);
                if (ext is not null)
                    set.Add(ext);
            }
            else
            {
                set.Add(value);
            }
        }
        return set.Count == 0 ? DefaultAttachmentTypes : set;
    }

    /// <summary>
    /// Clone this config bound to a different Graph external connection id —
    /// the sharding building block (GRAPH_CONNECTION_SHARDS). Every state key
    /// derived from the connector id (checkpoints, dead-letter, sync
    /// timestamps, identity DB, item URLs) follows the shard connection.
    /// </summary>
    public AppConfig CloneForConnection(string connectionId) => new()
    {
        ConnectorId = connectionId,
        ConnectorName = ConnectorName,
        ConnectorDescription = ConnectorDescription,
        ClarizenBaseUrl = ClarizenBaseUrl,
        ClarizenUsername = ClarizenUsername,
        ClarizenPassword = ClarizenPassword,
        ClarizenApiCallsPerDay = ClarizenApiCallsPerDay,
        ClarizenPageSize = ClarizenPageSize,
        ClarizenQueryLimit = ClarizenQueryLimit,
        TdwExportPath = TdwExportPath,
        AadTenantId = AadTenantId,
        AadClientId = AadClientId,
        AadClientSecret = AadClientSecret,
        GraphClientCertPath = GraphClientCertPath,
        GraphClientCertPassword = GraphClientCertPassword,
        GraphClientCertThumbprint = GraphClientCertThumbprint,
        AadAuthorityHost = AadAuthorityHost,
        GraphBaseUrl = GraphBaseUrl,
        GraphApiVersion = GraphApiVersion,
        GraphScope = GraphScope,
        GraphMaxRetries = GraphMaxRetries,
        GraphRetryBackoffBase = GraphRetryBackoffBase,
        IngestChunkSize = IngestChunkSize,
        GraphBatchSize = GraphBatchSize,
        GraphBatchWorkers = GraphBatchWorkers,
        ConnectionTimeoutSeconds = ConnectionTimeoutSeconds,
        ConnectionRetryIntervalSeconds = ConnectionRetryIntervalSeconds,
        FinancialDataGroupId = FinancialDataGroupId,
        FinancialDataMode = FinancialDataMode,
        AttachmentIngestion = AttachmentIngestion,
        AttachmentMaxBytes = AttachmentMaxBytes,
        AttachmentAllowedTypes = AttachmentAllowedTypes,
        Classification = Classification,
        ClassificationManifest = ClassificationManifest,
        ClassificationEnforceAcl = ClassificationEnforceAcl,
        ClassificationRestrictedGroupId = ClassificationRestrictedGroupId,
        GraphItemTtlDays = GraphItemTtlDays,
    };

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw Missing(name);

    private static ArgumentException Missing(string name) =>
        new($"Invalid configuration: Missing {name}");

    private static double ParseDouble(string name, double defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static long ParseLong(string name, long defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }
}
