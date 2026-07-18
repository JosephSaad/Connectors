// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// ClassificationTests.cs — #6 advisory classification tag + optional ACL enforcement.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

[Collection("EnvVars")]
public sealed class ClassificationTests : IDisposable
{
    private readonly string?[] _saved =
    {
        Environment.GetEnvironmentVariable(Classification.FieldEnvVar),
        Environment.GetEnvironmentVariable(Classification.RestrictedValuesEnvVar),
        Environment.GetEnvironmentVariable(Classification.EnforceAclEnvVar),
        Environment.GetEnvironmentVariable(Classification.RestrictedGroupEnvVar),
    };

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, _saved[0]);
        Environment.SetEnvironmentVariable(Classification.RestrictedValuesEnvVar, _saved[1]);
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, _saved[2]);
        Environment.SetEnvironmentVariable(Classification.RestrictedGroupEnvVar, _saved[3]);
    }

    private static void ClearAll()
    {
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, null);
        Environment.SetEnvironmentVariable(Classification.RestrictedValuesEnvVar, null);
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, null);
        Environment.SetEnvironmentVariable(Classification.RestrictedGroupEnvVar, null);
    }

    private static List<Dictionary<string, string>> SampleAcl() => new()
    {
        new() { ["accessType"] = "grant", ["type"] = "user", ["value"] = "11111111-1111-1111-1111-111111111111" },
        new() { ["accessType"] = "grant", ["type"] = "user", ["value"] = "22222222-2222-2222-2222-222222222222" },
    };

    private static JsonObject RestrictedRecord() =>
        new() { ["Id"] = "001", ["Classification__c"] = "Restricted" };

    [Fact]
    public void DisabledByDefault()
    {
        ClearAll();
        Assert.False(Classification.Enabled);
        Assert.Null(Classification.TagFor(RestrictedRecord()));
        Assert.False(Classification.IsRestricted(RestrictedRecord()));
    }

    [Fact]
    public void AdvisoryByDefault_TagReadButAclUnchanged()
    {
        // Field configured (tagging on) but enforcement OFF: the tag is surfaced,
        // yet the item ACL is returned UNCHANGED — the tag is advisory only.
        ClearAll();
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Classification__c");

        var acl = SampleAcl();
        var decision = Classification.Evaluate(RestrictedRecord(), acl);

        Assert.Equal("Restricted", decision.Tag);
        Assert.False(decision.Restricted);
        Assert.Same(acl, decision.Acl);   // untouched — same reference
    }

    [Fact]
    public void EnforcementRestrictsRestrictedItemsToTheGroup()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Classification__c");
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, "true");
        Environment.SetEnvironmentVariable(
            Classification.RestrictedGroupEnvVar, "99999999-9999-9999-9999-999999999999");

        var decision = Classification.Evaluate(RestrictedRecord(), SampleAcl());

        Assert.True(decision.Restricted);
        var ace = Assert.Single(decision.Acl!);
        Assert.Equal("grant", ace["accessType"]);
        Assert.Equal("group", ace["type"]);
        Assert.Equal("99999999-9999-9999-9999-999999999999", ace["value"]);
    }

    [Fact]
    public void EnforcementLeavesNonRestrictedItemsAlone()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Classification__c");
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, "true");
        Environment.SetEnvironmentVariable(
            Classification.RestrictedGroupEnvVar, "99999999-9999-9999-9999-999999999999");

        var acl = SampleAcl();
        var record = new JsonObject { ["Id"] = "002", ["Classification__c"] = "Internal" };
        var decision = Classification.Evaluate(record, acl);

        Assert.False(decision.Restricted);
        Assert.Same(acl, decision.Acl);
        Assert.Equal("Internal", decision.Tag);
    }

    [Fact]
    public void EnforcementInactiveWithoutAGroupEvenForRestricted()
    {
        // Enforce=true but no group configured ⇒ enforcement is inactive (advisory).
        ClearAll();
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Classification__c");
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, "true");

        var acl = SampleAcl();
        var decision = Classification.Evaluate(RestrictedRecord(), acl);

        Assert.False(decision.Restricted);
        Assert.Same(acl, decision.Acl);
    }

    [Fact]
    public void CustomRestrictedValuesAreHonoredCaseInsensitively()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(Classification.FieldEnvVar, "Sensitivity__c");
        Environment.SetEnvironmentVariable(Classification.RestrictedValuesEnvVar, "Secret, TopSecret");
        Environment.SetEnvironmentVariable(Classification.EnforceAclEnvVar, "true");
        Environment.SetEnvironmentVariable(
            Classification.RestrictedGroupEnvVar, "abcdefab-1234-1234-1234-abcdefabcdef");

        var record = new JsonObject { ["Id"] = "003", ["Sensitivity__c"] = "topsecret" };
        var decision = Classification.Evaluate(record, SampleAcl());

        Assert.True(decision.Restricted);
        Assert.Equal("abcdefab-1234-1234-1234-abcdefabcdef", Assert.Single(decision.Acl!)["value"]);
    }
}
