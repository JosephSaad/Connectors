// Commands/ValidateConfig.cs
// --------------------------
// `validate-config [--strict]` — preflight checks before a long crawl:
//
//   • required env vars present, CONNECTOR_ID shape valid
//   • config/schema.json and config/graph-schema.json parse and are sane
//   • cross-flag consistency (HA needs SQL, acl mode needs the finance group,
//     TDW path exists when set, ...)
//   • --strict additionally requires Clarizen login and a Graph token to
//     succeed (best-effort connectivity), and turns warnings into failures.

using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Commands;

public static class ValidateConfig
{
    internal static readonly string[] RequiredEnvVars =
    {
        "CONNECTOR_ID",
        "CLARIZEN_USERNAME",
        "SECRET_CLARIZEN_PASSWORD",
        "AAD_APP_TENANT_ID",
        "AAD_APP_CLIENT_ID",
        "SECRET_AAD_APP_CLIENT_SECRET",
    };

    /// <summary>Prefix of every finding the schema.json block below records for a
    /// file it could not load. AddSchemaDriftFindings matches on it to PROVE a
    /// load failure was already reported instead of assuming it was.</summary>
    internal const string SchemaJsonErrorPrefix = "schema.json invalid:";

    /// <summary>Prefix shared by every graph-schema.json finding recorded before
    /// the drift check runs. Same purpose as <see cref="SchemaJsonErrorPrefix"/>.
    /// </summary>
    internal const string GraphSchemaJsonErrorPrefix = "graph-schema.json";

    public sealed class Result
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        public bool Ok(bool strict) => Errors.Count == 0 && (!strict || Warnings.Count == 0);
    }

    /// <summary>Offline validation (no network). Testable.</summary>
    internal static Result ValidateCore(string? schemaPath = null, string? graphSchemaPath = null)
    {
        var result = new Result();

        foreach (var name in RequiredEnvVars)
        {
            var value = name.StartsWith("SECRET_", StringComparison.Ordinal)
                ? SecretProvider.GetSecret(name)
                : Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                result.Errors.Add($"Missing required environment variable: {name}");
        }

        var connectorId = Environment.GetEnvironmentVariable("CONNECTOR_ID");
        if (!string.IsNullOrWhiteSpace(connectorId)
            && AppConfig.ValidateConnectorId(connectorId) is { } idError)
        {
            result.Errors.Add(idError);
        }

        // schema.json
        var schemaFile = schemaPath ?? SchemaConfig.DefaultPath;
        if (!File.Exists(schemaFile))
        {
            result.Errors.Add($"Missing config file: {schemaFile}");
        }
        else
        {
            try
            {
                var schema = SchemaConfig.Load(schemaFile);
                foreach (var obj in schema.ObjectList)
                {
                    if (obj.AclMode is not ("projectMembers" or "ownerOnly" or "public"))
                        result.Errors.Add(
                            $"schema.json: object '{obj.ObjectName}' has invalid aclMode '{obj.AclMode}' "
                            + "(projectMembers | ownerOnly | public).");
                }

                // GRAPH_CONNECTION_SHARDS must be a valid exact partition of the
                // object list — a bad shard map must fail preflight, not mid-crawl.
                if (ShardingConfig.IsEnabled
                    && !ShardingConfig.TryLoad(schema, out _, out var shardError)
                    && shardError is not null)
                {
                    result.Errors.Add(shardError);
                }
            }
            catch (Exception exc)
            {
                result.Errors.Add($"{SchemaJsonErrorPrefix} {exc.Message}");
            }
        }

        // graph-schema.json
        var graphSchemaFile = graphSchemaPath ?? ConnectionManager.DefaultGraphSchemaPath;
        if (!File.Exists(graphSchemaFile))
        {
            result.Errors.Add($"Missing config file: {graphSchemaFile}");
        }
        else
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(graphSchemaFile)) is not JsonArray properties)
                {
                    result.Errors.Add("graph-schema.json must be a JSON array of property definitions.");
                }
                else
                {
                    foreach (var property in properties)
                    {
                        if (property?["name"] is null || property["type"] is null)
                        {
                            result.Errors.Add(
                                "graph-schema.json: every property needs 'name' and 'type'.");
                            break;
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                result.Errors.Add($"graph-schema.json invalid: {exc.Message}");
            }
        }

        // ── SCHEMA DRIFT: what the code stamps vs what graph-schema.json declares ──
        //
        // This used to exist ONLY as a unit test, so an operator running
        // `validate-config` on a deployment host against a hand-edited
        // graph-schema.json got no drift signal at all — the first sign was
        // Graph rejecting items mid-crawl. It is a preflight now.
        //
        // The stamped side is collected by EXECUTING stamper call sites
        // (StampedPropertyInventory) rather than by reading a list of registered
        // NAMES — but the set of call sites it executes is itself maintained, so
        // this is a best-effort early warning, NOT the drift guarantee. A stamp
        // written somewhere the inventory does not invoke passes here and is
        // caught by GraphPropertyBag at the moment of the stamp instead, which
        // aborts the crawl. Do not read a clean preflight as proof of no drift.
        AddSchemaDriftFindings(result, schemaFile, graphSchemaFile);

        // Cross-flag consistency.
        if (EnvFlags.HaMode && !EnvFlags.UseSqlServer)
            result.Errors.Add("HA_MODE=true requires USE_SQL_SERVER=true and SQL_CONNECTION_STRING.");

        if (EnvFlags.IsTrue("USE_KEY_VAULT")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEY_VAULT_URI")))
        {
            result.Errors.Add("USE_KEY_VAULT=true requires KEY_VAULT_URI.");
        }

        var financialMode = EnvFlags.GetString("FINANCIAL_DATA_MODE", "filter").ToLowerInvariant();
        if (financialMode is not ("tag" or "filter" or "acl"))
            result.Errors.Add("FINANCIAL_DATA_MODE must be one of tag | filter | acl.");
        if (financialMode == "acl"
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FINANCIAL_DATA_GROUP_ID")))
        {
            result.Errors.Add("FINANCIAL_DATA_MODE=acl requires FINANCIAL_DATA_GROUP_ID.");
        }
        // Loud note: tag mode CLASSIFIES but does NOT restrict financial values —
        // any reader with item access still sees the figures through Copilot.
        if (financialMode == "tag")
        {
            result.Warnings.Add(
                "FINANCIAL_DATA_MODE=tag classifies financial data but does NOT restrict it — "
                + "figures remain visible to any reader with item access. Use filter (default) to "
                + "redact values, or acl to lock items to FINANCIAL_DATA_GROUP_ID.");
        }

        // Classification is an ADVISORY connector tag, not a Purview-enforced
        // label. Enforcement (CLASSIFICATION_ENFORCE_ACL) needs a target group,
        // and only does anything when CLASSIFICATION is on.
        if (EnvFlags.IsTrue("CLASSIFICATION_ENFORCE_ACL"))
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("CLASSIFICATION_RESTRICTED_GROUP_ID")))
            {
                result.Errors.Add(
                    "CLASSIFICATION_ENFORCE_ACL=true requires CLASSIFICATION_RESTRICTED_GROUP_ID.");
            }
            if (!EnvFlags.IsTrue("CLASSIFICATION"))
            {
                result.Warnings.Add(
                    "CLASSIFICATION_ENFORCE_ACL=true has no effect unless CLASSIFICATION=true "
                    + "(the SensitivityLabel tag must be derived before it can be enforced).");
            }
        }

        // Content gate (docs/CONTENT_GATE.md). Preflight catches the two
        // configurations an operator regrets discovering mid-crawl: a fail-mode
        // typo, and "gate on, binaries ingested, no scanner" — which is a valid
        // but total block of every attachment.
        foreach (var name in new[]
                 {
                     "CONTENT_GATE_FAIL_MODE", "CONTENT_GATE_FAIL_MODE_BINARY",
                     "CONTENT_GATE_FAIL_MODE_TEXT",
                 })
        {
            var mode = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(mode)
                && mode.Trim().ToLowerInvariant() is not ("closed" or "open"))
            {
                result.Errors.Add($"{name} must be one of closed | open.");
            }
        }

        if (EnvFlags.IsTrue("CONTENT_GATE"))
        {
            var icapUrl = Environment.GetEnvironmentVariable("CONTENT_GATE_ICAP_URL");
            var binaryFailOpen = string.Equals(
                EnvFlags.GetString(
                    "CONTENT_GATE_FAIL_MODE_BINARY",
                    EnvFlags.GetString("CONTENT_GATE_FAIL_MODE", "closed")),
                "open", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(icapUrl)
                && EnvFlags.IsTrue("ATTACHMENT_INGESTION")
                && !binaryFailOpen)
            {
                result.Warnings.Add(
                    "CONTENT_GATE=true with ATTACHMENT_INGESTION=true but no CONTENT_GATE_ICAP_URL: "
                    + "no binary scanner is wired and the binary fail mode is closed, so EVERY "
                    + "attachment will be quarantined. Set CONTENT_GATE_ICAP_URL, or accept the "
                    + "risk explicitly with CONTENT_GATE_FAIL_MODE_BINARY=open.");
            }
            if (binaryFailOpen)
            {
                result.Warnings.Add(
                    "CONTENT_GATE_FAIL_MODE_BINARY=open means UNSCANNED BINARY CONTENT CAN BE "
                    + "INDEXED when the scanner is unreachable. The shipped default is closed.");
            }

            var patterns = ContentGate.InjectionScanner.Load();
            if (patterns.PatternCount == 0)
            {
                result.Warnings.Add(
                    $"CONTENT_GATE=true but no usable injection patterns loaded from "
                    + $"config/{ContentGate.InjectionScanner.ConfigFileName}. The text scanner is "
                    + "BLIND and CONTENT_GATE_FAIL_MODE_TEXT decides what happens (default open = "
                    + "ingestion continues UNPROTECTED).");
            }
        }

        var tdwPath = Environment.GetEnvironmentVariable("TDW_EXPORT_PATH");
        if (!string.IsNullOrWhiteSpace(tdwPath) && !Directory.Exists(tdwPath))
            result.Warnings.Add($"TDW_EXPORT_PATH '{tdwPath}' does not exist; full crawls will use the REST API.");

        var logFormat = Environment.GetEnvironmentVariable("LOG_FORMAT");
        if (!string.IsNullOrEmpty(logFormat)
            && !logFormat.Equals("json", StringComparison.OrdinalIgnoreCase)
            && !logFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add($"LOG_FORMAT '{logFormat}' is not 'text' or 'json'; treating as text.");
        }

        var budget = EnvFlags.GetInt("CLARIZEN_API_CALLS_PER_DAY", 25_000);
        if (budget < 1000)
            result.Warnings.Add(
                $"CLARIZEN_API_CALLS_PER_DAY={budget} is very low; crawls may pause on quota often.");

        // Webhook receiver: a configured port with no secret is fail-closed
        // (the receiver refuses to start). Surface it as an error at preflight.
        var webhookPort = Environment.GetEnvironmentVariable(Webhook.WebhookReceiver.PortEnvVar);
        if (!string.IsNullOrWhiteSpace(webhookPort)
            && int.TryParse(webhookPort, out var wp) && wp > 0
            && string.IsNullOrWhiteSpace(SecretProvider.GetSecret(Webhook.WebhookReceiver.SecretEnvVar)))
        {
            result.Errors.Add(
                $"{Webhook.WebhookReceiver.PortEnvVar}={wp} requires {Webhook.WebhookReceiver.SecretEnvVar} "
                + "(the receiver refuses to start unauthenticated).");
        }

        return result;
    }

    /// <summary>
    /// Compare the Graph property names the code ACTUALLY stamps against the
    /// names config/graph-schema.json declares, in both directions.
    /// <list type="bullet">
    ///   <item>stamped but undeclared → ERROR. Graph rejects the property; the
    ///   connector is undeployable and every affected item fails.</item>
    ///   <item>declared but never stamped → WARNING. Harmless dead schema, but
    ///   usually the trace of a rename that only got applied on one side.</item>
    /// </list>
    /// Both files must have parsed for this to mean anything. When one did not,
    /// this stays silent ONLY if the earlier checks demonstrably recorded an
    /// error for that same file — it CHECKS that rather than assuming it.
    /// <para>
    /// The previous version wrapped both loads in a blanket
    /// <c>catch { return; }</c> justified by that assumption. The assumption was
    /// false for a degenerate graph-schema.json (<c>[]</c>, or entries whose
    /// name is empty): the earlier array/field checks pass it, the swallowed
    /// <see cref="InvalidDataException"/> produced no finding, and
    /// <c>validate-config --strict</c> reported a clean config for a file that
    /// makes the first property stamp of the crawl throw.
    /// </para>
    /// </summary>
    internal static void AddSchemaDriftFindings(
        Result result, string schemaFile, string graphSchemaFile)
    {
        if (!File.Exists(schemaFile) || !File.Exists(graphSchemaFile))
            return;

        SchemaConfig schema;
        try
        {
            schema = SchemaConfig.Load(schemaFile);
        }
        catch (Exception exc)
        {
            if (!result.Errors.Any(e => e.StartsWith(SchemaJsonErrorPrefix, StringComparison.Ordinal)))
            {
                result.Errors.Add(
                    $"schema.json could not be loaded, so Graph schema drift could not be checked: "
                    + exc.Message);
            }
            return;
        }

        HashSet<string> declared;
        try
        {
            declared = GraphPropertyRegistry.ReadDeclaredNames(graphSchemaFile);
        }
        catch (Exception exc)
        {
            if (!result.Errors.Any(e => e.StartsWith(GraphSchemaJsonErrorPrefix, StringComparison.Ordinal)))
            {
                result.Errors.Add(
                    "graph-schema.json is unusable as a Graph property declaration: " + exc.Message
                    + " The connector loads this same file at runtime to decide which properties it "
                    + "may stamp, so with this file every record would fail to transform.");
            }
            return;
        }

        List<string> stamped;
        try
        {
            stamped = StampedPropertyInventory.Collect(schema).ToList();
        }
        catch (Exception exc)
        {
            result.Errors.Add(
                "Could not determine which Graph properties the connector stamps, so schema drift "
                + $"could not be checked: {exc.Message}");
            return;
        }

        var undeclared = stamped.Where(name => !declared.Contains(name)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (undeclared.Count > 0)
        {
            result.Errors.Add(
                "graph-schema.json does not declare property name(s) the connector stamps on "
                + $"external items: {string.Join(", ", undeclared)}. Microsoft Graph REJECTS "
                + "undeclared properties, so every item carrying one will fail to ingest. Add them "
                + "to config/graph-schema.json.");
        }

        var unused = declared.Where(name => !stamped.Contains(name)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (unused.Count > 0)
        {
            result.Warnings.Add(
                "graph-schema.json declares property name(s) nothing stamps: "
                + $"{string.Join(", ", unused)}. Harmless, but usually a rename applied to only one "
                + "of schema.json / graph-schema.json.");
        }
    }

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var strict = args.HasFlag("--strict");
        EnvLoader.LoadLayered();
        Logging.Initialize("validate_config", args.Verbose);
        Dashboard.Banner("Validate config" + (strict ? " (strict)" : string.Empty));

        var result = ValidateCore();

        // Report the circuit-breaker configuration (informational).
        var cb = CircuitBreakerOptions.FromEnv();
        if (!cb.Enabled)
        {
            Dashboard.Line("Circuit breakers: disabled (CIRCUIT_BREAKER=false — pure passthrough).");
        }
        else
        {
            Dashboard.Line(
                $"Circuit breakers: enabled — threshold {cb.FailureThreshold} failures / "
                + $"{cb.Window.TotalSeconds:0}s window, open {cb.OpenDuration.TotalSeconds:0}s, "
                + $"{cb.HalfOpenTrials} half-open trial(s). Guarded: clarizen, graph.");
        }

        // Report the tracing target (informational — never a failure).
        var otelEndpoint = Environment.GetEnvironmentVariable(Tracing.EndpointEnvVar);
        if (string.IsNullOrWhiteSpace(otelEndpoint))
        {
            Dashboard.Line("OpenTelemetry tracing: disabled (OTEL_EXPORTER_OTLP_ENDPOINT not set).");
        }
        else
        {
            var serviceName = Tracing.ResolveServiceName(
                Environment.GetEnvironmentVariable("CONNECTOR_NAME") ?? "Clarizen AdaptiveWork");
            Dashboard.Line(
                $"OpenTelemetry tracing: exporting to '{otelEndpoint}' as service '{serviceName}'.");
        }

        // Best-effort connectivity — required to pass only under --strict.
        if (result.Errors.Count == 0)
        {
            try
            {
                var config = AppConfig.Load();
                var clarizen = new ClarizenClient(config);
                var clarizenOk = await clarizen.PingAsync(ServiceStop.Token);
                if (!clarizenOk)
                    (strict ? result.Errors : result.Warnings).Add("Clarizen connectivity check failed.");
                else
                    Dashboard.Line("Clarizen connectivity: OK");

                try
                {
                    var graph = new GraphClient(config);
                    await graph.GetTokenAsync(ServiceStop.Token);
                    Dashboard.Line("Graph token acquisition: OK");
                }
                catch (Exception exc)
                {
                    (strict ? result.Errors : result.Warnings).Add(
                        $"Graph token acquisition failed: {exc.Message}");
                }
            }
            catch (Exception exc)
            {
                (strict ? result.Errors : result.Warnings).Add($"Connectivity checks skipped: {exc.Message}");
            }
        }

        foreach (var warning in result.Warnings)
            Dashboard.Line($"WARNING: {warning}");
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"ERROR: {error}");

        var ok = result.Ok(strict);
        Dashboard.Line(ok
            ? "Configuration looks good."
            : $"Configuration is NOT valid ({result.Errors.Count} error(s), {result.Warnings.Count} warning(s)).");
        return ok;
    }
}
