// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Port of tests/test_acl_engine/test_acl_parent_mapping.py.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Item;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

public class AclParentMappingTests
{
    /// <summary>
    /// AclResolver whose ``_build_private_acl_map`` fails the test if invoked,
    /// mirroring the Python tests' monkeypatched ``fake_private_acl_map``.
    /// </summary>
    internal class FailingPrivateAclResolver : AclResolver
    {
        // Never invoked — instances are created via GetUninitializedObject
        // (the C# equivalent of Python's ``AclResolver.__new__(AclResolver)``).
        public FailingPrivateAclResolver()
            : base(null!, null!)
        {
        }

        internal override Task<Dictionary<string, List<Dictionary<string, string>>>> BuildPrivateAclMapAsync(
            string objectName,
            List<JsonObject> records)
        {
            Assert.Fail($"Did not expect fallback private ACL path for {objectName}: {records}");
            return Task.FromResult(new Dictionary<string, List<Dictionary<string, string>>>());  // unreachable
        }
    }

    /// <summary>
    /// Mirror of the Python helper ``_make_resolver`` — creates the resolver via
    /// ``AclResolver.__new__`` (no constructor run) and assigns ``_handlers`` and
    /// ``_tenant_id`` directly.
    /// </summary>
    private static T MakeResolver<T>(JsonObject config)
        where T : AclResolver
    {
        var resolver = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(AclResolver)
            .GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(resolver, Converter.BuildHandlersFromConfig(config));
        typeof(AclResolver)
            .GetField("_tenantId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(resolver, "11111111-2222-3333-4444-555555555555");
        return resolver;
    }

    private static List<Dictionary<string, string>> ExpectedAcl() => new()
    {
        new Dictionary<string, string>
        {
            ["accessType"] = "grant",
            ["type"] = "everyone",
            ["value"] = "tenant-guid",
        },
    };

    [Fact]
    public async Task ParentControlledAclUsesNestedSchemaParentPath()
    {
        var resolver = MakeResolver<FailingPrivateAclResolver>(new JsonObject
        {
            ["objectList"] = new JsonArray(
                new JsonObject
                {
                    ["objectName"] = "Account",
                    ["selectedFields"] = new JsonObject { ["Name"] = "Name" },
                },
                new JsonObject
                {
                    ["objectName"] = "Contact",
                    ["selectedFields"] = new JsonObject
                    {
                        ["Name"] = "Name",
                        ["Account.Id"] = "AccountId",
                    },
                    ["parentObjectName"] = "Account",
                    ["objectNameAsChild"] = "Contacts",
                }),
        });
        var expectedAcl = ExpectedAcl();

        var result = await resolver.BuildParentControlledAclMapAsync(
            "Contact",
            new List<JsonObject>
            {
                new() { ["Id"] = "003-contact", ["Account"] = new JsonObject { ["Id"] = "001-account" } },
            },
            new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>
            {
                ["Account"] = new() { ["001-account"] = expectedAcl },
            });

        Assert.Single(result);
        Assert.Same(expectedAcl, result["003-contact"]);
    }

    [Fact]
    public async Task ParentControlledAclUsesSchemaMappedCustomParentField()
    {
        var resolver = MakeResolver<FailingPrivateAclResolver>(new JsonObject
        {
            ["objectList"] = new JsonArray(
                new JsonObject
                {
                    ["objectName"] = "Account",
                    ["selectedFields"] = new JsonObject { ["Name"] = "Name" },
                },
                new JsonObject
                {
                    ["objectName"] = "Project__c",
                    ["selectedFields"] = new JsonObject
                    {
                        ["Name"] = "Name",
                        ["Account__c"] = "AccountId",
                    },
                    ["parentObjectName"] = "Account",
                    ["objectNameAsChild"] = "Projects__r",
                }),
        });
        var expectedAcl = ExpectedAcl();

        var result = await resolver.BuildParentControlledAclMapAsync(
            "Project__c",
            new List<JsonObject>
            {
                new() { ["Id"] = "a01-project", ["Account__c"] = "001-account" },
            },
            new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>
            {
                ["Account"] = new() { ["001-account"] = expectedAcl },
            });

        Assert.Single(result);
        Assert.Same(expectedAcl, result["a01-project"]);
    }

    [Fact]
    public void SortObjectNamesUsesSchemaParentDependencies()
    {
        var resolver = MakeResolver<AclResolver>(new JsonObject
        {
            ["objectList"] = new JsonArray(
                new JsonObject
                {
                    ["objectName"] = "Account",
                    ["selectedFields"] = new JsonObject { ["Name"] = "Name" },
                },
                new JsonObject
                {
                    ["objectName"] = "Project__c",
                    ["selectedFields"] = new JsonObject { ["Account__c"] = "AccountId" },
                    ["parentObjectName"] = "Account",
                    ["objectNameAsChild"] = "Projects__r",
                },
                new JsonObject
                {
                    ["objectName"] = "Task__c",
                    ["selectedFields"] = new JsonObject { ["Project__c"] = "Project__cId" },
                    ["parentObjectName"] = "Project__c",
                    ["objectNameAsChild"] = "Tasks__r",
                }),
        });

        var ordered = resolver.SortObjectNames(new List<string> { "Task__c", "Project__c", "Account" });

        Assert.Equal(new List<string> { "Account", "Project__c", "Task__c" }, ordered);
    }
}
