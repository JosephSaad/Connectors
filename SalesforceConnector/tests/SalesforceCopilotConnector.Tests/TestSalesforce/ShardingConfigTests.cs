// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

/// <summary>
/// Tests for <see cref="ShardingConfig"/> (GRAPH_CONNECTION_SHARDS, improvements-contract #2).
/// Joins the "EnvVars" collection because it mutates the process-global environment; every
/// test saves and restores GRAPH_CONNECTION_SHARDS.
/// </summary>
[Collection("EnvVars")]
public class ShardingConfigTests : IDisposable
{
    private readonly string? _savedShards;

    public ShardingConfigTests()
    {
        _savedShards = Environment.GetEnvironmentVariable(ShardingConfig.EnvVar);
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, _savedShards);
    }

    /// <summary>
    /// Build a minimal AppConfig with a known, small object universe so the "every object
    /// assigned exactly once" rule is easy to exercise. Mirrors how SettingsTests builds a
    /// schema and how TestFixtures derives ObjectNames from it.
    /// </summary>
    private static AppConfig ConfigWithObjects(params string[] objectNames)
    {
        var objectList = new JsonArray();
        foreach (var name in objectNames)
            objectList.Add(new JsonObject { ["objectName"] = name });
        var schema = new JsonObject { ["objectList"] = objectList };

        return new AppConfig
        {
            ClientId = "00000000-0000-0000-0000-000000000000",
            TenantId = TestFixtures.TenantId,
            RepoRoot = Settings.RepoRoot,
            SchemaConfig = schema,
            ObjectNames = Settings.BuildObjectNameList(schema),
            Connector = new ConnectorSettings
            {
                Id = "BaseConnector",
                Name = "Base Connector",
                Description = "Base connector for sharding tests.",
                Schema = new JsonArray(),
                Template = new JsonObject { ["id"] = "display" },
                Salesforce = new SalesforceSettings
                {
                    InstanceUrl = TestFixtures.InstanceUrl,
                    ApiVersion = TestFixtures.ApiVersion,
                    ClientId = "mock-salesforce-client-id",
                    ClientSecret = "mock-salesforce-client-secret",
                },
            },
        };
    }

    // ── IsEnabled / disabled ──────────────────────────────────────────────────

    [Fact]
    public void IsEnabledFalseWhenUnset()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
        Assert.False(ShardingConfig.IsEnabled);
    }

    [Fact]
    public void IsEnabledFalseWhenWhitespace()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, "   ");
        Assert.False(ShardingConfig.IsEnabled);
    }

    [Fact]
    public void IsEnabledTrueWhenSet()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, "{\"connA\":[\"Account\"]}");
        Assert.True(ShardingConfig.IsEnabled);
    }

    [Fact]
    public void TryLoadDisabledWhenEnvUnset()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
        var config = ConfigWithObjects("Account", "Contact");

        var ok = ShardingConfig.TryLoad(config, out var shards, out var error);

        Assert.False(ok);
        Assert.Null(error);            // disabled is not an error
        Assert.Empty(shards);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void TryLoadHappyPathTwoShardsPartitionSchema()
    {
        var config = ConfigWithObjects("Account", "Contact", "Case", "Lead");
        Environment.SetEnvironmentVariable(
            ShardingConfig.EnvVar,
            "{\"salesforceCrmA\":[\"Account\",\"Contact\"],\"salesforceCrmB\":[\"Case\",\"Lead\"]}");

        var ok = ShardingConfig.TryLoad(config, out var shards, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(2, shards.Count);

        var a = shards.Single(s => s.ConnectionId == "salesforceCrmA");
        var b = shards.Single(s => s.ConnectionId == "salesforceCrmB");
        Assert.Equal(new[] { "Account", "Contact" }, a.ObjectTypes);
        Assert.Equal(new[] { "Case", "Lead" }, b.ObjectTypes);
    }

    // ── Error: exactly-duplicated JSON key ────────────────────────────────────

    [Fact]
    public void TryLoadDuplicateJsonKeyReportsErrorInsteadOfThrowing()
    {
        // JsonObject parses a duplicated key lazily and throws ArgumentException on
        // first access — TryLoad must convert that into a validation error, never
        // crash the command ("never throws for user-input problems").
        var config = ConfigWithObjects("Account", "Contact");
        Environment.SetEnvironmentVariable(
            ShardingConfig.EnvVar,
            "{\"salesforceCrmA\":[\"Account\"],\"salesforceCrmA\":[\"Contact\"]}");

        var ok = ShardingConfig.TryLoad(config, out var shards, out var error);

        Assert.False(ok);
        Assert.Empty(shards);
        Assert.NotNull(error);
        Assert.Contains("duplicate connection id", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Error: unknown object ─────────────────────────────────────────────────

    [Fact]
    public void TryLoadUnknownObjectTypeFails()
    {
        var config = ConfigWithObjects("Account", "Contact");
        Environment.SetEnvironmentVariable(
            ShardingConfig.EnvVar,
            "{\"connA\":[\"Account\",\"Contact\",\"Widget\"]}");

        var ok = ShardingConfig.TryLoad(config, out var shards, out var error);

        Assert.False(ok);
        Assert.Empty(shards);
        Assert.NotNull(error);
        Assert.Contains("Widget", error);
        Assert.Contains("unknown object type", error);
    }

    // ── Error: duplicate assignment ───────────────────────────────────────────

    [Fact]
    public void TryLoadDuplicateAssignmentFails()
    {
        var config = ConfigWithObjects("Account", "Contact");
        // Account claimed by both shards.
        Environment.SetEnvironmentVariable(
            ShardingConfig.EnvVar,
            "{\"connA\":[\"Account\"],\"connB\":[\"Account\",\"Contact\"]}");

        var ok = ShardingConfig.TryLoad(config, out var shards, out var error);

        Assert.False(ok);
        Assert.Empty(shards);
        Assert.NotNull(error);
        Assert.Contains("Account", error);
        Assert.Contains("multiple shards", error);
    }

    // ── Error: unassigned object ──────────────────────────────────────────────

    [Fact]
    public void TryLoadUnassignedObjectFails()
    {
        var config = ConfigWithObjects("Account", "Contact", "Case");
        // Case is left unassigned.
        Environment.SetEnvironmentVariable(
            ShardingConfig.EnvVar,
            "{\"connA\":[\"Account\"],\"connB\":[\"Contact\"]}");

        var ok = ShardingConfig.TryLoad(config, out var shards, out var error);

        Assert.False(ok);
        Assert.Empty(shards);
        Assert.NotNull(error);
        Assert.Contains("Case", error);
        Assert.Contains("not assigned to any shard", error);
    }

    // ── Error: bad connection id ──────────────────────────────────────────────

    [Fact]
    public void TryLoadBadConnectionIdFails()
    {
        var config = ConfigWithObjects("Account");
        // "ab" is too short (min 3) per Settings.ValidateConnectorId.
        Environment.SetEnvironmentVariable(
            ShardingConfig.EnvVar,
            "{\"ab\":[\"Account\"]}");

        var ok = ShardingConfig.TryLoad(config, out var shards, out var error);

        Assert.False(ok);
        Assert.Empty(shards);
        Assert.NotNull(error);
        Assert.Contains("Invalid connection id", error);
        Assert.Contains("ab", error);
    }

    [Fact]
    public void TryLoadReservedPrefixConnectionIdFails()
    {
        var config = ConfigWithObjects("Account");
        // "SharePointShard" starts with a disallowed prefix.
        Environment.SetEnvironmentVariable(
            ShardingConfig.EnvVar,
            "{\"SharePointShard\":[\"Account\"]}");

        var ok = ShardingConfig.TryLoad(config, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Invalid connection id", error);
    }

    // ── Error: malformed JSON ─────────────────────────────────────────────────

    [Fact]
    public void TryLoadNonObjectJsonFails()
    {
        var config = ConfigWithObjects("Account");
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, "[\"Account\"]");

        var ok = ShardingConfig.TryLoad(config, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("must be a JSON object", error!);
    }

    [Fact]
    public void TryLoadInvalidJsonFails()
    {
        var config = ConfigWithObjects("Account");
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, "{not json");

        var ok = ShardingConfig.TryLoad(config, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("not valid JSON", error!);
    }

    [Fact]
    public void TryLoadShardWithNoObjectTypesFails()
    {
        var config = ConfigWithObjects("Account");
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, "{\"connA\":[]}");

        var ok = ShardingConfig.TryLoad(config, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("lists no object types", error!);
    }

    // ── ForShard / ForShardObject ─────────────────────────────────────────────

    [Fact]
    public void ForShardSetsConnectionIdAndRestrictsSingleObject()
    {
        var baseConfig = ConfigWithObjects("Account", "Contact");
        var shard = new Shard("salesforceCrmA", new[] { "Account" });

        var shardConfig = ShardingConfig.ForShard(baseConfig, shard);

        Assert.Equal("salesforceCrmA", shardConfig.Connector.Id);
        // Shard restriction rides on ShardObjectTypes (honored by Ingest + ApiClient),
        // leaving the single-object DebugObjectType seam free for the ingest-object command.
        Assert.Equal(new[] { "Account" }, shardConfig.ShardObjectTypes);
        Assert.Null(shardConfig.DebugObjectType);
        // Base config untouched.
        Assert.Equal("BaseConnector", baseConfig.Connector.Id);
        Assert.Null(baseConfig.ShardObjectTypes);
    }

    [Fact]
    public void ForShardMultiObjectSetsConnectionIdWithoutSingleObjectRestriction()
    {
        var baseConfig = ConfigWithObjects("Account", "Contact", "Case");
        var shard = new Shard("salesforceCrmA", new[] { "Account", "Contact" });

        var shardConfig = ShardingConfig.ForShard(baseConfig, shard);

        Assert.Equal("salesforceCrmA", shardConfig.Connector.Id);
        // A multi-object shard runs as ONE connection covering all its object types.
        Assert.Equal(new[] { "Account", "Contact" }, shardConfig.ShardObjectTypes);
        Assert.Null(shardConfig.DebugObjectType);
    }

    [Fact]
    public void ForShardObjectRestrictsToOneObjectType()
    {
        var baseConfig = ConfigWithObjects("Account", "Contact", "Case");
        var shard = new Shard("salesforceCrmA", new[] { "Account", "Contact" });

        var perObject = ShardingConfig.ForShardObject(baseConfig, shard, "Contact");

        Assert.Equal("salesforceCrmA", perObject.Connector.Id);
        Assert.Equal("Contact", perObject.DebugObjectType);
        // Carries over shared, read-only config surface.
        Assert.Same(baseConfig.Tuning, perObject.Tuning);
        Assert.Same(baseConfig.Connector.Salesforce, perObject.Connector.Salesforce);
    }

    [Fact]
    public void ForShardObjectRejectsObjectNotInShard()
    {
        var baseConfig = ConfigWithObjects("Account", "Contact", "Case");
        var shard = new Shard("salesforceCrmA", new[] { "Account", "Contact" });

        Assert.Throws<ArgumentException>(
            () => ShardingConfig.ForShardObject(baseConfig, shard, "Case"));
    }

    [Fact]
    public void ForShardPreservesBaseConnectorMetadata()
    {
        var baseConfig = ConfigWithObjects("Account");
        var shard = new Shard("salesforceCrmA", new[] { "Account" });

        var shardConfig = ShardingConfig.ForShard(baseConfig, shard);

        // Only the id changes; name/description/salesforce carry over so the shard's
        // connection is provisioned with the same metadata.
        Assert.Equal(baseConfig.Connector.Name, shardConfig.Connector.Name);
        Assert.Equal(baseConfig.Connector.Description, shardConfig.Connector.Description);
        Assert.Same(baseConfig.Connector.Salesforce, shardConfig.Connector.Salesforce);
    }

    // ── Accumulate ────────────────────────────────────────────────────────────

    [Fact]
    public void AccumulateSumsCountersAcrossShards()
    {
        var combined = new SalesforceCopilotConnector.Graph.IngestionStats();

        var a = new SalesforceCopilotConnector.Graph.IngestionStats
        {
            TotalFetched = 10, SuccessCount = 8, FailedCount = 1, DeletedCount = 1,
        };
        a.ObjectTypeCounts["Account"] = 10;
        a.FailedIds.Add("a1");

        var b = new SalesforceCopilotConnector.Graph.IngestionStats
        {
            TotalFetched = 5, SuccessCount = 5,
        };
        b.ObjectTypeCounts["Case"] = 5;
        b.AclFallbackUsed = true;

        ShardingConfig.Accumulate(combined, a);
        ShardingConfig.Accumulate(combined, b);

        Assert.Equal(15, combined.TotalFetched);
        Assert.Equal(13, combined.SuccessCount);
        Assert.Equal(1, combined.FailedCount);
        Assert.Equal(1, combined.DeletedCount);
        Assert.Equal(10, combined.ObjectTypeCounts["Account"]);
        Assert.Equal(5, combined.ObjectTypeCounts["Case"]);
        Assert.Contains("a1", combined.FailedIds);
        Assert.True(combined.AclFallbackUsed);
    }
}
