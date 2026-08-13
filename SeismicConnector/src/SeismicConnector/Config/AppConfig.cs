// Config/AppConfig.cs
// -------------------
// Strongly-typed snapshot of every setting the connector reads, built once per
// command from the (layered) environment plus config/schema.json and
// config/exclusions.json. Commands pass this object down; nothing below the
// command layer reads the environment for core settings.

using System.Text.Json;
using System.Text.Json.Serialization;
using SeismicConnector.Infrastructure;
using SeismicConnector.Security;
using SeismicConnector.Seismic;

namespace SeismicConnector.Config;

public sealed class ConnectorSettings
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
}

public sealed class SeismicSettings
{
    /// <summary>Seismic tenant (subdomain), e.g. <c>contoso</c>.</summary>
    public required string Tenant { get; init; }

    /// <summary>API base, default <c>https://api.seismic.com</c>.</summary>
    public required string ApiBaseUrl { get; init; }

    /// <summary>Token endpoint, default <c>https://auth.seismic.com/tenants/{tenant}/connect/token</c>.</summary>
    public required string TokenUrl { get; init; }

    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }

    /// <summary>Page size for Seismic list endpoints (default 100, max 500).</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>Optional dev/debug cap on items fetched per teamsite. 0 = unlimited.</summary>
    public int QueryLimit { get; init; }

    /// <summary>Max retries against the Seismic API (429/5xx).</summary>
    public int MaxRetries { get; init; } = 4;

    /// <summary>Max bytes downloaded per item for text extraction (default 10 MB).</summary>
    public long MaxExtractBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Optional webhook listener port for near-real-time events. 0 = disabled.</summary>
    public int WebhookPort { get; init; }

    /// <summary>
    /// SEISMIC_ENRICH_USAGE=true → pull per-content usage analytics
    /// (views/downloads/shares) each crawl and index them as refinable
    /// properties so Copilot can rank popular collateral higher. Default off.
    /// </summary>
    public bool EnrichUsage { get; init; }

    /// <summary>
    /// LIVEDOC_FIELD_INDEXING=true → for LiveDoc content items, fetch their
    /// field/variable metadata and index the input names/labels (as refinable
    /// properties and in the searchable content text). Default off; the extra
    /// API call is made only for LiveDoc items and only when this is set.
    /// </summary>
    public bool LiveDocFieldIndexing { get; init; }

    /// <summary>
    /// PERMISSION_REACL=true → on each crawl, re-resolve permissions for
    /// content whose payload did NOT change and re-ACL (ACL-only Graph PATCH,
    /// no content re-send) any item whose resolved principal set drifted, so a
    /// teamsite/content-profile permission change is reflected in the index
    /// even when the content itself is untouched. Default off (behaviour
    /// unchanged). The `reacl` command works regardless of this flag.
    /// </summary>
    public bool PermissionReacl { get; init; }

    /// <summary>
    /// CLASSIFICATION=true → run the dependency-free content classifier over the
    /// indexed text of every ingested item and stamp an ADVISORY, connector-
    /// applied sensitivity classification TAG (the <c>advisorySensitivity</c>
    /// property) + detected categories. This is NOT a Microsoft Purview-enforced
    /// label — it carries no encryption/DLP enforcement; it is a search/triage
    /// signal (Purview-aligned in naming only). Default off (behaviour
    /// unchanged). Never weakens the No-MNE exclusion — excluded content is
    /// still never ingested; classification applies only to what IS ingested.
    /// </summary>
    public bool Classification { get; init; }

    /// <summary>
    /// CLASSIFICATION_MANIFEST=true → additionally write a per-crawl
    /// classification manifest (item → advisory tag + categories, plus counts)
    /// for a downstream catalog/DLP job. Purview-aligned in naming only — no
    /// Purview API call. Requires CLASSIFICATION=true. Default off.
    /// </summary>
    public bool ClassificationManifest { get; init; }

    /// <summary>
    /// ACL fallback when no Seismic principal on an item maps to Entra:
    /// <c>skip</c> (default; item not ingested — compliance-safe) or
    /// <c>tenant</c> (grant everyone in the tenant).
    /// </summary>
    public string FallbackAcl { get; init; } = "skip";

    /// <summary>
    /// GRAPH_ITEM_TTL_DAYS: when &gt; 0, every ingested externalItem is stamped
    /// with <c>expirationDateTime = now + TTL</c> so the index self-expires if
    /// crawling stops (defense-in-depth after an outage — a stale, no-longer-
    /// refreshed item ages out instead of lingering forever). 0 = unset =
    /// current behaviour (items never auto-expire; withdrawal is crawl-driven).
    /// </summary>
    public int GraphItemTtlDays { get; init; }

    /// <summary>
    /// CLASSIFICATION_ENFORCE_ACL: when true (and an Entra enforcement group is
    /// configured) items whose derived sensitivity label is the top tier
    /// (<c>Restricted</c>) have their ACL RESTRICTED to that group. Default off
    /// (the sensitivity label stays advisory only). Requires CLASSIFICATION=true
    /// to have any effect.
    /// </summary>
    public bool ClassificationEnforceAcl { get; init; }

    /// <summary>CLASSIFICATION_ENFORCE_GROUP: Entra group object id that Restricted
    /// items are locked to when <see cref="ClassificationEnforceAcl"/> is on.</summary>
    public string? ClassificationEnforceGroup { get; init; }
}

public sealed class GraphSettings
{
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }

    /// <summary>GRAPH_CLIENT_CERT_PATH: PFX/PEM file for certificate (client_assertion)
    /// auth. When set (or the thumbprint is), the certificate WINS over the client
    /// secret. Null = secret auth (default).</summary>
    public string? ClientCertPath { get; init; }

    /// <summary>GRAPH_CLIENT_CERT_PASSWORD: optional PFX/encrypted-PEM password.</summary>
    public string? ClientCertPassword { get; init; }

    /// <summary>GRAPH_CLIENT_CERT_THUMBPRINT: certificate store lookup alternative to the path.</summary>
    public string? ClientCertThumbprint { get; init; }
    public string BaseUrl { get; init; } = "https://graph.microsoft.com";
    public string ApiVersion { get; init; } = "v1.0";
    public int MaxRetries { get; init; } = 4;
    public int RetryBackoffBase { get; init; } = 2;
    public int ConnectionTimeoutSeconds { get; init; } = 600;
    public int ConnectionRetryIntervalSeconds { get; init; } = 15;
    public int SchemaRetryIntervalSeconds { get; init; } = 15;
}

public sealed class IngestSettings
{
    /// <summary>Content items per checkpoint chunk (default 200).</summary>
    public int ChunkSize { get; init; } = 200;

    /// <summary>Requests per Graph $batch POST (API cap 20).</summary>
    public int GraphBatchSize { get; init; } = 20;

    /// <summary>Concurrent Graph $batch workers.</summary>
    public int BatchWorkers { get; init; } = 4;
}

/// <summary>One ingestable object type from config/schema.json.</summary>
public sealed class SchemaObject
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>Property name on the Seismic item carrying the expiry date, if any.</summary>
    [JsonPropertyName("expiryProperty")]
    public string? ExpiryProperty { get; init; }
}

public sealed class SchemaConfig
{
    [JsonPropertyName("objects")]
    public List<SchemaObject> Objects { get; init; } = new();
}

public sealed class AppConfig
{
    public required ConnectorSettings Connector { get; init; }
    public required SeismicSettings Seismic { get; init; }
    public required GraphSettings Graph { get; init; }
    public required IngestSettings Ingest { get; init; }
    public required SchemaConfig Schema { get; init; }
    public required ExclusionRules Exclusions { get; init; }

    /// <summary>Classifier rules (config/classification.json, or the built-in default set).</summary>
    public required ClassificationRules Classification { get; init; }

    /// <summary>ContentGate (CS-1) settings. Disabled unless CONTENT_GATE=true.</summary>
    public ContentGateSettings ContentGate { get; init; } = new();

    /// <summary>
    /// Prompt-injection rules for the ContentGate text channel
    /// (config/content-gate.json). EMPTY — and the file is never read — when the
    /// gate is off, so an unset CONTENT_GATE costs nothing, not even the file IO.
    /// </summary>
    public ClassificationRules ContentGateRules { get; init; } = new();

    /// <summary>Directory holding config/schema.json etc. (repo root by default).</summary>
    public required string ConfigDir { get; init; }

    public static string DefaultConfigDir => Path.Combine(Directory.GetCurrentDirectory(), "config");

    /// <summary>
    /// Build the full configuration from the environment + config files.
    /// Throws <see cref="ConfigException"/> naming the first missing setting.
    /// </summary>
    public static AppConfig Load(string? configDir = null)
    {
        Settings.EnsureEnvLoaded();
        configDir ??= DefaultConfigDir;

        var connectorId = Settings.RequireEnv("CONNECTOR_ID");
        Settings.ValidateConnectorId(connectorId);

        var tenant = Settings.RequireEnv("SEISMIC_TENANT");
        var graphCertPath = Environment.GetEnvironmentVariable(
            SeismicConnector.Graph.CertificateCredential.CertPathEnvVar);
        var graphCertThumbprint = Environment.GetEnvironmentVariable(
            SeismicConnector.Graph.CertificateCredential.CertThumbprintEnvVar);
        var graphCertConfigured = !string.IsNullOrWhiteSpace(graphCertPath)
            || !string.IsNullOrWhiteSpace(graphCertThumbprint);
        var contentGate = ContentGateSettings.FromEnvironment();
        var config = new AppConfig
        {
            Connector = new ConnectorSettings
            {
                Id = connectorId,
                Name = Settings.RequireEnv("CONNECTOR_NAME"),
                Description = Settings.RequireEnv("CONNECTOR_DESCRIPTION"),
            },
            Seismic = new SeismicSettings
            {
                Tenant = tenant,
                ApiBaseUrl = Settings.EnvOr("SEISMIC_API_BASE_URL", "https://api.seismic.com").TrimEnd('/'),
                TokenUrl = Settings.EnvOr(
                    "SEISMIC_AUTH_URL",
                    $"https://auth.seismic.com/tenants/{tenant}/connect/token"),
                ClientId = Settings.RequireEnv("SEISMIC_CLIENT_ID"),
                ClientSecret = Settings.RequireSecret("SECRET_SEISMIC_CLIENT_SECRET"),
                PageSize = Math.Clamp(Settings.IntEnv("SEISMIC_PAGE_SIZE", 100), 1, 500),
                QueryLimit = Math.Max(0, Settings.IntEnv("SEISMIC_QUERY_LIMIT", 0)),
                MaxRetries = Math.Max(0, Settings.IntEnv("SEISMIC_MAX_RETRIES", 4)),
                MaxExtractBytes = Math.Max(0, Settings.IntEnv("SEISMIC_MAX_EXTRACT_MB", 10)) * 1024L * 1024L,
                WebhookPort = Settings.IntEnv("SEISMIC_WEBHOOK_PORT", 0),
                EnrichUsage = Settings.BoolEnv("SEISMIC_ENRICH_USAGE"),
                LiveDocFieldIndexing = Settings.BoolEnv("LIVEDOC_FIELD_INDEXING"),
                PermissionReacl = Settings.BoolEnv("PERMISSION_REACL"),
                Classification = Settings.BoolEnv("CLASSIFICATION"),
                ClassificationManifest = Settings.BoolEnv("CLASSIFICATION")
                    && Settings.BoolEnv("CLASSIFICATION_MANIFEST"),
                FallbackAcl = Settings.EnvOr("SEISMIC_FALLBACK_ACL", "skip").ToLowerInvariant(),
                GraphItemTtlDays = Math.Max(0, Settings.IntEnv("GRAPH_ITEM_TTL_DAYS", 0)),
                ClassificationEnforceAcl = Settings.BoolEnv("CLASSIFICATION_ENFORCE_ACL"),
                ClassificationEnforceGroup =
                    Environment.GetEnvironmentVariable("CLASSIFICATION_ENFORCE_GROUP")?.Trim(),
            },
            Graph = new GraphSettings
            {
                TenantId = Settings.RequireEnv("AAD_APP_TENANT_ID"),
                ClientId = Settings.RequireEnv("AAD_APP_CLIENT_ID"),
                // Certificate credential (GRAPH_CLIENT_CERT_PATH / _THUMBPRINT)
                // wins over the client secret; with a certificate configured the
                // secret becomes optional so cert-only deployments carry none.
                ClientSecret = graphCertConfigured
                    ? (SecretProvider.GetSecret("SECRET_AAD_APP_CLIENT_SECRET") ?? "")
                    : Settings.RequireSecret("SECRET_AAD_APP_CLIENT_SECRET"),
                ClientCertPath = graphCertPath,
                ClientCertPassword = SecretProvider.GetSecret(
                    SeismicConnector.Graph.CertificateCredential.CertPasswordEnvVar),
                ClientCertThumbprint = graphCertThumbprint,
                BaseUrl = Settings.EnvOr("GRAPH_BASE_URL", "https://graph.microsoft.com").TrimEnd('/'),
                ApiVersion = Settings.EnvOr("GRAPH_API_VERSION", "v1.0"),
                MaxRetries = Math.Max(0, Settings.IntEnv("GRAPH_MAX_RETRIES", 4)),
                RetryBackoffBase = Math.Max(1, Settings.IntEnv("GRAPH_RETRY_BACKOFF_BASE", 2)),
                ConnectionTimeoutSeconds = Settings.IntEnv("CONNECTION_TIMEOUT_SECONDS", 600),
                ConnectionRetryIntervalSeconds = Settings.IntEnv("CONNECTION_RETRY_INTERVAL_SECONDS", 15),
                SchemaRetryIntervalSeconds = Settings.IntEnv("SCHEMA_RETRY_INTERVAL_SECONDS", 15),
            },
            Ingest = new IngestSettings
            {
                ChunkSize = Math.Max(1, Settings.IntEnv("INGEST_CHUNK_SIZE", 200)),
                // INGEST_GRAPH_BATCH_SIZE is the historical knob; GRAPH_BATCH_SIZE is
                // accepted as an alias (explicit INGEST_GRAPH_BATCH_SIZE wins). The
                // Graph API hard-caps $batch at 20 requests.
                GraphBatchSize = Math.Clamp(
                    Settings.IntEnv("INGEST_GRAPH_BATCH_SIZE", Settings.IntEnv("GRAPH_BATCH_SIZE", 20)),
                    1, 20),
                // GRAPH_BATCH_WORKERS is the documented knob; GRAPH_CONCURRENT_BATCHES
                // is the alias the reference codebase has always read and WINS when
                // both are set. Adaptive concurrency dials the live worker count
                // 1..max on 429 feedback.
                BatchWorkers = Math.Max(1, Settings.IntEnv(
                    "GRAPH_CONCURRENT_BATCHES", Settings.IntEnv("GRAPH_BATCH_WORKERS", 4))),
            },
            Schema = LoadSchema(Path.Combine(configDir, "schema.json")),
            Exclusions = ExclusionRules.LoadFile(Path.Combine(configDir, "exclusions.json")),
            Classification = ClassificationRules.LoadFile(Path.Combine(configDir, "classification.json")),
            ContentGate = contentGate,
            // Only read the injection ruleset when the gate is actually on, so
            // CONTENT_GATE unset means zero new file IO at startup.
            ContentGateRules = contentGate.Enabled
                ? InjectionRules.LoadFile(Path.Combine(configDir, InjectionRules.FileName))
                : new ClassificationRules(),
            ConfigDir = configDir,
        };

        // Mistyped gate knobs fail fast — but only when the gate is on, so an
        // unset CONTENT_GATE can never introduce a NEW startup failure.
        config.ContentGate.Validate();

        if (config.Seismic.FallbackAcl is not ("skip" or "tenant"))
            throw new ConfigException(
                $"Invalid SEISMIC_FALLBACK_ACL '{config.Seismic.FallbackAcl}': expected 'skip' or 'tenant'.");

        // Dead-letter payload mode: reject an unrecognized value at load rather
        // than silently falling back — the live reader fails safe (redacts), but
        // a typo like DEADLETTER_PAYLOAD_MODE=redact must be surfaced, not
        // quietly honoured as some default.
        var payloadMode = Environment.GetEnvironmentVariable(SyncState.PayloadModeEnvVar)?.Trim();
        if (!string.IsNullOrWhiteSpace(payloadMode)
            && !payloadMode.Equals("full", StringComparison.OrdinalIgnoreCase)
            && !payloadMode.Equals("redacted", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigException(
                $"Invalid {SyncState.PayloadModeEnvVar} '{payloadMode}': expected 'full' or 'redacted'.");
        }

        // Classification-enforced ACL requires a target Entra group to lock
        // Restricted items to — a naked CLASSIFICATION_ENFORCE_ACL=true with no
        // group would have nothing to restrict TO, so fail fast.
        if (config.Seismic.ClassificationEnforceAcl
            && string.IsNullOrWhiteSpace(config.Seismic.ClassificationEnforceGroup))
        {
            throw new ConfigException(
                "Invalid configuration: CLASSIFICATION_ENFORCE_ACL=true requires CLASSIFICATION_ENFORCE_GROUP "
                + "(the Entra group object id that Restricted items are locked to).");
        }

        // HA requires the shared SQL backend — fail fast, matching the reference contract.
        if (Settings.BoolEnv("HA_MODE") && !Settings.BoolEnv("USE_SQL_SERVER"))
            throw new ConfigException("Invalid configuration: HA_MODE=true requires USE_SQL_SERVER=true.");
        if (Settings.BoolEnv("USE_SQL_SERVER")
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")))
        {
            throw new ConfigException("Invalid configuration: Missing SQL_CONNECTION_STRING");
        }

        return config;
    }

    internal static SchemaConfig LoadSchema(string path)
    {
        if (!File.Exists(path))
            throw new ConfigException($"Invalid configuration: schema file not found at {path}");
        var schema = JsonSerializer.Deserialize<SchemaConfig>(File.ReadAllText(path))
            ?? throw new ConfigException($"Invalid configuration: {path} is empty");
        if (schema.Objects.Count == 0)
            throw new ConfigException($"Invalid configuration: {path} defines no objects");
        return schema;
    }

    /// <summary>Enabled object type names, in schema order.</summary>
    public IReadOnlyList<string> EnabledObjects =>
        Schema.Objects.Where(o => o.Enabled).Select(o => o.Name).ToList();

    /// <summary>
    /// Clone this configuration bound to a different Graph connection id
    /// (connection sharding — docs/SHARDING.md). Everything else is carried
    /// over by reference; the nested settings objects are read-only.
    /// </summary>
    public AppConfig ForConnection(string connectionId) => new()
    {
        Connector = new ConnectorSettings
        {
            Id = connectionId,
            Name = Connector.Name,
            Description = Connector.Description,
        },
        Seismic = Seismic,
        Graph = Graph,
        Ingest = Ingest,
        Schema = Schema,
        Exclusions = Exclusions,
        Classification = Classification,
        ContentGate = ContentGate,
        ContentGateRules = ContentGateRules,
        ConfigDir = ConfigDir,
    };
}
