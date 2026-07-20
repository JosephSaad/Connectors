// Item/ProducedGraphProperties.cs
// -------------------------------
// "Which Graph property names does this configuration PUT ON external items?",
// answered for the graph-schema.json cross-check in validate-config.
//
// The answer is NOT a restated copy of the emission rules. Two sources, both
// production symbols:
//
//   1. The selectedFields-mapped names are collected by EXECUTING
//      ItemConverter.Convert — the production converter itself — over a
//      synthetic, fully populated record for each object. Drop/mask/_bdh_
//      routing therefore comes from the code that does it at crawl time
//      (PolicyFor + RouteFor inside Convert): a DROPPED column emits nothing
//      and is not reported produced; a MASKED column still emits its property
//      (carrying the mask marker) and is; a _bdh_ placeholder routes to the
//      content body and never becomes a Graph property. A duplicated rule set
//      here would drift — that duplication is exactly how the original
//      "five standard properties" defect happened (the validator read
//      ItemConverter's list while SensitivityClassifier emitted two more).
//
//   2. The always-emitted set is AlwaysEmittedProperties.Names — the existing
//      registry the collision checks already read, pinned to the two real
//      emitters by ConfigPreflightClosureTests. It is included UNCONDITIONALLY:
//      SensitivityLabel and DetectedCategories are required in graph-schema.json
//      whether or not CLASSIFICATION is currently true, because the flag is what
//      operators flip last — declaring two extra properties costs an operator
//      nothing; discovering them missing on flag-flip day costs an incident.
//      (Convert's own standard five are a subset of Names, so executing Convert
//      and unioning Names double-covers them harmlessly.)
//
// Inherited from the Clarizen connector's failure history, deliberately:
// its first two guards were curated lists (names, then call sites) and both had
// holes; what held was deriving from production symbols. And no blanket
// catch(Exception){return;} here — the CALLER (ValidateConfig) turns a
// collection failure into a preflight ERROR, never into silence.

using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using System.Text.Json.Nodes;

namespace HadoopConnector.Item;

public static class ProducedGraphProperties
{
    /// <summary>
    /// Every Graph property name items built from <paramref name="schema"/> can
    /// carry: the always-emitted registry plus, per object, whatever the
    /// production converter emits for a fully populated record. Case-insensitive
    /// (Graph property names are), sorted for stable messages. Pure — no I/O,
    /// no environment reads, so the answer is the same on every host.
    /// </summary>
    public static IReadOnlyCollection<string> Collect(SchemaConfig schema)
    {
        var produced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AlwaysEmittedProperties.Names)
            produced.Add(name);

        var converter = new ItemConverter(InventoryConfig, "https://inventory.invalid");
        foreach (var objectConfig in schema.ObjectList)
        {
            var item = converter.Convert(
                SyntheticRecord(objectConfig), objectConfig, new List<AclEntry>());
            foreach (var name in item.Properties.Keys)
                produced.Add(name);
        }
        return produced;
    }

    /// <summary>A record with EVERY selected field populated (plus Id and a
    /// DataAsOf marker), so no field-driven emission is skipped for want of a
    /// value and the inventory reports what the config produces, not what one
    /// sparse record happened to carry.</summary>
    private static BdhRecord SyntheticRecord(ObjectConfig objectConfig)
    {
        var fields = new JsonObject { ["Id"] = "INVENTORY000000001" };
        foreach (var (field, _) in objectConfig.SelectedFields)
        {
            if (!fields.ContainsKey(field))
                fields[field] = "synthetic";
        }
        return new BdhRecord(
            objectConfig.ObjectName, fields,
            dataAsOf: "2026-01-01",
            sourcePath: $"{objectConfig.ObjectName}/dt=2026-01-01/part-000.jsonl");
    }

    /// <summary>Minimal synthetic AppConfig for the converter. Deliberately NOT
    /// read from the environment (the inventory must answer the same question on
    /// every host); the converter only reads GraphItemTtlDays from it, and the
    /// base URL is passed explicitly.</summary>
    private static AppConfig InventoryConfig { get; } = new()
    {
        ConnectorId = "SchemaDriftInventory",
        ConnectorName = "Schema drift inventory",
        ConnectorDescription = "Synthetic configuration; never connects to anything.",
        HdfsMode = "webhdfs",
        AadTenantId = "00000000-0000-0000-0000-000000000000",
        AadClientId = "00000000-0000-0000-0000-000000000000",
        AadClientSecret = string.Empty,
    };
}
