// Commands/ValidateConfig.cs
// --------------------------
// `validate-config [--strict]` — preflight checks before a long crawl:
//
//   • required env vars present, CONNECTOR_ID shape valid
//   • HDFS_MODE consistency (webhdfs needs HDFS_NAMENODE_URL, localpath
//     needs BDH_EXPORT_PATH)
//   • EVERY config file the connector reads — config/schema.json,
//     config/graph-schema.json, config/filters.json AND config/classification.json
//     — parses and is sane (a malformed filter is an ERROR, never ignored).
//     That set is the whole of it: those four files are the only `config/`
//     paths in the source. The invariant: no config, in any file, may pass
//     `validate-config --strict` green and then fail at crawl time.
//   • graph-schema.json declares every Graph property the object list PRODUCES
//     (selectedFields-mapped names as the production converter actually emits
//     them — drop/mask/_bdh_ aware — plus the always-emitted set, including the
//     classifier pair regardless of CLASSIFICATION). Undeclared ⇒ ERROR: Graph
//     rejects the item at push time, so the config cannot work.
//   • the fail-closed scale guard: objects with NO filter and no exemption
//     are a warning — and an ERROR under --strict (deploying an unfiltered
//     150M-row object is an outage waiting to happen)
//   • cross-flag consistency (HA needs SQL, Key Vault needs URI, ...)
//   • --strict additionally requires BDH source connectivity and a Graph
//     token to succeed, and turns warnings into failures.

using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;
using HadoopConnector.Item;

namespace HadoopConnector.Commands;

public static class ValidateConfig
{
    internal static readonly string[] RequiredEnvVars =
    {
        "CONNECTOR_ID",
        "AAD_APP_TENANT_ID",
        "AAD_APP_CLIENT_ID",
        "SECRET_AAD_APP_CLIENT_SECRET",
    };

    public sealed class Result
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        /// <summary>Findings a human has explicitly attested to in config (today:
        /// the coarse-ACL posture via <c>coarseAclAcknowledged</c>). They are
        /// still REPORTED on every run — the exposure is real and does not go
        /// away — but they do not gate <c>--strict</c>, because the whole point
        /// of the attestation is that someone accountable already accepted it.
        /// Kept apart from <see cref="Warnings"/> so "accepted" can never be
        /// confused with "clean".</summary>
        public List<string> AcknowledgedWarnings { get; } = new();

        /// <summary>Purely INFORMATIONAL posture statements — never a finding and
        /// never gating, under --strict or otherwise. Today: the per-column
        /// drop/mask policies (WP-HD-3). A configured restriction is good news,
        /// so it does not belong in <see cref="Warnings"/>; but it must still be
        /// printed, because an operator has to be able to see which columns are
        /// restricted — and that NONE are — without reading schema.json.</summary>
        public List<string> Notices { get; } = new();

        public bool Ok(bool strict) => Errors.Count == 0 && (!strict || Warnings.Count == 0);
    }

    /// <summary>Offline validation (no network). Testable.</summary>
    internal static Result ValidateCore(
        string? schemaPath = null, string? graphSchemaPath = null, string? filtersPath = null,
        string? classificationPath = null,
        bool strict = false)
    {
        var result = new Result();

        foreach (var name in RequiredEnvVars)
        {
            string? value;
            try
            {
                value = name.StartsWith("SECRET_", StringComparison.Ordinal)
                    ? SecretProvider.GetSecret(name)
                    : Environment.GetEnvironmentVariable(name);
            }
            catch (ArgumentException exc)
            {
                // e.g. USE_KEY_VAULT=true without KEY_VAULT_URI — report and
                // keep validating instead of aborting preflight.
                result.Errors.Add(exc.Message.Replace("Invalid configuration: ", string.Empty));
                continue;
            }
            if (string.IsNullOrWhiteSpace(value))
                result.Errors.Add($"Missing required environment variable: {name}");
        }

        var connectorId = Environment.GetEnvironmentVariable("CONNECTOR_ID");
        if (!string.IsNullOrWhiteSpace(connectorId)
            && AppConfig.ValidateConnectorId(connectorId) is { } idError)
        {
            result.Errors.Add(idError);
        }

        // HDFS_MODE consistency.
        var hdfsMode = EnvFlags.GetString("HDFS_MODE", "webhdfs").ToLowerInvariant();
        if (hdfsMode is not ("webhdfs" or "localpath"))
        {
            result.Errors.Add($"HDFS_MODE '{hdfsMode}' is invalid (webhdfs | localpath).");
        }
        else if (hdfsMode == "webhdfs"
                 && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HDFS_NAMENODE_URL")))
        {
            result.Errors.Add("HDFS_MODE=webhdfs requires HDFS_NAMENODE_URL "
                + "(e.g. http://namenode:9870/webhdfs/v1).");
        }
        else if (hdfsMode == "localpath")
        {
            var exportPath = Environment.GetEnvironmentVariable("BDH_EXPORT_PATH");
            if (string.IsNullOrWhiteSpace(exportPath))
                result.Errors.Add("HDFS_MODE=localpath requires BDH_EXPORT_PATH.");
            else if (!Directory.Exists(exportPath))
                result.Warnings.Add($"BDH_EXPORT_PATH '{exportPath}' does not exist.");
        }

        // schema.json
        SchemaConfig? schema = null;
        var schemaFile = schemaPath ?? SchemaConfig.DefaultPath;
        if (!File.Exists(schemaFile))
        {
            result.Errors.Add($"Missing config file: {schemaFile}");
        }
        else
        {
            try
            {
                schema = SchemaConfig.Load(schemaFile);

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
                result.Errors.Add($"schema.json invalid: {exc.Message}");
            }
        }

        // graph-schema.json
        var graphPropertyTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var graphSchemaFile = graphSchemaPath ?? ConnectionManager.DefaultGraphSchemaPath;
        // The produced-vs-declared cross-check below must only read a file this
        // block fully accepted — a partially parsed declaration would make the
        // drift report half-true. Tracked by error count, not by assumption.
        var graphSchemaErrorsBefore = result.Errors.Count;
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
                else if (properties.Count == 0)
                {
                    // Degenerate but well-formed JSON. Without this, an empty
                    // array used to sail through this block and the connection
                    // would be registered with NO properties — every item push
                    // then fails. (The Clarizen connector shipped exactly this
                    // hole behind a blanket catch; error, never silence.)
                    result.Errors.Add(
                        "graph-schema.json declares no properties at all (empty array). The "
                        + "connector emits properties on every item (at minimum "
                        + $"{string.Join(", ", AlwaysEmittedProperties.Names)}), so a "
                        + "connection registered from this file rejects every item at push time.");
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
                        // A non-string 'name' (or 'type') is not a Graph property
                        // name: JsonNode.ToString() would launder `123` or `true`
                        // into "123"/"True" and register it, so the registration
                        // Graph rejects would be discovered at connection setup
                        // rather than here.
                        if (property["name"] is not JsonValue nameValue
                            || !nameValue.TryGetValue<string>(out var propertyName))
                        {
                            result.Errors.Add(
                                $"graph-schema.json: property 'name' must be a string, but "
                                + $"{property["name"]!.ToJsonString()} is not.");
                            break;
                        }
                        // "" and "   " ARE strings, so they pass the check above
                        // — and Graph would reject the registration. Catch them
                        // here rather than at connection setup.
                        if (string.IsNullOrWhiteSpace(propertyName))
                        {
                            result.Errors.Add(
                                "graph-schema.json: a property has an empty or whitespace-only "
                                + "'name'. Graph property names must be non-empty; remove or name "
                                + "the entry.");
                            break;
                        }
                        if (property["type"] is not JsonValue typeValue
                            || !typeValue.TryGetValue<string>(out var propertyType))
                        {
                            result.Errors.Add(
                                $"graph-schema.json: property '{propertyName}' has a non-string "
                                + $"'type' ({property["type"]!.ToJsonString()}).");
                            break;
                        }
                        // Graph property names are case-insensitive, so two
                        // entries differing only in case declare the SAME
                        // property twice with (possibly) different types. The
                        // later one silently won this lookup, which is what the
                        // mask-vs-typed-property warning below reads.
                        if (graphPropertyTypes.TryGetValue(propertyName, out var firstType))
                        {
                            result.Errors.Add(
                                $"graph-schema.json: property '{propertyName}' is declared more "
                                + $"than once (first as '{firstType}', again as '{propertyType}'). "
                                + "Graph property names are matched case-insensitively; keep one "
                                + "declaration so which type applies is not file-order luck.");
                            break;
                        }
                        graphPropertyTypes[propertyName] = propertyType;
                    }
                }
            }
            catch (Exception exc)
            {
                result.Errors.Add($"graph-schema.json invalid: {exc.Message}");
            }
        }
        var graphSchemaUsable = result.Errors.Count == graphSchemaErrorsBefore;

        // ── Produced-vs-declared cross-check ─────────────────────────────────
        // graph-schema.json must declare every Graph property the object list
        // PRODUCES on external items — Graph REJECTS an item carrying an
        // undeclared property, so a gap here is not a style issue, it is a
        // config that cannot work. What "produces" means is answered by
        // ProducedGraphProperties, which executes the production converter and
        // reads the always-emitted registry rather than restating either
        // (restated rule sets drift; see that file's header). The always-emitted
        // set — including the classifier pair SensitivityLabel and
        // DetectedCategories — is required UNCONDITIONALLY, CLASSIFICATION on or
        // off: same rationale as the unconditional classification.json load
        // above, because the flag is what operators flip last.
        //
        // Runs only when BOTH files parsed clean: a produced-set computed from a
        // half-loaded schema, or diffed against a half-parsed declaration, would
        // report drift that is not real (and the parse failure is already an
        // error above — checked by counting, not assumed). No blanket
        // catch-and-return around the computation either: a failure to compute
        // the produced set is itself reported as an ERROR. Both lessons are
        // inherited from the Clarizen connector's drift-check history.
        if (schema is not null && graphSchemaUsable)
            AddGraphSchemaDriftFindings(result, schema, graphPropertyTypes);

        // filters.json — THE scale control. Malformed → error. Missing → every
        // object is unfiltered (guard applies below).
        FilterSet filters = FilterSet.Empty;
        var filtersFile = filtersPath
            ?? Environment.GetEnvironmentVariable("BDH_FILTERS_PATH")
            ?? FilterSet.DefaultPath;
        if (!File.Exists(filtersFile))
        {
            result.Warnings.Add(
                $"Missing filter config: {filtersFile} — every object will trip the "
                + "fail-closed full-scan guard unless exempted.");
        }
        else
        {
            try
            {
                filters = FilterSet.Load(filtersFile);
            }
            catch (Exception exc)
            {
                result.Errors.Add($"filters.json invalid: {exc.Message}");
            }
        }

        // classification.json — THE fourth and last config file this connector
        // reads (schema.json, graph-schema.json, filters.json, classification.json;
        // the set is closed by the four `config/` DefaultPath symbols in the
        // source). It is read by IngestPipeline's constructor whenever
        // CLASSIFICATION=true, so a file that only fails at LOAD time used to give
        // preflight-green followed by a crawl-time stack naming neither the file
        // nor the key. It is validated here by calling the SAME loader the crawl
        // calls, exactly as schema.json and filters.json are — the invariant being
        // that no config in any file can pass `validate-config --strict` green and
        // then fail at crawl time.
        var classificationFile = classificationPath ?? Content.ContentClassifier.DefaultPath;
        var classificationEnabled = EnvFlags.IsTrue("CLASSIFICATION");
        if (!File.Exists(classificationFile))
        {
            // Only read when CLASSIFICATION=true; absent is otherwise a non-event.
            if (classificationEnabled)
            {
                result.Errors.Add(
                    $"Missing config file: {classificationFile} — CLASSIFICATION=true reads it on "
                    + "every crawl, so the crawl would fail at startup.");
            }
        }
        else
        {
            try
            {
                // UNCONDITIONAL on purpose — a file that EXISTS is validated
                // whether or not CLASSIFICATION is currently true. The flag is
                // what operators flip last, so gating the load on it would bless
                // a broken file today and turn it into a startup crash the day
                // classification is switched on, with a green validate-config run
                // in the change ticket. (Absence is still judged by the flag,
                // above: an absent file with the feature off is a non-event.)
                // Pinned by BrokenClassificationConfig_IsReported_EvenWhenClassificationOff.
                var classifier = Content.ContentClassifier.Load(classificationFile);
                // Null-is-empty (docs/CONFIG_NULL_SEMANTICS.md) means
                // `"categories": null` now LOADS, as an empty set. Empty is legal
                // but it is also a total detection gap, so — exactly as
                // schema.json's empty objectList is judged after normalisation —
                // say so rather than let it pass silently.
                //
                // UNCONDITIONAL, exactly like the load above and for the same
                // reason: this used to be guarded by `classificationEnabled &&`,
                // which made the --strict verdict depend on the flag — green with
                // it off, red with it on — one line below the comment explaining
                // why that asymmetry is a time bomb. A file that EXISTS is judged
                // as it is, whatever the flag currently says.
                // Pinned by EmptyCategorySet_IsReported_EvenWhenClassificationOff.
                if (classifier.Categories.Count == 0)
                {
                    result.Warnings.Add(
                        $"{classificationFile} yields NO usable categories (categories is null, "
                        + "empty, or every category has no valid pattern). Under CLASSIFICATION=true "
                        + "nothing will ever be detected and no item will be labelled Restricted — "
                        + "classification on in name only. Reported whether or not the flag is "
                        + "currently set: the flag is what operators flip last, and this file is "
                        + "validated as it exists.");
                }
            }
            catch (Exception exc)
            {
                result.Errors.Add($"classification.json invalid: {exc.Message}");
            }
        }

        // Fail-closed scale guard preflight: unfiltered objects are warnings,
        // errors under --strict (unless ALLOW_FULL_SCAN=true).
        if (schema is not null && !EnvFlags.IsTrue("ALLOW_FULL_SCAN"))
        {
            foreach (var obj in schema.ObjectList)
            {
                var filter = filters.For(obj.ObjectName);
                if (filter is { IsEffectivelyFiltered: true } || filters.IsFullScanAllowed(obj.ObjectName))
                    continue;
                // Distinguish "no filter at all" from "a filter that prunes
                // nothing" (only a non-dt partition predicate on a key that
                // MatchesPartition never prunes on) — both trip the guard.
                var hasInertFilter = filter is { HasAnyFilter: true };
                var message = hasInertFilter
                    ? $"Object '{obj.ObjectName}' is only 'filtered' by a non-pruning partition "
                      + "predicate (no record predicate and no 'dt' partition predicate) — it does "
                      + "NOT prune and the crawl will refuse it (fail-closed scale guard). Add a "
                      + "record predicate or a 'dt' partition predicate, or an explicit exemption."
                    : $"Object '{obj.ObjectName}' has NO filter in filters.json and is not in "
                      + "fullScanAllowed — the crawl will refuse it (fail-closed scale guard). "
                      + "Add partition/record filters or an explicit exemption.";
                (strict ? result.Errors : result.Warnings).Add(message);
            }
        }

        // Coarse-ACL posture (WP-HD-2) — INTERIM control pending Ranger.
        //
        // BDH mirrors the source DATA but not its authorisation model, so
        // aclMode 'public' (everyoneExceptGuests) and 'group' (ONE flat Entra
        // group for every row of the object) are the only options above
        // ownerOnly — and neither can express the row/column restrictions the
        // source enforces. On a mart mirroring 150M+ Salesforce rows that is a
        // material over-sharing exposure, so it is surfaced on EVERY run and
        // must be explicitly attested in schema.json before --strict passes.
        //
        // Deliberately independent of ALLOW_FULL_SCAN and fullScanAllowed: those
        // exempt an object from the SCALE guard, which if anything means MORE
        // rows are exposed at the coarse grant. Effective-filtering is therefore
        // read straight off the filter (reusing the guard's own
        // IsEffectivelyFiltered predicate, which already discounts a dt
        // isNotNull-only filter and non-dt partition predicates).
        if (schema is not null)
        {
            foreach (var obj in schema.ObjectList.Where(o => o.IsCoarseAcl))
            {
                var effectivelyFiltered = filters.For(obj.ObjectName) is { IsEffectivelyFiltered: true };
                var message = CoarseAclFinding(obj, effectivelyFiltered);
                if (obj.HasBoundCoarseAclAttestation)
                {
                    result.AcknowledgedWarnings.Add(
                        message + " ACKNOWLEDGED in schema.json (coarseAclAcknowledged=true, "
                        + $"coarseAclAcknowledgedFor=\"{obj.CoarseAclPostureToken}\"): this exact "
                        + "posture has been reviewed and accepted, not removed or reduced. The "
                        + "sign-off is void the moment aclMode or aclGroupId changes.");
                }
                else if (obj.CoarseAclAcknowledged)
                {
                    // An UNBOUND 'true' — the state that let a group→public
                    // widening (or a broader aclGroupId) inherit an old sign-off.
                    // It records that someone signed, not what they signed, so it
                    // is not treated as an attestation at all.
                    (strict ? result.Errors : result.Warnings).Add(
                        message + " An UNBOUND sign-off is recorded (\"coarseAclAcknowledged\": true "
                        + "with no \"coarseAclAcknowledgedFor\"), which does not say WHICH posture "
                        + "was reviewed and would therefore carry over unchanged if this object's "
                        + "aclMode or aclGroupId were later widened. It is not accepted. Re-review "
                        + "the object and record the posture verbatim: "
                        + $"\"coarseAclAcknowledgedFor\": \"{obj.CoarseAclPostureToken}\" "
                        + "(required by --strict; the warning is reported either way).");
                }
                else
                {
                    (strict ? result.Errors : result.Warnings).Add(
                        message + " No sign-off recorded: set \"coarseAclAcknowledged\": true and "
                        + $"\"coarseAclAcknowledgedFor\": \"{obj.CoarseAclPostureToken}\" on this "
                        + "object in schema.json to attest that THIS posture has been reviewed and "
                        + "accepted (required by --strict; the warning is reported either way). "
                        + "Binding the sign-off to the posture is what makes a later widening "
                        + "require a fresh review instead of inheriting this one.");
                }
            }
        }

        // Per-column policy posture (WP-HD-3) — informational, never gating.
        //
        // Sensitivity in this connector is per-OBJECT, so without columnPolicies
        // a restricted column rides along with the record for everyone the
        // (coarse) ACL admits. Which columns are dropped/masked is therefore
        // posture an operator must be able to READ OFF preflight rather than
        // reconstruct from schema.json — including the all-important zero case,
        // which is the state this connector shipped in.
        if (schema is not null)
        {
            foreach (var obj in schema.ObjectList.Where(o => o.HasColumnPolicies))
            {
                result.Notices.Add(
                    $"Object '{obj.ObjectName}': per-column policies active — "
                    + $"{obj.DroppedColumnCount} dropped, {obj.MaskedColumnCount} masked. "
                    + $"dropped=[{string.Join(", ", obj.DroppedColumns)}] "
                    + $"masked=[{string.Join(", ", obj.MaskedColumns)}]. Dropped columns are not "
                    + "emitted at all; masked columns keep their property name and content key "
                    + $"with the value replaced by '{ColumnPolicy.MaskMarker}'. Neither action "
                    + "changes the item ACL.");
            }
            // The mask marker is a STRING. Masking a column mapped to a typed
            // Graph property (Int64/Double/DateTime/Boolean) pushes a string
            // into that property and Graph rejects the whole item — so the
            // "restriction" would quietly stop the record being indexed at all,
            // discovered mid-crawl. Drop is unaffected (nothing is emitted).
            foreach (var obj in schema.ObjectList.Where(o => o.HasColumnPolicies))
            {
                foreach (var column in obj.MaskedColumns)
                {
                    var property = obj.SelectedFields
                        .FirstOrDefault(kv => string.Equals(
                            kv.Key, column, StringComparison.OrdinalIgnoreCase)).Value;
                    if (string.IsNullOrEmpty(property)
                        || property.StartsWith("_bdh_", StringComparison.Ordinal))
                    {
                        continue;   // content-routed: no typed Graph property involved
                    }
                    if (graphPropertyTypes.TryGetValue(property, out var type)
                        && !type.Equals("String", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Warnings.Add(
                            $"Object '{obj.ObjectName}' masks column '{column}', but its Graph "
                            + $"property '{property}' is declared '{type}' in graph-schema.json. "
                            + $"The mask marker '{ColumnPolicy.MaskMarker}' is a string, so Graph "
                            + "will reject every item for this object and the records will not be "
                            + $"indexed at all. Use \"{ColumnPolicy.Drop}\" for this column, or "
                            + "redeclare the property as String.");
                    }
                }
            }

            if (!schema.ObjectList.Any(o => o.HasColumnPolicies))
            {
                result.Notices.Add(
                    "No per-column policies are configured on any object (columnPolicies is empty "
                    + "everywhere). Every selected column of every crawled record is indexed in "
                    + "full for whoever that item's ACL admits — this connector has no other "
                    + "column-level restriction, and its sensitivity labels are per-OBJECT only. "
                    + "If any object exposes compensation, margin or personal identifiers, add a "
                    + "\"columnPolicies\" entry (drop | mask) for those columns in schema.json.");
            }
        }

        // Cross-flag consistency.
        if (EnvFlags.HaMode && !EnvFlags.UseSqlServer)
            result.Errors.Add("HA_MODE=true requires USE_SQL_SERVER=true and SQL_CONNECTION_STRING.");

        if (EnvFlags.IsTrue("USE_KEY_VAULT")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEY_VAULT_URI")))
        {
            result.Errors.Add("USE_KEY_VAULT=true requires KEY_VAULT_URI.");
        }

        // Dead-letter payload mode: default is the safe 'redacted'; 'full' is an
        // explicit opt-in. An unrecognized value fails fast at config load, so
        // surface it here as a preflight ERROR too.
        var deadLetterMode = Environment.GetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar);
        if (!string.IsNullOrWhiteSpace(deadLetterMode)
            && !deadLetterMode.Trim().Equals("full", StringComparison.OrdinalIgnoreCase)
            && !deadLetterMode.Trim().Equals("redacted", StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add(
                $"{DeadLetterRedaction.ModeEnvVar} '{deadLetterMode}' is invalid (full | redacted).");
        }

        // Classification enforcement (advisory by default). SensitivityLabel is a
        // connector-applied tag, NOT a Purview-enforced label; ACL enforcement is
        // opt-in and needs a target group.
        if (EnvFlags.IsTrue("CLASSIFICATION_ENFORCE_ACL"))
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("CLASSIFICATION_RESTRICTED_GROUP_ID")))
            {
                result.Errors.Add(
                    "CLASSIFICATION_ENFORCE_ACL=true requires CLASSIFICATION_RESTRICTED_GROUP_ID "
                    + "(the Entra group Restricted items are limited to).");
            }
            if (!EnvFlags.IsTrue("CLASSIFICATION"))
            {
                result.Warnings.Add(
                    "CLASSIFICATION_ENFORCE_ACL=true has no effect without CLASSIFICATION=true "
                    + "(nothing is classified, so no item is ever narrowed).");
            }
        }

        var logFormat = Environment.GetEnvironmentVariable("LOG_FORMAT");
        if (!string.IsNullOrEmpty(logFormat)
            && !logFormat.Equals("json", StringComparison.OrdinalIgnoreCase)
            && !logFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add($"LOG_FORMAT '{logFormat}' is not 'text' or 'json'; treating as text.");
        }

        var rowCap = EnvFlags.GetInt("BDH_MAX_RECORDS_PER_OBJECT", 500_000);
        if (rowCap == 0)
            result.Warnings.Add(
                "BDH_MAX_RECORDS_PER_OBJECT=0 disables the row-cap safety valve entirely.");

        var lagHours = EnvFlags.GetInt("BDH_LAG_HOURS", 24);
        if (lagHours < 24)
            result.Warnings.Add(
                $"BDH_LAG_HOURS={lagHours} is below the nightly sync lag (24h); incremental "
                + "crawls may miss late-arriving partitions.");

        return result;
    }

    /// <summary>
    /// The produced-vs-declared diff itself, callable only once BOTH files have
    /// parsed clean (the caller checks that by error count, not by assumption).
    /// <list type="bullet">
    ///   <item>produced but undeclared → ERROR. Graph rejects an item carrying
    ///   an undeclared property, so every affected record fails at push time —
    ///   the config cannot work.</item>
    ///   <item>declared but never produced → NOTICE, never gating. Dead schema
    ///   is harmless (pre-declaring ahead of a config rollout is exactly the
    ///   over-declaring this check encourages), but it is the usual trace of a
    ///   one-sided rename, so it must be visible.</item>
    /// </list>
    /// <para>
    /// A failure to COMPUTE the produced set is an ERROR, not silence — the
    /// Clarizen connector wrapped its drift computation in a blanket
    /// <c>catch {}</c> and a degenerate file passed --strict green while the
    /// crawl threw. <paramref name="producedCollector"/> exists so a test can
    /// force that path (DriftComputationFailure_IsAnError_NeverSilence);
    /// production always uses <see cref="ProducedGraphProperties.Collect"/>.
    /// </para>
    /// </summary>
    internal static void AddGraphSchemaDriftFindings(
        Result result,
        SchemaConfig schema,
        IReadOnlyDictionary<string, string> declaredTypes,
        Func<SchemaConfig, IReadOnlyCollection<string>>? producedCollector = null)
    {
        try
        {
            var produced = (producedCollector ?? ProducedGraphProperties.Collect)(schema);
            var undeclared = produced
                .Where(name => !declaredTypes.ContainsKey(name))
                .ToList();
            if (undeclared.Count > 0)
            {
                result.Errors.Add(
                    "graph-schema.json does not declare Graph property name(s) this "
                    + $"configuration emits on external items: {string.Join(", ", undeclared)}. "
                    + "Graph rejects an item carrying an undeclared property, so every affected "
                    + "record would fail at push time — add the declaration(s) to "
                    + "config/graph-schema.json. Note that the always-emitted properties "
                    + $"({string.Join(", ", AlwaysEmittedProperties.Names)}) are required "
                    + "whether or not CLASSIFICATION is currently enabled: the flag is what "
                    + "operators flip last, and declaring a property costs nothing while "
                    + "discovering it missing on flag-flip day costs an incident.");
            }

            var unproduced = declaredTypes.Keys
                .Where(name => !produced.Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unproduced.Count > 0)
            {
                result.Notices.Add(
                    "graph-schema.json declares property name(s) nothing in schema.json "
                    + $"produces: {string.Join(", ", unproduced)}. Harmless dead schema — but "
                    + "if one of these was recently renamed, check the rename reached both "
                    + "files.");
            }
        }
        catch (Exception exc)
        {
            result.Errors.Add(
                "Could not determine which Graph properties this configuration emits, so the "
                + $"graph-schema.json cross-check could not run: {exc.Message}");
        }
    }

    /// <summary>
    /// Describe one object's coarse-ACL exposure. The coarse+UNFILTERED case is
    /// the worst case — a flat grant over the object's ENTIRE row set — and is
    /// stated far more strongly than coarse+filtered, where filters.json at
    /// least bounds how many rows reach that grant.
    /// </summary>
    private static string CoarseAclFinding(ObjectConfig obj, bool effectivelyFiltered)
    {
        var grant = string.Equals(obj.AclMode, "public", StringComparison.OrdinalIgnoreCase)
            ? "EVERY ingested record is granted to everyoneExceptGuests — every non-guest user "
              + "in the tenant"
            : $"EVERY ingested record is granted to the single flat Entra group '{obj.AclGroupId}', "
              + "regardless of who could see that row in the source";

        var head =
            $"Object '{obj.ObjectName}' uses coarse aclMode '{obj.AclMode}': {grant}. BDH mirrors "
            + "the source data but not its authorisation model, so this grant CANNOT express the "
            + "row/column restrictions the source enforces — anything indexed is over-shared "
            + "relative to the system of record.";

        return effectivelyFiltered
            ? head + " This object is effectively filtered, so filters.json bounds WHICH rows "
                   + "reach that grant — but every row that is ingested is exposed at it."
            : head + " WORST CASE: this object is ALSO NOT effectively filtered, so there is no "
                   + "bound on either side — the ENTIRE object (150M+ rows at BDH scale) is "
                   + "indexed and exposed at that coarse grant. Narrow the aclMode, or add a "
                   + "pruning filter in filters.json, before this reaches a production tenant.";
    }

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var strict = args.HasFlag("--strict");
        EnvLoader.LoadLayered();
        Logging.Initialize("validate_config", args.Verbose);
        Dashboard.Banner("Validate config" + (strict ? " (strict)" : string.Empty));

        var result = ValidateCore(strict: strict);

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
                + $"{cb.HalfOpenTrials} half-open trial(s). Guarded: hdfs, graph.");
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
                Environment.GetEnvironmentVariable("CONNECTOR_NAME") ?? "BDH Hadoop Data Mart");
            Dashboard.Line(
                $"OpenTelemetry tracing: exporting to '{otelEndpoint}' as service '{serviceName}'.");
        }

        // Best-effort connectivity — required to pass only under --strict.
        if (result.Errors.Count == 0)
        {
            try
            {
                var config = AppConfig.Load();
                using var source = Runtime.CreateSource(config, CircuitBreaker.Disabled);
                bool sourceOk;
                string? sourceFailReason = null;
                try
                {
                    sourceOk = await source.ExistsAsync(string.Empty, ServiceStop.Token);
                    if (!sourceOk)
                        sourceFailReason = "the BDH root path does not exist";
                }
                catch (Exception exc)
                {
                    // Preflight is exactly where the failure REASON matters —
                    // "connection refused" vs "404" vs "path escapes root" each
                    // point at a different setting. Never discard it.
                    sourceOk = false;
                    sourceFailReason = $"{exc.GetType().Name}: {exc.Message}";
                }
                if (!sourceOk)
                    (strict ? result.Errors : result.Warnings).Add(
                        $"BDH source connectivity check failed ({source.Description}): {sourceFailReason}");
                else
                    Dashboard.Line($"BDH source connectivity: OK ({source.Description})");

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

        // Posture first, so it frames the findings that follow.
        foreach (var notice in result.Notices)
            Dashboard.Line($"POSTURE: {notice}");
        foreach (var warning in result.Warnings)
            Dashboard.Line($"WARNING: {warning}");
        // Attested findings are printed with the SAME severity word — an accepted
        // over-sharing exposure is still an exposure and must stay on screen.
        foreach (var acknowledged in result.AcknowledgedWarnings)
            Dashboard.Line($"WARNING (acknowledged): {acknowledged}");
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"ERROR: {error}");

        var ok = result.Ok(strict);
        var acknowledgedNote = result.AcknowledgedWarnings.Count == 0
            ? string.Empty
            : $" {result.AcknowledgedWarnings.Count} acknowledged warning(s) not gating.";
        Dashboard.Line((ok
            ? "Configuration looks good."
            : $"Configuration is NOT valid ({result.Errors.Count} error(s), {result.Warnings.Count} warning(s)).")
            + acknowledgedNote);
        return ok;
    }
}
