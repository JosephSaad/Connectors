using ClarizenConnector.Config;

namespace ClarizenConnector.Tests;

public class ShardingConfigTests
{
    private static SchemaConfig Schema(params string[] objects) => new()
    {
        ObjectList = objects.Select(name => new ObjectConfig
        {
            ObjectName = name,
            SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
        }).ToList(),
    };

    [Fact]
    public void Disabled_WhenUnset_NoError()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, null));
        Assert.False(ShardingConfig.IsEnabled);
        Assert.False(ShardingConfig.TryLoad(Schema("Project"), out var shards, out var error));
        Assert.Null(error);
        Assert.Empty(shards);
    }

    [Fact]
    public void ValidExactPartition_Parses()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar,
            """{"clarizenWorkA": ["Project", "Task"], "clarizenWorkB": ["Milestone"]}"""));
        Assert.True(ShardingConfig.TryLoad(
            Schema("Project", "Task", "Milestone"), out var shards, out var error));
        Assert.Null(error);
        Assert.Equal(2, shards.Count);
        Assert.Equal("clarizenWorkA", shards[0].ConnectionId);
        Assert.Equal(new[] { "Project", "Task" }, shards[0].ObjectTypes);
        Assert.Equal(new[] { "Milestone" }, shards[1].ObjectTypes);
    }

    [Fact]
    public void InvalidJson_IsError()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, "{not json"));
        Assert.False(ShardingConfig.TryLoad(Schema("Project"), out _, out var error));
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void NonObjectRoot_IsError()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, """["a", "b"]"""));
        Assert.False(ShardingConfig.TryLoad(Schema("Project"), out _, out var error));
        Assert.Contains("must be a JSON object", error);
    }

    [Fact]
    public void EmptyObject_IsError()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, "{}"));
        Assert.False(ShardingConfig.TryLoad(Schema("Project"), out _, out var error));
        Assert.Contains("no shards", error);
    }

    [Fact]
    public void UnknownObjectType_IsReported()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar,
            """{"shardA": ["Project", "Ghost"]}"""));
        Assert.False(ShardingConfig.TryLoad(Schema("Project"), out _, out var error));
        Assert.Contains("unknown object type 'Ghost'", error);
    }

    [Fact]
    public void UnassignedObjects_AreReported()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, """{"shardA": ["Project"]}"""));
        Assert.False(ShardingConfig.TryLoad(Schema("Project", "Task", "Risk"), out _, out var error));
        Assert.Contains("not assigned to any shard", error);
        Assert.Contains("Task", error);
        Assert.Contains("Risk", error);
    }

    [Fact]
    public void DuplicateAssignment_IsReported()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar,
            """{"shardA": ["Project", "Task"], "shardB": ["Task"]}"""));
        Assert.False(ShardingConfig.TryLoad(Schema("Project", "Task"), out _, out var error));
        Assert.Contains("assigned to multiple shards", error);
    }

    [Fact]
    public void InvalidConnectionId_IsReported()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar,
            """{"has spaces!": ["Project"], "MicrosoftShard": ["Task"]}"""));
        Assert.False(ShardingConfig.TryLoad(Schema("Project", "Task"), out _, out var error));
        Assert.Contains("Invalid connection id 'has spaces!'", error);
        Assert.Contains("Invalid connection id 'MicrosoftShard'", error);
    }

    [Fact]
    public void NonArrayShardValue_AndEmptyEntries_AreReported()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar,
            """{"shardA": "Project", "shardB": ["", "Task"]}"""));
        Assert.False(ShardingConfig.TryLoad(Schema("Project", "Task"), out _, out var error));
        Assert.Contains("must map to a JSON array", error);
        Assert.Contains("non-string or empty object-type entry", error);
    }

    [Fact]
    public void AllProblems_ReportedTogether()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar,
            """{"x": ["Ghost"], "shardB": []}"""));
        Assert.False(ShardingConfig.TryLoad(Schema("Project"), out _, out var error));
        // Multiple independent problems land in one multi-line report.
        Assert.Contains("Invalid connection id 'x'", error);
        Assert.Contains("unknown object type 'Ghost'", error);
        Assert.Contains("lists no object types", error);
        Assert.Contains("not assigned to any shard", error);
    }

    [Fact]
    public void EffectiveConnectionIds_ShardIdsWhenValid_BaseOtherwise()
    {
        var config = TestConfig.Make(connectorId: "BaseConn");
        var schema = Schema("Project", "Task");

        using (new EnvScope((ShardingConfig.EnvVar, null)))
        {
            Assert.Equal(new[] { "BaseConn" }, ShardingConfig.EffectiveConnectionIds(config, schema));
        }
        using (new EnvScope((ShardingConfig.EnvVar,
            """{"shardA": ["Project"], "shardB": ["Task"]}""")))
        {
            Assert.Equal(
                new[] { "shardA", "shardB" },
                ShardingConfig.EffectiveConnectionIds(config, schema));
        }
        using (new EnvScope((ShardingConfig.EnvVar, "{broken")))
        {
            Assert.Equal(new[] { "BaseConn" }, ShardingConfig.EffectiveConnectionIds(config, schema));
        }
    }

    [Fact]
    public void CloneForConnection_SwapsOnlyTheConnectorId()
    {
        var baseConfig = TestConfig.Make(connectorId: "BaseConn");
        var clone = baseConfig.CloneForConnection("shardA");
        Assert.Equal("shardA", clone.ConnectorId);
        Assert.Equal(baseConfig.ClarizenUsername, clone.ClarizenUsername);
        Assert.Equal(baseConfig.GraphBatchSize, clone.GraphBatchSize);
        Assert.Equal(baseConfig.GraphBatchWorkers, clone.GraphBatchWorkers);
        Assert.Equal(baseConfig.FinancialDataMode, clone.FinancialDataMode);
        Assert.Equal("BaseConn", baseConfig.ConnectorId);  // original untouched
    }
}
