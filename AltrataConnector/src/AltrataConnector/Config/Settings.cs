// Config/Settings.cs
// ------------------
// Environment-driven configuration for the Altrata connector. Loads the
// layered env files (see EnvLoader), resolves SECRET_* values through
// SecretProvider (env → optional Key Vault), validates required values, and
// exposes a strongly typed AppConfig.

using System.Globalization;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.Config;

public sealed class ConfigurationError : Exception
{
    public ConfigurationError(string message) : base(message) { }
}

public sealed record AppConfig
{
    // ---- Core identity -----------------------------------------------------
    public required string ConnectorId { get; init; }
    public required string ConnectorName { get; init; }
    public required string ConnectorDescription { get; init; }

    // ---- Microsoft Graph / Entra --------------------------------------------
    public required string AadClientId { get; init; }
    public required string AadTenantId { get; init; }
    public required string AadClientSecret { get; init; }
    public string GraphApiVersion { get; init; } = "v1.0";
    public int GraphMaxRetries { get; init; } = 4;
    public double GraphRetryBackoffBase { get; init; } = 2;
    public int GraphBatchSize { get; init; } = 20;

    /// <summary>Concurrent Graph $batch workers. GRAPH_CONCURRENT_BATCHES is an
    /// alias that WINS over GRAPH_BATCH_WORKERS when both are set (same
    /// precedence as the reference connector); default 8.</summary>
    public int GraphBatchWorkers { get; init; } = 8;

    // ---- Feeds ---------------------------------------------------------------
    public string? FeedPath { get; init; }
    public int RetentionDays { get; init; }
    public string RetentionMode { get; init; } = "archive";  // archive | delete
    public string? CrmContactsPath { get; init; }

    // ---- Altrata REST API (enrichment) ---------------------------------------
    public string? AltrataApiBaseUrl { get; init; }
    public string? AltrataTokenUrl { get; init; }
    public string? AltrataClientId { get; init; }
    public string? AltrataClientSecret { get; init; }
    public int AltrataApiCallsPerMinute { get; init; } = 60;

    /// <summary>ALTRATA_MAX_LOOKUPS_PER_DAY (default unset = 0 = NO CEILING,
    /// behaviour byte-identical to before WP-AL-4): maximum billable enrichment
    /// lookups per calendar UTC day. Unlike ALTRATA_API_CALLS_PER_MINUTE — which
    /// only makes a caller WAIT — this REFUSES the lookup fail-closed once
    /// reached. See Altrata/UsageBudget.cs, including the scope note: on the file
    /// backend the ceiling is per host, and only USE_SQL_SERVER=true makes it
    /// fleet-wide.</summary>
    public int AltrataMaxLookupsPerDay { get; init; }

    /// <summary>ALTRATA_MAX_LOOKUPS_PER_WINDOW (default unset = 0 = no ceiling):
    /// maximum billable lookups in the trailing
    /// <see cref="AltrataUsageWindowHours"/>-hour ROLLING window. Complements the
    /// calendar-day ceiling by capping bursts that would otherwise all land just
    /// after a midnight reset. Both may be set; a lookup must clear both.</summary>
    public int AltrataMaxLookupsPerWindow { get; init; }

    /// <summary>ALTRATA_USAGE_WINDOW_HOURS (default 24, range 1-168): length of
    /// the rolling window. Inert unless ALTRATA_MAX_LOOKUPS_PER_WINDOW is set.</summary>
    public int AltrataUsageWindowHours { get; init; } = 24;

    // ---- Entity resolution ------------------------------------------------------
    /// <summary>ENTITY_FUZZY_MATCHING (default false): enable the scored fuzzy
    /// tier beneath the deterministic email / name+employer rules.</summary>
    public bool EntityFuzzyMatching { get; init; }

    /// <summary>ENTITY_MATCH_THRESHOLD (default 0.85): auto-link at/above.</summary>
    public double EntityMatchThreshold { get; init; } = 0.85;

    /// <summary>ENTITY_REVIEW_FLOOR (default 0.6): candidates in
    /// [floor, threshold) land in the review queue instead of auto-linking.</summary>
    public double EntityReviewFloor { get; init; } = 0.6;

    // ---- Relationship-path materialization --------------------------------------
    /// <summary>RELATIONSHIP_PATHS (default false): precompute per-person path
    /// summaries from RelationshipPath/BoardMembership/Organization and
    /// materialize them onto PersonProfile items.</summary>
    public bool RelationshipPaths { get; init; }

    /// <summary>RELATIONSHIP_TOP_ORGS (default 3, range 1-10): bound on the
    /// materialized topConnectedOrgs list.</summary>
    public int RelationshipTopOrgs { get; init; } = 3;

    // ---- Data classification & sensitivity labeling -----------------------------
    /// <summary>CLASSIFICATION (default false): stamp the unified
    /// sensitivityLabel / detectedCategories / dataResidency properties.</summary>
    public bool Classification { get; init; }

    /// <summary>DATA_RESIDENCY (optional): governance residency tag, e.g. US/EU.</summary>
    public string? DataResidency { get; init; }

    /// <summary>CLASSIFICATION_MANIFEST (default false): write a per-delivery
    /// classification summary JSONL (counts + item→label; no personal values).</summary>
    public bool ClassificationManifest { get; init; }

    /// <summary>CLASSIFICATION_ENFORCE_ACL (#6b, default false): when true AND
    /// <see cref="ClassificationEnforceGroupId"/> is set, top-tier (Restricted)
    /// items have their ACL restricted to that Entra group only. Requires
    /// CLASSIFICATION=true (the tag drives enforcement). Non-breaking (off).</summary>
    public bool ClassificationEnforceAcl { get; init; }

    /// <summary>CLASSIFICATION_ENFORCE_GROUP_ID (#6b): the Entra group id that
    /// Restricted items are locked to when enforcement is on.</summary>
    public string? ClassificationEnforceGroupId { get; init; }

    /// <summary>GRAPH_ITEM_TTL_DAYS (#8, default unset = 0 = off): when > 0,
    /// ingested items are stamped with expirationDateTime = now + this many days
    /// so the index self-expires if crawling stops (defense after an outage).</summary>
    public int GraphItemTtlDays { get; init; }

    // ---- Entitlement ----------------------------------------------------------
    public string SeatListPath { get; init; } = Path.Combine("config", "seats.json");
    public string? SeatGroupId { get; init; }

    /// <summary>IDENTITY_SYNC_ON_INCREMENTAL (#5, default TRUE): re-sync seat
    /// entitlement on INCREMENTAL crawls too, so a seat change triggers the
    /// re-ACL sweep at the incremental cadence (not just on full crawls). The
    /// seat list itself is always loaded every crawl (new items always get the
    /// current ACL); this flag governs only the sweep over EXISTING items. Set
    /// false to defer that (potentially large) sweep to full crawls. Residual
    /// lag is non-real-time: a mid-cycle change is enforced at the next crawl.</summary>
    public bool IdentitySyncOnIncremental { get; init; } = true;

    // ---- Circuit breakers (DR / degraded mode) ----------------------------------
    /// <summary>CIRCUIT_BREAKER (default true): master switch; false = passthrough.</summary>
    public bool CircuitBreakerEnabled { get; init; } = true;
    public int CircuitBreakerFailureThreshold { get; init; } = 5;
    public int CircuitBreakerWindowSeconds { get; init; } = 60;
    public int CircuitBreakerOpenSeconds { get; init; } = 30;
    public int CircuitBreakerHalfOpenTrials { get; init; } = 2;

    // ---- State backends --------------------------------------------------------
    public bool UseSqlServer { get; init; }
    public string? SqlConnectionString { get; init; }
    public bool SqlUseManagedIdentity { get; init; }
    public int SqlMaxRetries { get; init; } = 3;
    public bool HaMode { get; init; }
    public string DataDirectory { get; init; } = "data";

    /// <summary>Reserved Microsoft connection-id prefixes (Graph rejects them).</summary>
    internal static readonly string[] ReservedPrefixes =
    {
        "Microsoft", "SharePoint", "Teams", "Exchange", "OneDriveBusiness",
        "LinkedIn", "Yammer", "Connectors", "PowerBI", "None", "Directory", "Windows",
    };

    /// <summary>Load configuration from the environment (env files already layered in).</summary>
    public static AppConfig Load(ISecretProvider? secrets = null)
    {
        EnvLoader.LoadOnce();
        secrets ??= new SecretProvider();

        var errors = new List<string>();

        string Require(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Missing {name}");
                return string.Empty;
            }
            return value.Trim();
        }

        string RequireSecret(string name)
        {
            var value = secrets.Get(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Missing {name}");
                return string.Empty;
            }
            return value.Trim();
        }

        static string? Optional(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        static int OptionalInt(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;
        }

        static double OptionalDouble(string name, double fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d
                : fallback;
        }

        var connectorId = Require("CONNECTOR_ID");
        ValidateConnectorId(connectorId, errors);

        // Certificate credential (GRAPH_CLIENT_CERT_PATH / _THUMBPRINT) WINS
        // over the client secret; when configured, the secret becomes optional.
        var certCredential = Graph.CertificateCredential.Configured;
        var certPath = Optional(Graph.CertificateCredential.CertPathEnvVar);
        if (certPath != null && !File.Exists(certPath))
            errors.Add($"{Graph.CertificateCredential.CertPathEnvVar} '{certPath}' does not exist");

        var config = new AppConfig
        {
            ConnectorId = connectorId,
            ConnectorName = Require("CONNECTOR_NAME"),
            ConnectorDescription = Require("CONNECTOR_DESCRIPTION"),

            AadClientId = Require("AAD_APP_CLIENT_ID"),
            AadTenantId = Require("AAD_APP_TENANT_ID"),
            AadClientSecret = certCredential
                ? secrets.Get("SECRET_AAD_APP_CLIENT_SECRET")?.Trim() ?? string.Empty
                : RequireSecret("SECRET_AAD_APP_CLIENT_SECRET"),
            GraphApiVersion = Optional("GRAPH_API_VERSION") ?? "v1.0",
            GraphMaxRetries = OptionalInt("GRAPH_MAX_RETRIES", 4),
            GraphRetryBackoffBase = OptionalDouble("GRAPH_RETRY_BACKOFF_BASE", 2),
            GraphBatchSize = Math.Clamp(OptionalInt("GRAPH_BATCH_SIZE", 20), 1, 20),
            GraphBatchWorkers = Math.Clamp(
                OptionalInt("GRAPH_CONCURRENT_BATCHES", OptionalInt("GRAPH_BATCH_WORKERS", 8)), 1, 64),

            FeedPath = Optional("FEED_PATH"),
            RetentionDays = OptionalInt("RETENTION_DAYS", 0),
            RetentionMode = (Optional("RETENTION_MODE") ?? "archive").ToLowerInvariant(),
            CrmContactsPath = Optional("CRM_CONTACTS_PATH"),

            AltrataApiBaseUrl = Optional("ALTRATA_API_BASE_URL"),
            AltrataTokenUrl = Optional("ALTRATA_TOKEN_URL"),
            AltrataClientId = Optional("ALTRATA_CLIENT_ID"),
            AltrataClientSecret = secrets.Get("SECRET_ALTRATA_CLIENT_SECRET"),
            AltrataApiCallsPerMinute = Math.Max(1, OptionalInt("ALTRATA_API_CALLS_PER_MINUTE", 60)),
            // WP-AL-4 usage ceilings. NOT clamped with Math.Max: 0/unset must mean
            // "no ceiling" and a NEGATIVE value must be an error, not silently
            // rounded into one — an operator who typed -1 has not configured a
            // ceiling and must be told so rather than left unprotected.
            AltrataMaxLookupsPerDay = OptionalInt(Altrata.UsageBudgetOptions.MaxPerDayEnvVar, 0),
            AltrataMaxLookupsPerWindow = OptionalInt(Altrata.UsageBudgetOptions.MaxPerWindowEnvVar, 0),
            AltrataUsageWindowHours = OptionalInt(Altrata.UsageBudgetOptions.WindowHoursEnvVar, 24),

            EntityFuzzyMatching = EnvFlags.IsTrue("ENTITY_FUZZY_MATCHING"),
            EntityMatchThreshold = OptionalDouble("ENTITY_MATCH_THRESHOLD", 0.85),
            EntityReviewFloor = OptionalDouble("ENTITY_REVIEW_FLOOR", 0.6),

            RelationshipPaths = EnvFlags.IsTrue("RELATIONSHIP_PATHS"),
            RelationshipTopOrgs = OptionalInt("RELATIONSHIP_TOP_ORGS", 3),

            Classification = EnvFlags.IsTrue("CLASSIFICATION"),
            DataResidency = Optional("DATA_RESIDENCY"),
            ClassificationManifest = EnvFlags.IsTrue("CLASSIFICATION_MANIFEST"),
            ClassificationEnforceAcl = EnvFlags.IsTrue("CLASSIFICATION_ENFORCE_ACL"),
            ClassificationEnforceGroupId = Optional("CLASSIFICATION_ENFORCE_GROUP_ID"),

            GraphItemTtlDays = OptionalInt("GRAPH_ITEM_TTL_DAYS", 0),

            SeatListPath = Optional("SEAT_LIST_PATH") ?? Path.Combine("config", "seats.json"),
            SeatGroupId = Optional("SEAT_GROUP_ID"),
            // #5 default flip: entitlement re-syncs on incrementals unless
            // explicitly turned off (default-ON knob → IsFalse check).
            IdentitySyncOnIncremental = !EnvFlags.IsFalse("IDENTITY_SYNC_ON_INCREMENTAL"),

            CircuitBreakerEnabled = !EnvFlags.IsFalse("CIRCUIT_BREAKER"),
            CircuitBreakerFailureThreshold = OptionalInt("CIRCUIT_BREAKER_FAILURE_THRESHOLD", 5),
            CircuitBreakerWindowSeconds = OptionalInt("CIRCUIT_BREAKER_WINDOW_SECONDS", 60),
            CircuitBreakerOpenSeconds = OptionalInt("CIRCUIT_BREAKER_OPEN_SECONDS", 30),
            CircuitBreakerHalfOpenTrials = OptionalInt("CIRCUIT_BREAKER_HALFOPEN_TRIALS", 2),

            UseSqlServer = EnvFlags.IsTrue("USE_SQL_SERVER"),
            SqlConnectionString = Optional("SQL_CONNECTION_STRING"),
            SqlUseManagedIdentity = EnvFlags.IsTrue("SQL_USE_MANAGED_IDENTITY"),
            SqlMaxRetries = OptionalInt("SQL_MAX_RETRIES", 3),
            HaMode = EnvFlags.IsTrue("HA_MODE"),
            DataDirectory = Optional("DATA_DIR") ?? "data",
        };

        if (config.UseSqlServer && string.IsNullOrWhiteSpace(config.SqlConnectionString))
            errors.Add("USE_SQL_SERVER=true requires SQL_CONNECTION_STRING");
        if (config.HaMode && !config.UseSqlServer)
            errors.Add("HA_MODE=true requires USE_SQL_SERVER=true (shared SQL backend)");
        if (config.RetentionMode is not ("archive" or "delete"))
            errors.Add($"RETENTION_MODE must be 'archive' or 'delete', got '{config.RetentionMode}'");
        if (config.EntityMatchThreshold is <= 0 or > 1)
            errors.Add("ENTITY_MATCH_THRESHOLD must be in (0, 1]");
        if (config.EntityReviewFloor < 0 || config.EntityReviewFloor >= config.EntityMatchThreshold)
            errors.Add("ENTITY_REVIEW_FLOOR must be >= 0 and below ENTITY_MATCH_THRESHOLD");
        if (config.RelationshipTopOrgs is < 1 or > 10)
            errors.Add("RELATIONSHIP_TOP_ORGS must be between 1 and 10");
        if (config.CircuitBreakerFailureThreshold < 1)
            errors.Add("CIRCUIT_BREAKER_FAILURE_THRESHOLD must be >= 1");
        if (config.CircuitBreakerWindowSeconds < 1)
            errors.Add("CIRCUIT_BREAKER_WINDOW_SECONDS must be >= 1");
        if (config.CircuitBreakerOpenSeconds < 1)
            errors.Add("CIRCUIT_BREAKER_OPEN_SECONDS must be >= 1");
        if (config.CircuitBreakerHalfOpenTrials < 1)
            errors.Add("CIRCUIT_BREAKER_HALFOPEN_TRIALS must be >= 1");
        if (config.GraphItemTtlDays < 0)
            errors.Add("GRAPH_ITEM_TTL_DAYS must be >= 0 (0 = disabled)");
        if (config.AltrataMaxLookupsPerDay < 0)
            errors.Add($"{Altrata.UsageBudgetOptions.MaxPerDayEnvVar} must be >= 0 (0 = unset = no ceiling)");
        if (config.AltrataMaxLookupsPerWindow < 0)
            errors.Add($"{Altrata.UsageBudgetOptions.MaxPerWindowEnvVar} must be >= 0 (0 = unset = no ceiling)");
        if (config.AltrataUsageWindowHours is < 1 or > 168)
            errors.Add($"{Altrata.UsageBudgetOptions.WindowHoursEnvVar} must be between 1 and 168 (default 24)");
        if (config.ClassificationEnforceAcl && string.IsNullOrWhiteSpace(config.ClassificationEnforceGroupId))
            errors.Add("CLASSIFICATION_ENFORCE_ACL=true requires CLASSIFICATION_ENFORCE_GROUP_ID (the Entra group Restricted items are locked to)");
        if (config.ClassificationEnforceAcl && !config.Classification)
            errors.Add("CLASSIFICATION_ENFORCE_ACL=true requires CLASSIFICATION=true (the classification tag drives enforcement)");
        // Dead-letter payload mode: validate at load so a typo (e.g. "redacetd")
        // fails validate-config / startup, not mid-crawl. DeadLetterPolicy.Mode
        // throws a ConfigurationError naming the setting for any unknown value.
        try { _ = State.DeadLetterPolicy.Mode; }
        catch (ConfigurationError exc) { errors.Add(exc.Message); }

        // ContentGate (CS-1): same discipline. The gate itself is read live from
        // the environment (Altrata.ContentGateOptions.FromEnv), but a bad fail
        // mode / size / pattern file must fail validate-config, not mid-crawl.
        // Validated even when CONTENT_GATE is off so a typo cannot lie dormant.
        try
        {
            var gate = Altrata.ContentGateOptions.FromEnv();
            if (gate.Enabled && gate.PatternsPath != null)
                _ = Altrata.InjectionScanner.FromFile(gate.PatternsPath, gate.PatternTimeout);
            if (gate.Enabled && gate.IcapUrl != null)
            {
                // Honest about the scope reduction rather than silently ignoring it.
                Infrastructure.Logging.GetLogger("altrata_connector.config").Info(
                    $"{Altrata.ContentGateOptions.IcapUrlEnvVar} is set but INERT in this connector: " +
                    "Altrata ingests no binary content (FeedReader accepts .json/.jsonl/.csv only), " +
                    "so there is no malware scanner. File integrity is the SHA-256 manifest gate.");
            }
        }
        catch (ConfigurationError exc) { errors.Add(exc.Message); }

        if (errors.Count > 0)
            throw new ConfigurationError("Invalid configuration: " + string.Join("; ", errors));

        return config;
    }

    internal static void ValidateConnectorId(string id, List<string> errors)
    {
        if (string.IsNullOrEmpty(id))
            return;  // "Missing CONNECTOR_ID" already recorded
        if (id.Length is < 3 or > 32)
            errors.Add("CONNECTOR_ID must be 3-32 characters");
        if (!id.All(char.IsLetterOrDigit))
            errors.Add("CONNECTOR_ID must be alphanumeric (no spaces or punctuation)");
        foreach (var prefix in ReservedPrefixes)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"CONNECTOR_ID must not start with reserved prefix '{prefix}'");
                break;
            }
        }
    }

    /// <summary>True when the Altrata enrichment API is fully configured.</summary>
    public bool ApiConfigured =>
        !string.IsNullOrEmpty(AltrataApiBaseUrl)
        && !string.IsNullOrEmpty(AltrataTokenUrl)
        && !string.IsNullOrEmpty(AltrataClientId)
        && !string.IsNullOrEmpty(AltrataClientSecret);
}
