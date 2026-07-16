// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

public class IdentitySyncTests
{
    [Fact]
    public void EntityShareParserToleratesMissingId()
    {
        var processor = new SalesforceIdentitySOQLResponseProcessor();

        var parsed = processor.Get<EntityShareBase>(new JsonObject
        {
            ["records"] = new JsonArray(
                new JsonObject
                {
                    ["UserOrGroupId"] = "005000000000001AAA",
                    ["RowCause"] = "Manual",
                    ["UserOrGroup"] = new JsonObject { ["Type"] = "User" },
                }),
        });

        Assert.Single(parsed);
        Assert.Equal("", parsed[0].Id);
        Assert.Equal("005000000000001AAA", parsed[0].UserOrGroupId);
        Assert.Equal("Manual", parsed[0].RowCause);
        Assert.NotNull(parsed[0].UserOrGroup);
        Assert.Equal("User", parsed[0].UserOrGroup!.Type);
    }

    [Fact]
    public void ShareQueryRequestsNestedShareId()
    {
        Assert.Contains(
            "(SELECT Id, UserOrGroupId, UserOrGroup.Type from Shares)",
            IdentitySyncQueries.AllSharesFromRecords);
    }
}
