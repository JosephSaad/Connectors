// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// validate-config command — preflight environment / config / connectivity checks.
//
// Runs a series of read-only checks and prints a clear, sectioned PASS/FAIL
// report so an operator can catch misconfiguration *before* attempting a real
// deployment. Nothing is written or mutated; at most, best-effort auth tokens
// are requested to confirm reachability.
//
// Checks (in order):
//   1. Env / config load     — Settings.LoadConfig() succeeds; report connector
//                               id / name / SF instance / ACL mode. Config errors
//                               (missing env vars, invalid connector id) are FAIL.
//   2. Config files          — schema.json / graph-schema.json / template.json
//                               parse and are structurally sane; report counts.
//   3. Schema sanity         — object-name list / OWD field map / parent map build;
//                               cross-references resolve. Suspicious refs WARN.
//   4. Connectivity          — best-effort, network-optional. Salesforce token and
//                               a Graph token are attempted only when creds are
//                               present; otherwise SKIP. Network failures WARN;
//                               auth failures (creds present) FAIL.
//
// --strict promotes connectivity/schema WARNs to FAIL.
// --verbose prints all INFO+ log lines to the console (as with the other commands).
//
// Usage::
//
//     run.py validate-config
//     run.py validate-config --verbose
//     run.py validate-config --strict
//
// Returns ``true`` when there are no hard failures, ``false`` otherwise.

using System.Globalization;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class ValidateConfig
{
    // ── Test hooks ───────────────────────────────────────────────────────────
    // Mirror the monkeypatch-style seams the other commands expose so tests can
    // run this command hermetically (no real network, no real Salesforce/Graph).
    // Default to the real implementations; behavior is identical when unhooked.

    /// <summary>Loads the full AppConfig (patched in tests to inject failures).</summary>
    internal static Func<AppConfig> LoadConfigHook = Settings.LoadConfig;

    /// <summary>Requests a Salesforce access token (best-effort reachability probe).</summary>
    internal static Func<AppConfig, Task<string>> SalesforceTokenHook = ApiClient.GetSalesforceAccessTokenAsync;

    /// <summary>Requests a Microsoft Graph token (best-effort reachability probe).</summary>
    internal static Func<AppConfig, Task> GraphTokenHook = AcquireGraphTokenAsync;

    private static readonly IAppLogger Logger = Logging.GetLogger("validate_config");

    // ── Check outcome tracking ────────────────────────────────────────────────

    private enum Outcome
    {
        Pass,
        Warn,
        Fail,
        Skip,
    }

    /// <summary>Accumulates check results and renders the sectioned report.</summary>
    private sealed class Report
    {
        private readonly IAppLogger _progress;
        private readonly bool _strict;

        public int Passed;
        public int Warned;
        public int Failed;
        public int Skipped;

        public Report(IAppLogger progress, bool strict)
        {
            _progress = progress;
            _strict = strict;
        }

        /// <summary>Print a section header.</summary>
        public void Section(string title)
        {
            _progress.Info("");
            _progress.Info($"── {title} " + new string('─', Math.Max(0, 66 - title.Length)));
        }

        public void Pass(string message) => Emit(Outcome.Pass, message);

        public void Fail(string message) => Emit(Outcome.Fail, message);

        public void Skip(string message) => Emit(Outcome.Skip, message);

        /// <summary>
        /// Record a soft issue. Reported as WARN normally; promoted to FAIL when
        /// <c>--strict</c> is active (used for connectivity and schema-sanity issues).
        /// </summary>
        public void WarnOrFail(string message)
        {
            if (_strict)
                Emit(Outcome.Fail, message + " [strict]");
            else
                Emit(Outcome.Warn, message);
        }

        private void Emit(Outcome outcome, string message)
        {
            switch (outcome)
            {
                case Outcome.Pass:
                    Passed++;
                    _progress.Info($"  [PASS] {message}");
                    break;
                case Outcome.Warn:
                    Warned++;
                    _progress.Info($"  [WARN] {message}");
                    break;
                case Outcome.Fail:
                    Failed++;
                    _progress.Info($"  [FAIL] {message}");
                    break;
                case Outcome.Skip:
                    Skipped++;
                    _progress.Info($"  [SKIP] {message}");
                    break;
            }
        }
    }

    /// <summary>
    /// Run all preflight checks and print a PASS/FAIL report.
    /// Returns <c>true</c> when no hard failures were recorded.
    /// </summary>
    public static async Task<bool> RunAsync(ParsedArgs args)
    {
        var inv = CultureInfo.InvariantCulture;
        var verbose = args.GetBool("verbose");
        var strict = args.GetBool("strict");
        var (logFile, _) = CommandRegistry.SetupLogging("validate_config", verbose: verbose);
        var progress = Logging.GetLogger("progress");
        var startTime = CommandRegistry.MonotonicSeconds();

        var report = new Report(progress, strict);

        Logger.Info($"📄 Logging to: {logFile}");
        progress.Info(new string('=', 70));
        progress.Info("VALIDATE CONFIG: env → config files → schema sanity → connectivity");
        if (strict)
            progress.Info("(strict mode: connectivity/schema warnings are treated as failures)");
        progress.Info(new string('=', 70));

        // ── Check 1: env / config load ────────────────────────────────────────
        report.Section("1. Environment & configuration load");
        AppConfig? config = null;
        try
        {
            config = LoadConfigHook();
            report.Pass("Settings.LoadConfig() succeeded");
            report.Pass($"Connector ID:        {config.Connector.Id}");
            report.Pass($"Connector name:      {config.Connector.Name}");
            report.Pass($"Salesforce instance: {config.Connector.Salesforce.InstanceUrl}");
            report.Pass($"ACL mode:            {AclModeLabel(config)}");
        }
        catch (Exception ex)
        {
            // Missing required env vars / invalid connector id surface here with
            // their exact message (ArgumentException from Settings). Report the
            // exact text and stop — later checks depend on a loaded config.
            report.Fail($"Config load failed: {ex.Message}");
        }

        // ── Check 2: config files parse & are structurally sane ───────────────
        report.Section("2. Config files");
        var configDir = ResolveConfigDir();
        Logger.Info($"Reading config files from: {configDir}");
        // The parsed schema.json drives Check 3; overall pass/fail is derived from
        // report.Failed, so the per-file all-ok flag is not needed here.
        var (schemaObj, _) = CheckConfigFiles(configDir, report);

        // ── Check 3: schema sanity (object list / OWD map / parent map) ───────
        report.Section("3. Schema sanity");
        if (schemaObj is not null)
            CheckSchemaSanity(schemaObj, report);
        else
            report.WarnOrFail("Skipping schema sanity — schema.json did not parse");

        // ── Check 4: connectivity (best-effort, network-optional) ─────────────
        report.Section("4. Connectivity (best-effort)");
        if (config is not null)
        {
            await CheckSalesforceAsync(config, report);
            await CheckGraphAsync(config, report);
        }
        else
        {
            report.Skip("Salesforce connectivity — config did not load");
            report.Skip("Graph connectivity — config did not load");
        }

        // ── Summary ───────────────────────────────────────────────────────────
        var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
        var ok = report.Failed == 0;
        progress.Info("");
        progress.Info(new string('=', 70));
        progress.Info(
            $"VALIDATION {(ok ? "PASSED" : "FAILED")} in {elapsed.ToString("F1", inv)}s — " +
            $"{report.Passed} passed, {report.Warned} warning(s), {report.Failed} failure(s), {report.Skipped} skipped.");
        if (!ok)
            progress.Info($"❌ {report.Failed} hard failure(s) — see {logFile} for details.");
        else if (report.Warned > 0)
            progress.Info("⚠ Passed with warnings — review the WARN lines above.");
        else
            progress.Info("✅ All checks passed.");
        progress.Info(new string('=', 70));

        return ok;
    }

    // ── Check 1 helpers ───────────────────────────────────────────────────────

    private static string AclModeLabel(AppConfig config) =>
        config.UseGroupAcl ? "GROUP" : (config.UseNewAclEngine ? "NEW" : "LEGACY");

    // ── Check 2 helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the directory the config files live in. Honors <c>SFCONNECTOR_HOME</c>
    /// (the same working-directory override ServiceHost uses), so a test can point
    /// the structural checks at a temp config tree. Falls back to
    /// <c>&lt;cwd&gt;/config</c>, matching Settings.LoadConfigJson.
    /// </summary>
    private static string ResolveConfigDir()
    {
        var home = Environment.GetEnvironmentVariable("SFCONNECTOR_HOME");
        var root = !string.IsNullOrWhiteSpace(home) ? home : Settings.RepoRoot;
        return Path.Combine(root, "config");
    }

    /// <summary>
    /// Parse the three config files and check basic shape. Returns the parsed
    /// schema.json object (or null if it could not be read as an object) plus a
    /// flag that is true only when all three files parsed and were shape-valid.
    /// </summary>
    private static (JsonObject? Schema, bool AllOk) CheckConfigFiles(string configDir, Report report)
    {
        var allOk = true;

        // schema.json → object with a non-empty objectList; every object has fields.
        JsonObject? schema = null;
        var schemaPath = Path.Combine(configDir, "schema.json");
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(schemaPath));
            schema = node as JsonObject;
            if (schema is null)
            {
                report.Fail("schema.json: expected a JSON object at the top level");
                allOk = false;
            }
            else if (schema["objectList"] is not JsonArray objectList || objectList.Count == 0)
            {
                report.Fail("schema.json: 'objectList' is missing or empty");
                schema = null;
                allOk = false;
            }
            else
            {
                var objectsWithoutFields = 0;
                var totalSelectedFields = 0;
                foreach (var entry in objectList)
                {
                    if (entry is not JsonObject obj)
                        continue;
                    if (obj["selectedFields"] is JsonObject fields && fields.Count > 0)
                        totalSelectedFields += fields.Count;
                    else
                        objectsWithoutFields++;
                }
                report.Pass($"schema.json parsed — {objectList.Count} object(s), {totalSelectedFields} total selected field(s)");
                if (objectsWithoutFields > 0)
                    report.WarnOrFail($"schema.json: {objectsWithoutFields} object(s) declare no selectedFields");
            }
        }
        catch (FileNotFoundException)
        {
            report.Fail($"schema.json not found at {schemaPath}");
            allOk = false;
        }
        catch (Exception ex)
        {
            report.Fail($"schema.json failed to parse: {ex.Message}");
            allOk = false;
        }

        // graph-schema.json → non-empty array of property objects (each has a name).
        var graphSchemaPath = Path.Combine(configDir, "graph-schema.json");
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(graphSchemaPath));
            if (node is not JsonArray props || props.Count == 0)
            {
                report.Fail("graph-schema.json: expected a non-empty JSON array of properties");
                allOk = false;
            }
            else
            {
                var named = props.OfType<JsonObject>().Count(p => !string.IsNullOrEmpty(p["name"]?.GetValue<string>()));
                report.Pass($"graph-schema.json parsed — {props.Count} property/properties ({named} named)");
                if (named == 0)
                {
                    report.Fail("graph-schema.json: no property has a 'name'");
                    allOk = false;
                }
            }
        }
        catch (FileNotFoundException)
        {
            report.Fail($"graph-schema.json not found at {graphSchemaPath}");
            allOk = false;
        }
        catch (Exception ex)
        {
            report.Fail($"graph-schema.json failed to parse: {ex.Message}");
            allOk = false;
        }

        // template.json → any valid JSON object (adaptive card).
        var templatePath = Path.Combine(configDir, "template.json");
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(templatePath));
            if (node is JsonObject tmpl && tmpl.Count > 0)
                report.Pass($"template.json parsed — {tmpl.Count} top-level key(s)");
            else
            {
                report.Fail("template.json: expected a non-empty JSON object");
                allOk = false;
            }
        }
        catch (FileNotFoundException)
        {
            report.Fail($"template.json not found at {templatePath}");
            allOk = false;
        }
        catch (Exception ex)
        {
            report.Fail($"template.json failed to parse: {ex.Message}");
            allOk = false;
        }

        return (schema, allOk);
    }

    // ── Check 3 helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Verify the derived maps build and cross-reference cleanly against the
    /// object-name list. Missing references are soft issues (WARN, or FAIL under
    /// <c>--strict</c>) — the schema can legitimately name parents/OWD fields for
    /// objects that are configured elsewhere.
    /// </summary>
    private static void CheckSchemaSanity(JsonObject schema, Report report)
    {
        List<string> objectNames;
        Dictionary<string, string> owdFieldMap;
        Dictionary<string, (string ParentFieldName, string ParentObjectName)> parentMap;

        try
        {
            objectNames = Settings.BuildObjectNameList(schema);
            owdFieldMap = Settings.BuildOwdFieldMap(schema);
            parentMap = Settings.BuildParentMap(schema);
        }
        catch (Exception ex)
        {
            report.Fail($"Failed to build schema maps: {ex.Message}");
            return;
        }

        if (objectNames.Count == 0)
        {
            report.Fail("Object name list is empty");
            return;
        }

        report.Pass(
            $"Derived maps built — {objectNames.Count} object(s), " +
            $"{owdFieldMap.Count} OWD field(s), {parentMap.Count} parent link(s)");

        var known = new HashSet<string>(objectNames, StringComparer.Ordinal);

        // Duplicate object names are always a real problem.
        var duplicates = objectNames
            .GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            report.Fail($"Duplicate object name(s) in schema: {string.Join(", ", duplicates)}");

        // Parent map: parentObjectName should reference a known object.
        var unknownParents = parentMap
            .Where(kv => !known.Contains(kv.Value.ParentObjectName))
            .Select(kv => $"{kv.Key} → {kv.Value.ParentObjectName}")
            .ToList();
        if (unknownParents.Count > 0)
            report.WarnOrFail($"Parent map references unknown object(s): {string.Join(", ", unknownParents)}");
        else
            report.Pass("Parent map references only known objects");

        // OWD field map: keys should be known objects.
        var unknownOwd = owdFieldMap.Keys.Where(k => !known.Contains(k)).ToList();
        if (unknownOwd.Count > 0)
            report.WarnOrFail($"OWD field map references unknown object(s): {string.Join(", ", unknownOwd)}");
        else
            report.Pass("OWD field map references only known objects");
    }

    // ── Check 4 helpers ───────────────────────────────────────────────────────

    /// <summary>True when all three Salesforce credential fields are present.</summary>
    private static bool HasSalesforceCreds(AppConfig config) =>
        !string.IsNullOrEmpty(config.Connector.Salesforce.InstanceUrl) &&
        !string.IsNullOrEmpty(config.Connector.Salesforce.ClientId) &&
        !string.IsNullOrEmpty(config.Connector.Salesforce.ClientSecret);

    /// <summary>True when Azure/Graph credentials are present (client + tenant + secret).</summary>
    private static bool HasGraphCreds(AppConfig config) =>
        !string.IsNullOrEmpty(config.ClientId) &&
        !string.IsNullOrEmpty(config.TenantId) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET"));

    private static async Task CheckSalesforceAsync(AppConfig config, Report report)
    {
        if (!HasSalesforceCreds(config))
        {
            report.Skip("Salesforce connectivity — credentials not present (INSTANCE_URL/CLIENT_ID/CLIENT_SECRET)");
            return;
        }

        try
        {
            var token = await SalesforceTokenHook(config);
            if (!string.IsNullOrEmpty(token))
                report.Pass($"Salesforce reachable — token acquired ({config.Connector.Salesforce.InstanceUrl})");
            else
                report.WarnOrFail("Salesforce token response contained no access token");
        }
        catch (Exception ex)
        {
            // GetSalesforceAccessTokenAsync throws InvalidOperationException with the
            // HTTP status baked into the message on an auth rejection; a genuine
            // network failure throws HttpRequestException / TaskCanceledException.
            if (IsSalesforceAuthError(ex))
                report.Fail($"Salesforce authentication rejected: {ex.Message}");
            else
                report.WarnOrFail($"Salesforce unreachable (environment may be offline): {ex.Message}");
        }
    }

    private static async Task CheckGraphAsync(AppConfig config, Report report)
    {
        if (!HasGraphCreds(config))
        {
            report.Skip("Graph connectivity — Azure credentials not present (AZURE_CLIENT_ID/TENANT_ID/CLIENT_SECRET)");
            return;
        }

        try
        {
            await GraphTokenHook(config);
            report.Pass("Microsoft Graph reachable — token acquired");
        }
        catch (AuthenticationFailedException ex)
        {
            report.Fail($"Graph authentication failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            report.WarnOrFail($"Graph token unavailable (environment may be offline): {ex.Message}");
        }
    }

    /// <summary>
    /// Classify a Salesforce token failure as an auth rejection (creds are wrong)
    /// vs. a transport failure (offline). GetSalesforceAccessTokenAsync formats
    /// auth rejections as "Failed to authenticate with Salesforce: {status} ...".
    /// </summary>
    private static bool IsSalesforceAuthError(Exception ex)
    {
        if (ex is not InvalidOperationException)
            return false;
        var msg = ex.Message;
        return msg.Contains("Failed to authenticate with Salesforce: 400", StringComparison.Ordinal)
            || msg.Contains("Failed to authenticate with Salesforce: 401", StringComparison.Ordinal)
            || msg.Contains("Failed to authenticate with Salesforce: 403", StringComparison.Ordinal)
            || msg.Contains("did not contain an access token", StringComparison.Ordinal);
    }

    /// <summary>
    /// Best-effort Graph token acquisition using the same client-credentials
    /// identity the connector deploys with. Kept separate from GraphClient (which
    /// uses DefaultAzureCredential) so the probe is explicit about which secret it
    /// exercises. Throws on failure; the caller classifies the exception.
    /// </summary>
    private static async Task AcquireGraphTokenAsync(AppConfig config)
    {
        var secret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") ?? "";
        var credential = new ClientSecretCredential(config.TenantId, config.ClientId, secret);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await credential.GetTokenAsync(new TokenRequestContext(new[] { GraphClient.GraphScope }), cts.Token);
    }
}
