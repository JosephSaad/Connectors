// Config/AppConfig.cs
// -------------------
// Central configuration object, populated from environment variables (after
// EnvLoader has layered env/.env.local + env/.env.local.user). Required
// variables fail fast with "Invalid configuration: Missing <NAME>".

using System.Globalization;
using System.Text.RegularExpressions;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Config;

public sealed partial class AppConfig
{
    // ── Connector identity ───────────────────────────────────────────────────
    public required string ConnectorId { get; init; }
    public required string ConnectorName { get; init; }
    public required string ConnectorDescription { get; init; }

    // ── BDH source access (HDFS_MODE) ────────────────────────────────────────
    /// <summary>webhdfs (default) | localpath.</summary>
    public required string HdfsMode { get; init; }

    /// <summary>WebHDFS endpoint incl. the /webhdfs/v1 prefix, e.g.
    /// http://namenode:9870/webhdfs/v1 (or a Knox gateway URL).</summary>
    public string? HdfsNamenodeUrl { get; init; }

    /// <summary>Simple-auth user (?user.name=). Optional.</summary>
    public string? HdfsUser { get; init; }

    /// <summary>HDFS delegation token (?delegation=, SECRET_HDFS_DELEGATION_TOKEN). Optional.</summary>
    public string? HdfsDelegationToken { get; init; }

    /// <summary>Local/SMB directory with the BDH layout (HDFS_MODE=localpath).</summary>
    public string? BdhExportPath { get; init; }

    /// <summary>HDFS path of the BDH data-mart root (webhdfs mode), default /data/bdh.</summary>
    public string BdhRootPath { get; init; } = "/data/bdh";

    // ── Freshness / scale ────────────────────────────────────────────────────
    /// <summary>BDH sync lag: incremental crawls re-read partitions this many
    /// hours behind the last sync watermark (default 24 — BDH loads nightly).</summary>
    public int BdhLagHours { get; init; } = 24;

    /// <summary>Row-cap safety valve per object per crawl (default 500000;
    /// 0 disables). Hitting it marks the crawl partial — never silent.</summary>
    public int BdhMaxRecordsPerObject { get; init; } = 500_000;

    /// <summary>Per-file read bound in bytes (default 1 GiB).</summary>
    public long BdhMaxFileBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>ALLOW_FULL_SCAN=true disables the fail-closed scale guard globally
    /// (an object with no filter refuses to crawl otherwise).</summary>
    public bool AllowFullScan { get; init; }

    /// <summary>BDH object holding the Salesforce user directory (identity sync).</summary>
    public string IdentityObject { get; init; } = "User";

    /// <summary>Base URL for record deep links (the live Salesforce org, so
    /// result cards open the real record), e.g. https://org.lightning.force.com.</summary>
    public string ItemUrlBase { get; init; } = "https://login.salesforce.com";

    // ── Microsoft Graph / Entra ──────────────────────────────────────────────
    public required string AadTenantId { get; init; }
    public required string AadClientId { get; init; }
    /// <summary>Client secret (SECRET_AAD_APP_CLIENT_SECRET). Empty when a
    /// certificate credential is configured — the certificate wins.</summary>
    public required string AadClientSecret { get; init; }

    /// <summary>PFX/PEM file for the certificate credential (GRAPH_CLIENT_CERT_PATH).</summary>
    public string? GraphClientCertPath { get; init; }
    /// <summary>PFX password (GRAPH_CLIENT_CERT_PASSWORD; SECRET-style, Key Vault supported).</summary>
    public string? GraphClientCertPassword { get; init; }
    /// <summary>Windows cert-store thumbprint lookup (GRAPH_CLIENT_CERT_THUMBPRINT).</summary>
    public string? GraphClientCertThumbprint { get; init; }

    /// <summary>True when a certificate credential is configured (path or
    /// thumbprint). The certificate always wins over a client secret.</summary>
    public bool HasGraphClientCertificate =>
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

    // ── Unified data classification & sensitivity labeling (docs/CLASSIFICATION.md) ─
    /// <summary>CLASSIFICATION — derive SensitivityLabel + DetectedCategories. Default OFF.</summary>
    public bool Classification { get; init; }
    /// <summary>CLASSIFICATION_MANIFEST — write a per-crawl classification JSONL. Default OFF.</summary>
    public bool ClassificationManifest { get; init; }

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

        var hdfsMode = EnvFlags.GetString("HDFS_MODE", "webhdfs").ToLowerInvariant();
        if (hdfsMode is not ("webhdfs" or "localpath"))
            throw new ArgumentException(
                "Invalid configuration: HDFS_MODE must be webhdfs or localpath.");

        var namenodeUrl = Environment.GetEnvironmentVariable("HDFS_NAMENODE_URL")?.TrimEnd('/');
        var exportPath = Environment.GetEnvironmentVariable("BDH_EXPORT_PATH");
        if (hdfsMode == "webhdfs" && string.IsNullOrWhiteSpace(namenodeUrl))
            throw new ArgumentException("Invalid configuration: Missing HDFS_NAMENODE_URL");
        if (hdfsMode == "localpath" && string.IsNullOrWhiteSpace(exportPath))
            throw new ArgumentException("Invalid configuration: Missing BDH_EXPORT_PATH");

        // Graph certificate credential (docs/DEPLOYMENT_ENTERPRISE.md): a file
        // path or a Windows-store thumbprint. Certificate wins over the secret.
        var certPath = Environment.GetEnvironmentVariable("GRAPH_CLIENT_CERT_PATH");
        var certThumbprint = Environment.GetEnvironmentVariable("GRAPH_CLIENT_CERT_THUMBPRINT");
        var certConfigured = !string.IsNullOrWhiteSpace(certPath)
            || !string.IsNullOrWhiteSpace(certThumbprint);

        return new AppConfig
        {
            ConnectorId = connectorId,
            ConnectorName = EnvFlags.GetString("CONNECTOR_NAME", "BDH Hadoop Data Mart"),
            ConnectorDescription = EnvFlags.GetString(
                "CONNECTOR_DESCRIPTION",
                "Salesforce data from the BDH Hadoop data mart (nightly sync) "
                + "indexed for Microsoft 365 Copilot."),

            HdfsMode = hdfsMode,
            HdfsNamenodeUrl = namenodeUrl,
            HdfsUser = Environment.GetEnvironmentVariable("HDFS_USER"),
            HdfsDelegationToken = SecretProvider.GetSecret("SECRET_HDFS_DELEGATION_TOKEN"),
            BdhExportPath = exportPath,
            BdhRootPath = "/" + EnvFlags.GetString("BDH_ROOT_PATH", "/data/bdh").Trim('/'),

            BdhLagHours = Math.Clamp(EnvFlags.GetInt("BDH_LAG_HOURS", 24), 0, 24 * 14),
            BdhMaxRecordsPerObject = Math.Max(0, EnvFlags.GetInt("BDH_MAX_RECORDS_PER_OBJECT", 500_000)),
            BdhMaxFileBytes = Math.Max(1, ParseLong("BDH_MAX_FILE_BYTES", 1024L * 1024 * 1024)),
            AllowFullScan = EnvFlags.IsTrue("ALLOW_FULL_SCAN"),
            IdentityObject = EnvFlags.GetString("BDH_IDENTITY_OBJECT", "User"),
            ItemUrlBase = EnvFlags.GetString("ITEM_URL_BASE", "https://login.salesforce.com").TrimEnd('/'),

            AadTenantId = Require("AAD_APP_TENANT_ID"),
            AadClientId = Require("AAD_APP_CLIENT_ID"),
            // With a certificate credential configured the client secret is
            // optional (and ignored — the certificate wins); without one it
            // stays REQUIRED exactly as before.
            AadClientSecret = SecretProvider.GetSecret("SECRET_AAD_APP_CLIENT_SECRET")
                ?? (certConfigured ? string.Empty : throw Missing("SECRET_AAD_APP_CLIENT_SECRET")),
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

            Classification = EnvFlags.IsTrue("CLASSIFICATION"),
            ClassificationManifest = EnvFlags.IsTrue("CLASSIFICATION_MANIFEST"),
        };
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
        HdfsMode = HdfsMode,
        HdfsNamenodeUrl = HdfsNamenodeUrl,
        HdfsUser = HdfsUser,
        HdfsDelegationToken = HdfsDelegationToken,
        BdhExportPath = BdhExportPath,
        BdhRootPath = BdhRootPath,
        BdhLagHours = BdhLagHours,
        BdhMaxRecordsPerObject = BdhMaxRecordsPerObject,
        BdhMaxFileBytes = BdhMaxFileBytes,
        AllowFullScan = AllowFullScan,
        IdentityObject = IdentityObject,
        ItemUrlBase = ItemUrlBase,
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
        Classification = Classification,
        ClassificationManifest = ClassificationManifest,
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
