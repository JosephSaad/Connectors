using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;

namespace ClarizenConnector.Tests;

public class DeltaCursorTests
{
    private static ObjectConfig TaskConfig(string filter = "") => new()
    {
        ObjectName = "Task",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["State"] = "State",
            ["LastUpdatedOn"] = "LastUpdatedOn",
        },
        FilterCondition = filter,
    };

    [Fact]
    public void BuildQuery_FullCrawl_NoWhereClause()
    {
        var czql = ClarizenClient.BuildQuery(TaskConfig(), sinceUtc: null);
        Assert.Equal("SELECT Name, State, LastUpdatedOn FROM Task ORDER BY LastUpdatedOn", czql);
    }

    [Fact]
    public void BuildQuery_Incremental_AddsModifiedDateCursor()
    {
        var since = new DateTime(2026, 7, 1, 14, 30, 0, DateTimeKind.Utc);
        var czql = ClarizenClient.BuildQuery(TaskConfig(), since);
        Assert.Equal(
            "SELECT Name, State, LastUpdatedOn FROM Task "
            + "WHERE LastUpdatedOn > 2026-07-01T14:30:00Z ORDER BY LastUpdatedOn",
            czql);
    }

    [Fact]
    public void BuildQuery_FilterConditionAndCursor_AreAnded()
    {
        var since = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var czql = ClarizenClient.BuildQuery(TaskConfig("State = 'Active'"), since);
        Assert.Contains("WHERE (State = 'Active') AND LastUpdatedOn > 2026-07-01T00:00:00Z", czql);
    }

    [Fact]
    public void BuildQuery_AlwaysIncludesLastUpdatedOn()
    {
        var config = new ObjectConfig
        {
            ObjectName = "Project",
            SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
        };
        var czql = ClarizenClient.BuildQuery(config, null);
        Assert.Contains("LastUpdatedOn", czql);
    }

    [Fact]
    public void BuildQuery_DeduplicatesFields()
    {
        var config = TaskConfig();
        var czql = ClarizenClient.BuildQuery(config, null, extraFields: new[] { "Name", "Owner" });
        // "Name" appears once, "Owner" appended.
        Assert.Equal("SELECT Name, State, LastUpdatedOn, Owner FROM Task ORDER BY LastUpdatedOn", czql);
    }
}
