// Graph/StampedPropertyInventory.cs
// ---------------------------------
// "Which Graph property names does this connector ACTUALLY stamp?", answered by
// EXECUTING the production stamping code rather than by reading a curated list
// of registered symbols.
//
// KNOW WHAT THIS IS AND IS NOT — READ BEFORE RELYING ON IT.
// This is a BEST-EFFORT EARLY WARNING, not the drift guarantee. It executes the
// stamper call sites listed in CollectForObject/CollectObjectIndependent below.
// That is a MAINTAINED LIST — of call sites rather than of symbol names, but a
// maintained list all the same. A stamp written anywhere this file does not
// invoke (a new stamper, or a bare `item.Properties["X"] = ...` in
// Graph/Ingest.cs) is INVISIBLE here, and both this inventory and the
// validate-config preflight built on it will report no drift for it.
//
// Demonstrated, round 9: adding `item.Properties["CrawlBatchIdProbeB"] = ...`
// to the per-record crawl loop in Graph/Ingest.cs left this inventory — and the
// build-time drift test, and `validate-config --strict` — completely green.
//
// THE GUARANTEE LIVES AT THE WRITE PATH. GraphPropertyBag rejects an undeclared
// name at the point of the stamp, from any call site, and IngestPipeline lets
// that exception abort the crawl rather than dead-lettering the record. Nothing
// has to be registered here for that to work. What this inventory adds is
// EARLINESS: when it does see a stamp, an operator learns about the drift at
// `validate-config` time instead of at crawl time.
//
// A consequence of the incompleteness, stated plainly: because the diff runs in
// both directions, a property this file fails to observe is also reported as
// "declared but nothing stamps it" — a spurious WARNING, not a missed error.

using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.ContentGate;
using ClarizenConnector.Content;
using ClarizenConnector.Item;

namespace ClarizenConnector.Graph;

public static class StampedPropertyInventory
{
    /// <summary>
    /// Graph property names the connector is OBSERVED to stamp on an external
    /// item, collected by running the stamper call sites this class knows about
    /// over a synthetic record for each object in <paramref name="schema"/>.
    /// Incomplete by construction — see the file header. Enforcement is
    /// <see cref="GraphPropertyBag"/>, not this.
    /// <para>
    /// Collected with the registry temporarily widened to accept anything, so
    /// the inventory reports what the code DOES rather than what it is currently
    /// permitted to do — otherwise it could never observe the drift it exists to
    /// report.
    /// </para>
    /// </summary>
    /// <summary>Configuration knobs that change WHICH properties get stamped.
    /// The inventory is the UNION over all of them, never just the operator's
    /// current setting — otherwise "does the code stamp X?" would have a
    /// different answer per deployment, and a mode nobody is running today
    /// would ship undeployable.</summary>
    internal static readonly IReadOnlyList<string> FinancialModes =
        new[] { "tag", "filter", "acl" };

    public static IReadOnlyCollection<string> Collect(SchemaConfig schema, AppConfig? config = null)
    {
        var stamped = new SortedSet<string>(StringComparer.Ordinal);

        // Sweep the config space rather than sampling it. `filter` mode REMOVES
        // the financial properties the converter just stamped, so a single-mode
        // pass would under-report every financial property and the drift check
        // would then wrongly call them dead schema.
        var configs = config is not null
            ? new[] { config }
            : FinancialModes.Select(SweepConfig).ToArray();

        foreach (var appConfig in configs)
        {
            foreach (var objectConfig in schema.ObjectList)
            {
                // iconUrl is stamped only when the object declares one, so both
                // shapes are exercised for every object.
                foreach (var variant in ObjectVariants(objectConfig))
                {
                    foreach (var name in CollectForObject(variant, appConfig))
                        stamped.Add(name);
                }
            }
        }

        // Stampers that do not depend on the object config at all still publish
        // properties, and must be enumerated even for an empty object list.
        foreach (var name in CollectObjectIndependent())
            stamped.Add(name);

        return stamped;
    }

    /// <summary>A minimal config carrying only the knobs that steer stamping.
    /// Deliberately NOT read from the environment: the inventory must answer the
    /// same question on every host.</summary>
    private static AppConfig SweepConfig(string financialMode) => new()
    {
        ConnectorId = "SchemaDriftInventory",
        ConnectorName = "Schema drift inventory",
        ConnectorDescription = "Synthetic configuration; never connects to anything.",
        ClarizenBaseUrl = "https://inventory.invalid",
        ClarizenUsername = "inventory@invalid",
        ClarizenPassword = string.Empty,
        AadTenantId = "00000000-0000-0000-0000-000000000000",
        AadClientId = "00000000-0000-0000-0000-000000000000",
        FinancialDataMode = financialMode,
        FinancialDataGroupId = "00000000-0000-0000-0000-000000000000",
        GraphItemTtlDays = 1,
    };

    private static IEnumerable<ObjectConfig> ObjectVariants(ObjectConfig objectConfig)
    {
        yield return objectConfig;
        if (string.IsNullOrEmpty(objectConfig.IconUrl))
        {
            yield return new ObjectConfig
            {
                ObjectName = objectConfig.ObjectName,
                DisplayName = objectConfig.DisplayName,
                SelectedFields = objectConfig.SelectedFields,
                FinancialFields = objectConfig.FinancialFields,
                AclMode = objectConfig.AclMode,
                ProjectField = objectConfig.ProjectField,
                IconUrl = "https://example.invalid/icon.png",
                SensitivityDefault = objectConfig.SensitivityDefault,
            };
        }
    }

    private static IEnumerable<string> CollectForObject(ObjectConfig objectConfig, AppConfig config)
    {
        var record = SyntheticRecord(objectConfig);
        var converter = new ItemConverter(config);

        // The converter itself — this is the call site where a bare-literal
        // stamp lives, and running it is what makes such a stamp visible.
        var item = Capture(() => converter.Convert(record, objectConfig, new List<AclEntry>()));

        // The financial classifier, forced down its stamping branch. Note it is
        // invoked on a SEPARATE item so a `filter`-mode removal in the converter
        // pass cannot hide a name this pass would otherwise report.
        var financial = Capture(() =>
        {
            var target = new ExternalItem { Id = record.ItemId };
            FinancialFieldClassifier.Apply(target, objectConfig, config, contentFinancial: true);
            return target;
        });

        // The sensitivity classifier.
        var sensitivity = Capture(() =>
        {
            var target = new ExternalItem { Id = record.ItemId };
            new SensitivityClassifier(ContentClassifier.Load()).Classify(target, objectConfig);
            return target;
        });

        return item.Concat(financial).Concat(sensitivity);
    }

    private static IEnumerable<string> CollectObjectIndependent()
    {
        // The content gate's verdict stamp, every outcome.
        var gate = Capture(() =>
        {
            var target = new ExternalItem { Id = "synthetic" };
            foreach (var verdict in new[]
                     {
                         GateVerdict.Pass,
                         GateVerdict.Block(GateCategories.Malware, "synthetic"),
                         GateVerdict.Incomplete(GateCategories.MalwareUnscannable, "synthetic"),
                     })
            {
                ContentGateStage.Stamp(target, verdict, enabled: true);
            }
            return target;
        });

        // The attachment enricher's status stamp.
        var attachment = Capture(() =>
        {
            var target = new ExternalItem { Id = "synthetic" };
            AttachmentEnricher.StampStatus(target, "extracted");
            return target;
        });

        return gate.Concat(attachment);
    }

    /// <summary>Run a stamping action with the registry widened, and return the
    /// property names that landed on the item.</summary>
    private static IReadOnlyCollection<string> Capture(Func<ExternalItem> stamp)
    {
        using var _ = GraphPropertyRegistry.SuspendEnforcement();
        return stamp().Properties.Names.ToList();
    }

    /// <summary>A record populated for every selected field, so no field-driven
    /// stamp is skipped for want of a value.</summary>
    private static ClarizenRecord SyntheticRecord(ObjectConfig objectConfig)
    {
        var json = new JsonObject
        {
            ["id"] = $"/{objectConfig.ObjectName}/1",
            ["Name"] = "synthetic",
        };
        foreach (var (field, _) in objectConfig.SelectedFields)
        {
            if (!json.ContainsKey(field))
                json[field] = "synthetic";
        }
        return new ClarizenRecord(objectConfig.ObjectName, json);
    }
}
