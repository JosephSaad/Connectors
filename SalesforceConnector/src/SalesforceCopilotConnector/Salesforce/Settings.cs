// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNetEnv;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Salesforce;

public sealed class SalesforceSettings
{
    public string InstanceUrl { get; init; } = "";
    public string ApiVersion { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
}

public sealed class ConnectorSettings
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public JsonArray Schema { get; init; } = new();
    public JsonObject Template { get; init; } = new();
    public SalesforceSettings Salesforce { get; init; } = new();
}

public sealed class TuningSettings
{
    public string GraphApiVersion { get; init; } = "";
    public int GraphMaxRetries { get; init; }
    public int GraphRetryBackoffBase { get; init; }
    public int ConnectionTimeoutSeconds { get; init; }
    public int ConnectionRetryIntervalSeconds { get; init; }
    public int SchemaRetryIntervalSeconds { get; init; }
    public int SalesforceQueryLimit { get; init; }
    public int SalesforceBatchSize { get; init; }
    public int AclMaxParentDepth { get; init; }
    // Batching / parallelism controls
    public int IngestChunkSize { get; init; }          // records per processing chunk (default 500)
    public int IngestGraphBatchSize { get; init; }     // requests per Graph $batch POST (max 20, per MS docs)
    public int GraphConcurrentBatches { get; init; }   // parallel $batch calls (default 4, auto-dials on 429)
    public int ParallelObjectWorkers { get; init; }    // object types ingested in parallel (default 3)
}

public sealed class AppConfig
{
    public string ClientId { get; init; } = "";
    public string TenantId { get; init; } = "";
    public ConnectorSettings Connector { get; init; } = new();
    public string RepoRoot { get; init; } = "";
    public TuningSettings Tuning { get; init; } = new();
    public JsonObject SchemaConfig { get; init; } = new();
    public Dictionary<string, string> OwdFieldMap { get; init; } = new();
    public Dictionary<string, (string ParentFieldName, string ParentObjectName)> ParentMap { get; init; } = new();
    public Dictionary<string, string> OwdOverrides { get; init; } = new();
    public List<string> ObjectNames { get; init; } = new();
    public bool UseNewAclEngine { get; init; }
    public bool UseGroupAcl { get; init; }
    public bool UseEntityDefinitionOwd { get; init; }
    public string? DebugObjectType { get; init; }
    public string? DebugItemId { get; init; }

    /// <summary>
    /// Connection-sharding restriction (<c>GRAPH_CONNECTION_SHARDS</c>). When non-null,
    /// ingestion is limited to these object types (a shard runs as its own Graph connection
    /// covering its slice of the schema). <c>null</c> ⇒ single-connection default, all objects.
    /// </summary>
    public IReadOnlyList<string>? ShardObjectTypes { get; init; }

    /// <summary>
    /// Intra-object hash-sharding restriction (<c>GRAPH_CONNECTION_SHARDS</c> entries of the
    /// form <c>"Object#bucket/of"</c>). When non-null, records of a listed object type are
    /// fetched/ingested by this connection only when their id hashes into one of the shard's
    /// buckets (<see cref="ShardBucketSpec.Owns"/>) — the record-level counterpart of
    /// <see cref="ShardObjectTypes"/>. <c>null</c> ⇒ no record-level filtering (default).
    /// </summary>
    public IReadOnlyDictionary<string, ShardBucketSpec>? ShardObjectBuckets { get; init; }
}

public static class Settings
{
    public static readonly string RepoRoot = Path.GetFullPath(Directory.GetCurrentDirectory());

    private static readonly string ConfigDir = Path.Combine(RepoRoot, "config");

    public static readonly string[] LocalEnvFiles =
    {
        Path.Combine(RepoRoot, "env", ".env.local"),
        Path.Combine(RepoRoot, "env", ".env.local.user"),
        Path.Combine(RepoRoot, ".env.local"),
        Path.Combine(RepoRoot, ".env.local.user"),
    };

    public static readonly string[] DisallowedConnectorPrefixes =
    {
        "Microsoft",
        "None",
        "Directory",
        "Exchange",
        "ExchangeArchive",
        "LinkedIn",
        "Mailbox",
        "OneDriveBusiness",
        "SharePoint",
        "Teams",
        "Yammer",
        "Connectors",
        "TaskFabric",
        "PowerBI",
        "Assistant",
        "TopicEngine",
        "MSFT_All_Connectors",
    };

    // ── JSON config loaders (cached for the process lifetime) ─────────────────

    private static JsonObject? _schemaConfigCache;
    private static JsonArray? _graphSchemaCache;
    private static JsonObject? _templateCache;

    /// <summary>Load and parse a JSON file from the ``config/`` directory.</summary>
    private static JsonNode LoadConfigJson(string filename)
    {
        return JsonNode.Parse(File.ReadAllText(Path.Combine(ConfigDir, filename)))!;
    }

    /// <summary>Load and return the parsed ``config/schema.json``.</summary>
    public static JsonObject LoadSchemaConfig() =>
        _schemaConfigCache ??= (JsonObject)LoadConfigJson("schema.json");

    /// <summary>Load and return the parsed ``config/graph-schema.json``.</summary>
    public static JsonArray LoadGraphSchema() =>
        _graphSchemaCache ??= (JsonArray)LoadConfigJson("graph-schema.json");

    /// <summary>Load and return the parsed ``config/template.json``.</summary>
    public static JsonObject LoadTemplate() =>
        _templateCache ??= (JsonObject)LoadConfigJson("template.json");

    /// <summary>``{objectName: owdField}`` for every object that declares one.</summary>
    public static Dictionary<string, string> BuildOwdFieldMap(JsonObject? schema = null)
    {
        var data = schema ?? LoadSchemaConfig();
        var result = new Dictionary<string, string>();
        foreach (var node in data["objectList"] as JsonArray ?? new JsonArray())
        {
            if (node is JsonObject obj && obj.ContainsKey("owdField"))
                result[obj["objectName"]!.GetValue<string>()] = obj["owdField"]!.GetValue<string>();
        }
        return result;
    }

    /// <summary>Return a list of *all* object names declared in schema.json.</summary>
    public static List<string> BuildObjectNameList(JsonObject? schema = null)
    {
        var data = schema ?? LoadSchemaConfig();
        var result = new List<string>();
        foreach (var node in data["objectList"] as JsonArray ?? new JsonArray())
        {
            if (node is JsonObject obj && obj.ContainsKey("objectName"))
                result.Add(obj["objectName"]!.GetValue<string>());
        }
        return result;
    }

    /// <summary>``{objectName: (parentFieldName, parentObjectName)}``.</summary>
    public static Dictionary<string, (string ParentFieldName, string ParentObjectName)> BuildParentMap(JsonObject? schema = null)
    {
        var data = schema ?? LoadSchemaConfig();
        var result = new Dictionary<string, (string ParentFieldName, string ParentObjectName)>();
        foreach (var node in data["objectList"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject obj)
                continue;
            var objName = obj["objectName"]?.GetValue<string>() ?? "";
            var parentObj = obj["parentObjectName"]?.GetValue<string>() ?? "";
            if (objName.Length > 0 && parentObj.Length > 0)
            {
                var parentField = obj["parentFieldName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(parentField))
                    parentField = $"{parentObj}Id";
                result[objName] = (parentField, parentObj);
            }
        }
        return result;
    }

    /// <summary>Copy env var *source* into *target* if *target* is unset and *source* exists.</summary>
    private static void AliasEnv(string target, string source)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(target)) &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(source)))
        {
            Environment.SetEnvironmentVariable(target, Environment.GetEnvironmentVariable(source));
        }
    }

    /// <summary>
    /// Copy a <c>SECRET_*</c> value into *target* if *target* is unset and the secret resolves.
    /// The *source* secret is read through <see cref="SecretProvider.GetSecret(string)"/>, so it
    /// comes from Key Vault when <c>USE_KEY_VAULT</c> is enabled and from the environment variable
    /// otherwise — behaviour with <c>USE_KEY_VAULT</c> unset is identical to <see cref="AliasEnv"/>.
    /// </summary>
    private static void AliasEnvFromSecret(string target, string source)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(target)))
            return;
        var value = SecretProvider.GetSecret(source);
        if (!string.IsNullOrEmpty(value))
            Environment.SetEnvironmentVariable(target, value);
    }

    /// <summary>Load ``.env.local`` files and set up environment variable aliases.</summary>
    public static void LoadLocalEnvironment()
    {
        foreach (var envFile in LocalEnvFiles)
        {
            if (File.Exists(envFile))
                Env.Load(envFile);
        }

        AliasEnv("AZURE_CLIENT_ID", "AAD_APP_CLIENT_ID");
        AliasEnvFromSecret("AZURE_CLIENT_SECRET", "SECRET_AAD_APP_CLIENT_SECRET");
        AliasEnv("AZURE_TENANT_ID", "AAD_APP_TENANT_ID");
        AliasEnvFromSecret("SALESFORCE_CLIENT_SECRET", "SECRET_SALESFORCE_CLIENT_SECRET");

        if ((Environment.GetEnvironmentVariable("TEAMSFX_ENV") ?? "").ToLowerInvariant() == "local" &&
            Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") is null)
        {
            Environment.SetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT", "Development");
        }
    }

    /// <summary>Return the value of env var *name*, raising <see cref="ArgumentException"/> if missing.</summary>
    private static string RequireEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
            return value;
        throw new ArgumentException($"Invalid configuration: Missing {name}");
    }

    /// <summary>Validate that *connectorId* meets length, character, and prefix requirements.</summary>
    public static void ValidateConnectorId(string connectorId)
    {
        if (string.IsNullOrEmpty(connectorId))
            throw new ArgumentException("Connector ID is required.");
        if (connectorId.Length < 3 || connectorId.Length > 32)
            throw new ArgumentException("Connector ID must be between 3 and 32 characters long.");
        if (!connectorId.All(char.IsLetterOrDigit))
            throw new ArgumentException("Connector ID must contain only alphanumeric characters.");

        if (DisallowedConnectorPrefixes.Any(prefix => connectorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var disallowed = string.Join(", ", DisallowedConnectorPrefixes);
            throw new ArgumentException($"Connector ID cannot start with: {disallowed}.");
        }
    }

    /// <summary>Return env var *name* as an int, raising <see cref="ArgumentException"/> if missing or non-integer.</summary>
    private static int RequireIntEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"Invalid configuration: Missing {name}");
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"Invalid configuration: {name} must be an integer, got '{value}'");
        return parsed;
    }

    private static int IntEnv(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
            value = defaultValue;
        return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static bool BoolEnv(string name)
    {
        var value = (Environment.GetEnvironmentVariable(name) ?? "false").ToLowerInvariant();
        return value is "true" or "1" or "yes";
    }

    /// <summary>Render a JSON value the way Python's ``str()`` would.</summary>
    private static string PyStr(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue v when v.TryGetValue(out string? s) => s!,
        JsonValue v when v.TryGetValue(out bool b) => b ? "True" : "False",
        _ => node.ToJsonString(),
    };

    /// <summary>Load environment variables and JSON configs, returning a fully populated <see cref="AppConfig"/>.</summary>
    public static AppConfig LoadConfig()
    {
        LoadLocalEnvironment();

        var connectorId = RequireEnv("CONNECTOR_ID");
        ValidateConnectorId(connectorId);

        var cfg = LoadSchemaConfig();

        var owdOverrides = new Dictionary<string, string>();
        var rawOwdOverrides = (Environment.GetEnvironmentVariable("OWD_OVERRIDES") ?? "").Trim();
        if (rawOwdOverrides.Length > 0)
        {
            try
            {
                var parsed = JsonNode.Parse(rawOwdOverrides);
                if (parsed is JsonObject parsedObj)
                {
                    foreach (var (key, value) in parsedObj)
                        owdOverrides[key] = PyStr(value);
                }
            }
            catch (JsonException)
            {
                // pass (Python parity): an unparsable owd_overrides JSON env value
                // is ignored and defaults apply; validate-config reports it properly.
            }
        }

        // Field evaluation order mirrors the Python AppConfig(...) keyword order so
        // that the first missing environment variable reported is identical.
        var clientId = RequireEnv("AZURE_CLIENT_ID");
        var tenantId = RequireEnv("AZURE_TENANT_ID");
        var owdFieldMap = BuildOwdFieldMap(cfg);
        var parentMap = BuildParentMap(cfg);
        var objectNames = BuildObjectNameList(cfg);
        var useNewAclEngine = BoolEnv("USE_NEW_ACL_ENGINE");
        var useGroupAcl = BoolEnv("USE_GROUP_ACL");
        var useEntityDefinitionOwd = BoolEnv("USE_ENTITY_DEFINITION_OWD");
        var debugObjectType = Environment.GetEnvironmentVariable("DEBUG_OBJECT_TYPE");
        var debugItemId = Environment.GetEnvironmentVariable("DEBUG_ITEM_ID");

        var tuning = new TuningSettings
        {
            GraphApiVersion = Environment.GetEnvironmentVariable("GRAPH_API_VERSION") ?? "v1.0",
            GraphMaxRetries = RequireIntEnv("GRAPH_MAX_RETRIES"),
            GraphRetryBackoffBase = RequireIntEnv("GRAPH_RETRY_BACKOFF_BASE"),
            ConnectionTimeoutSeconds = RequireIntEnv("CONNECTION_TIMEOUT_SECONDS"),
            ConnectionRetryIntervalSeconds = RequireIntEnv("CONNECTION_RETRY_INTERVAL_SECONDS"),
            SchemaRetryIntervalSeconds = RequireIntEnv("SCHEMA_RETRY_INTERVAL_SECONDS"),
            SalesforceQueryLimit = IntEnv("SALESFORCE_QUERY_LIMIT", "0"),
            SalesforceBatchSize = RequireIntEnv("SALESFORCE_BATCH_SIZE"),
            AclMaxParentDepth = RequireIntEnv("ACL_MAX_PARENT_DEPTH"),
            IngestChunkSize = IntEnv("INGEST_CHUNK_SIZE", "2000"),  // 2000 matches SF page size = 1 page per ingest cycle
            IngestGraphBatchSize = Math.Min(IntEnv("INGEST_GRAPH_BATCH_SIZE", "20"), 20),
            // GRAPH_BATCH_WORKERS is the knob documented in env/README.md, docs/HA.md and
            // docs/CAPACITY.md; GRAPH_CONCURRENT_BATCHES is the name the (Python) code has
            // always read. Accept both: explicit GRAPH_CONCURRENT_BATCHES wins, then
            // GRAPH_BATCH_WORKERS, then the default (8) — unchanged when neither is set.
            GraphConcurrentBatches = Math.Max(1, IntEnv(
                "GRAPH_CONCURRENT_BATCHES",
                Environment.GetEnvironmentVariable("GRAPH_BATCH_WORKERS") ?? "8")),
            ParallelObjectWorkers = Math.Max(1, IntEnv("PARALLEL_OBJECT_WORKERS", "3")),
        };

        var connector = new ConnectorSettings
        {
            Id = connectorId,
            Name = RequireEnv("CONNECTOR_NAME"),
            Description = RequireEnv("CONNECTOR_DESCRIPTION"),
            Schema = LoadGraphSchema(),
            Template = LoadTemplate(),
            Salesforce = new SalesforceSettings
            {
                InstanceUrl = RequireEnv("SALESFORCE_INSTANCE_URL"),
                ApiVersion = RequireEnv("SALESFORCE_API_VERSION"),
                ClientId = RequireEnv("SALESFORCE_CLIENT_ID"),
                ClientSecret = RequireEnv("SALESFORCE_CLIENT_SECRET"),
            },
        };

        return new AppConfig
        {
            ClientId = clientId,
            TenantId = tenantId,
            RepoRoot = RepoRoot,
            SchemaConfig = cfg,
            OwdFieldMap = owdFieldMap,
            ParentMap = parentMap,
            OwdOverrides = owdOverrides,
            ObjectNames = objectNames,
            UseNewAclEngine = useNewAclEngine,
            UseGroupAcl = useGroupAcl,
            UseEntityDefinitionOwd = useEntityDefinitionOwd,
            DebugObjectType = string.IsNullOrEmpty(debugObjectType) ? null : debugObjectType,
            DebugItemId = string.IsNullOrEmpty(debugItemId) ? null : debugItemId,
            Tuning = tuning,
            Connector = connector,
        };
    }
}
