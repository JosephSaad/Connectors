// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for EntityDefinition-based OWD fetching.
//
// Covers both OWDFetcher (OrgWideDefaults.cs) and IdentityQueryClient
// (IdentityQueries.cs) EntityDefinition paths, including:
//   - Happy path: EntityDefinition resolves all objects
//   - Partial resolution: some objects fall back to Organization table
//   - Query failure: graceful fallback to Organization table
//   - Value mapping: all InternalSharingModel values → correct OWD values
//   - OWD overrides applied on top of EntityDefinition results
//   - Flag off: old behaviour unchanged

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

// ── Helpers ───────────────────────────────────────────────────────────────────

/// <summary>
/// Build a fake SalesforceClient with configurable QueryAll/Query responses
/// (port of the Python `_mock_sf_client` helper).
/// </summary>
file sealed class FakeSalesforceClient : SalesforceClient
{
    public List<JsonObject>? EntityDefRecords;
    public List<JsonObject>? OrgRecords;
    public Exception? EntityDefError;
    // Part of the Python `_mock_sf_client` signature; unused by the current tests.
    public Exception? OrgError = null;
    public List<string> OrgDescribeFields = new() { "DefaultAccountAccess", "DefaultCaseAccess" };

    public readonly List<(string Soql, bool Tooling)> QueryAllCalls = new();
    public readonly List<(string Soql, bool Tooling)> QueryCalls = new();

    public FakeSalesforceClient()
        : base("https://test.my.salesforce.com", "60.0", "mock-token")
    {
    }

    private static List<JsonObject> Clone(List<JsonObject>? records)
        => (records ?? new List<JsonObject>()).Select(r => (JsonObject)r.DeepClone()).ToList();

    public override Task<List<JsonObject>> QueryAllAsync(string soql, bool tooling = false)
    {
        QueryAllCalls.Add((soql, tooling));
        if (tooling)
        {
            if (EntityDefError is not null)
                throw EntityDefError;
            return Task.FromResult(Clone(EntityDefRecords));
        }
        if (OrgError is not null)
            throw OrgError;
        return Task.FromResult(Clone(OrgRecords));
    }

    public override Task<JsonObject> QueryAsync(string soql, bool tooling = false)
    {
        QueryCalls.Add((soql, tooling));
        List<JsonObject> records;
        if (tooling)
        {
            if (EntityDefError is not null)
                throw EntityDefError;
            records = Clone(EntityDefRecords);
        }
        else
        {
            if (OrgError is not null)
                throw OrgError;
            records = Clone(OrgRecords);
        }
        var array = new JsonArray();
        foreach (var r in records)
            array.Add(r);
        return Task.FromResult(new JsonObject { ["records"] = array });
    }

    public override Task<JsonObject> DescribeSObjectAsync(string sobjectName)
    {
        var fields = new JsonArray();
        foreach (var f in OrgDescribeFields)
            fields.Add(new JsonObject { ["name"] = f });
        return Task.FromResult(new JsonObject { ["fields"] = fields });
    }
}

file static class OwdTestHelpers
{
    public static JsonObject EntityDefRecord(string apiName, string? internalSharingModel)
        => new() { ["QualifiedApiName"] = apiName, ["InternalSharingModel"] = internalSharingModel };

    public static SalesforceClient DummySfClient()
        => new("https://test.my.salesforce.com", "60.0", "mock-token");
}

// ═══════════════════════════════════════════════════════════════════════════════
//  OWDFetcher (OrgWideDefaults.cs)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>Tests for OWDFetcher with USE_ENTITY_DEFINITION_OWD=true.</summary>
public class OwdFetcherEntityDefinitionTests
{
    // ── Value Mapping ─────────────────────────────────────────────────────────

    /// <summary>Each InternalSharingModel value maps to the correct OWDVisibility value.</summary>
    [Theory]
    [InlineData("Private", OWDVisibility.Private)]
    [InlineData("Read", OWDVisibility.PublicRead)]
    [InlineData("ReadSelect", OWDVisibility.PublicRead)]
    [InlineData("ReadWrite", OWDVisibility.PublicReadWrite)]
    [InlineData("ReadWriteTransfer", OWDVisibility.PublicReadWriteTransfer)]
    [InlineData("FullAccess", OWDVisibility.All)]
    [InlineData("ControlledByParent", OWDVisibility.ControlledByParent)]
    [InlineData("ControlledByCampaign", OWDVisibility.ControlledByCampaign)]
    [InlineData("ControlledByLeadOrContact", OWDVisibility.ControlledByLeadOrContact)]
    public async Task EntityDefValueMapping(string internalSharingModel, OWDVisibility expectedOwd)
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("TestObj", internalSharingModel),
            },
        };
        var fetcher = new OWDFetcher(
            sf, owdFieldMap: new Dictionary<string, string>(), useEntityDefinitionOwd: true,
            objectNames: new List<string> { "TestObj" });
        var result = await fetcher.GetOwdAsync("TestObj");
        Assert.Equal(expectedOwd.Value(), result);
    }

    /// <summary>An unrecognised InternalSharingModel value should default to Private.</summary>
    [Fact]
    public async Task UnknownSharingModelDefaultsToPrivate()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("TestObj", "SomeFutureValue"),
            },
        };
        var fetcher = new OWDFetcher(
            sf, owdFieldMap: new Dictionary<string, string>(), useEntityDefinitionOwd: true,
            objectNames: new List<string> { "TestObj" });
        var result = await fetcher.GetOwdAsync("TestObj");
        Assert.Equal(OWDVisibility.Private.Value(), result);
    }

    /// <summary>A null InternalSharingModel should default to Private.</summary>
    [Fact]
    public async Task NullSharingModelDefaultsToPrivate()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("TestObj", null),
            },
        };
        var fetcher = new OWDFetcher(
            sf, owdFieldMap: new Dictionary<string, string>(), useEntityDefinitionOwd: true,
            objectNames: new List<string> { "TestObj" });
        var result = await fetcher.GetOwdAsync("TestObj");
        Assert.Equal(OWDVisibility.Private.Value(), result);
    }

    // ── Happy Path ────────────────────────────────────────────────────────────

    /// <summary>When EntityDefinition returns all objects, no Organization query is needed.</summary>
    [Fact]
    public async Task AllObjectsResolvedViaEntityDefinition()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("Account", "ReadWrite"),
                OwdTestHelpers.EntityDefRecord("Contact", "ControlledByParent"),
            },
        };
        var fetcher = new OWDFetcher(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Account"] = "DefaultAccountAccess" },
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account", "Contact" });
        Assert.Equal("Edit", await fetcher.GetOwdAsync("Account"));
        Assert.Equal("ControlledByParent", await fetcher.GetOwdAsync("Contact"));
        // EntityDefinition query should be tooling=True; Organization query should NOT fire
        var calls = sf.QueryAllCalls;
        Assert.Single(calls);  // only one query_all call (the EntityDefinition one)
        Assert.True(calls[0].Tooling);
    }

    /// <summary>The EntityDefinition query fires only once regardless of how many GetOwdAsync calls.</summary>
    [Fact]
    public async Task CachePrimedOnceAcrossMultipleGetOwdCalls()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("Account", "Private"),
                OwdTestHelpers.EntityDefRecord("Contact", "Read"),
            },
        };
        var fetcher = new OWDFetcher(
            sf, owdFieldMap: new Dictionary<string, string>(), useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account", "Contact" });
        await fetcher.GetOwdAsync("Account");
        await fetcher.GetOwdAsync("Contact");
        await fetcher.GetOwdAsync("Account");
        // Only one tooling query should have been made
        var toolingCalls = sf.QueryAllCalls.Where(c => c.Tooling).ToList();
        Assert.Single(toolingCalls);
    }

    // ── Fallback to Organization Table ────────────────────────────────────────

    /// <summary>If EntityDefinition doesn't return an object, fall back to Organization query.</summary>
    [Fact]
    public async Task ObjectMissingFromEntityDefFallsBackToOrgTable()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("Account", "ReadWrite"),
                // Contact missing from EntityDefinition
            },
            OrgRecords = new List<JsonObject>
            {
                new() { ["DefaultAccountAccess"] = "Private" },  // Organization table
            },
        };
        var fetcher = new OWDFetcher(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Account"] = "DefaultAccountAccess" },
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account", "Contact" });
        // Account resolved via EntityDefinition
        Assert.Equal("Edit", await fetcher.GetOwdAsync("Account"));
        // Contact: not in EntityDefinition, not in owd_field_map → Private
        Assert.Equal("Private", await fetcher.GetOwdAsync("Contact"));
    }

    /// <summary>If the EntityDefinition query fails entirely, fall back to Organization table.</summary>
    [Fact]
    public async Task EntityDefQueryFailureFallsBackToOrgTable()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefError = new InvalidOperationException("Tooling API unavailable"),
            OrgRecords = new List<JsonObject>
            {
                new() { ["DefaultAccountAccess"] = "Read" },
            },
        };
        var fetcher = new OWDFetcher(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Account"] = "DefaultAccountAccess" },
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account" });
        // Should fall back to Organization query → "Read"
        var result = await fetcher.GetOwdAsync("Account");
        Assert.Equal("Read", result);
    }

    /// <summary>If EntityDefinition fails and object has no owdField, default to Private.</summary>
    [Fact]
    public async Task EntityDefQueryFailureObjectWithoutOwdFieldDefaultsPrivate()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefError = new InvalidOperationException("Tooling API unavailable"),
        };
        var fetcher = new OWDFetcher(
            sf,
            owdFieldMap: new Dictionary<string, string>(),  // Contact has no owdField
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Contact" });
        var result = await fetcher.GetOwdAsync("Contact");
        Assert.Equal("Private", result);
    }

    // ── OWD Overrides ─────────────────────────────────────────────────────────

    /// <summary>OWD_OVERRIDES config should override EntityDefinition values.</summary>
    [Fact]
    public async Task OwdOverrideAppliedOnTopOfEntityDef()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("Account", "ReadWrite"),
            },
        };
        var fetcher = new OWDFetcher(
            sf,
            owdFieldMap: new Dictionary<string, string>(),
            owdOverrides: new Dictionary<string, string> { ["Account"] = "Private" },
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account" });
        var result = await fetcher.GetOwdAsync("Account");
        Assert.Equal("Private", result);
    }

    // ── Flag Off ──────────────────────────────────────────────────────────────

    /// <summary>When useEntityDefinitionOwd=false, only the Organization table is queried.</summary>
    [Fact]
    public async Task FlagOffUsesOrgTableOnly()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("Account", "FullAccess"),
            },
            OrgRecords = new List<JsonObject>
            {
                new() { ["DefaultAccountAccess"] = "Private" },
            },
        };
        var fetcher = new OWDFetcher(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Account"] = "DefaultAccountAccess" },
            useEntityDefinitionOwd: false,
            objectNames: new List<string> { "Account" });
        var result = await fetcher.GetOwdAsync("Account");
        Assert.Equal("Private", result);  // Organization table value, NOT EntityDefinition
        // No tooling query should have been made
        var toolingCalls = sf.QueryAllCalls.Where(c => c.Tooling).ToList();
        Assert.Empty(toolingCalls);
    }

    // ── Predicates ────────────────────────────────────────────────────────────

    /// <summary>Mapped EntityDefinition values should produce correct IsPublic() results.</summary>
    [Theory]
    [InlineData("ReadWrite", true)]
    [InlineData("Read", true)]
    [InlineData("ReadSelect", true)]
    [InlineData("ReadWriteTransfer", true)]
    [InlineData("FullAccess", true)]
    [InlineData("Private", false)]
    [InlineData("ControlledByParent", false)]
    [InlineData("ControlledByCampaign", false)]
    public async Task IsPublicAfterEntityDefMapping(string sharingModel, bool expectPublic)
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("TestObj", sharingModel),
            },
        };
        var fetcher = new OWDFetcher(
            sf, owdFieldMap: new Dictionary<string, string>(), useEntityDefinitionOwd: true,
            objectNames: new List<string> { "TestObj" });
        var owd = await fetcher.GetOwdAsync("TestObj");
        Assert.Equal(expectPublic, OWDFetcher.IsPublic(owd));
    }

    /// <summary>Mapped EntityDefinition values should produce correct IsControlledByParent() results.</summary>
    [Theory]
    [InlineData("ControlledByParent", true)]
    [InlineData("ControlledByCampaign", true)]
    [InlineData("ControlledByLeadOrContact", true)]
    [InlineData("ReadWrite", false)]
    [InlineData("Private", false)]
    public async Task IsControlledByParentAfterEntityDefMapping(string sharingModel, bool expectCbp)
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("TestObj", sharingModel),
            },
        };
        var fetcher = new OWDFetcher(
            sf, owdFieldMap: new Dictionary<string, string>(), useEntityDefinitionOwd: true,
            objectNames: new List<string> { "TestObj" });
        var owd = await fetcher.GetOwdAsync("TestObj");
        Assert.Equal(expectCbp, OWDFetcher.IsControlledByParent(owd));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  IdentityQueryClient (IdentityQueries.cs)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>Tests for IdentityQueryClient.GetOrgWideDefaultsAsync with EntityDefinition.</summary>
public class IdentityQueryClientEntityDefinitionTests
{
    // ── Value Mapping ─────────────────────────────────────────────────────────

    /// <summary>Each InternalSharingModel value maps to the correct EntityVisibility.</summary>
    [Theory]
    [InlineData("Private", EntityVisibility.None)]
    [InlineData("Read", EntityVisibility.Read)]
    [InlineData("ReadSelect", EntityVisibility.Read)]
    [InlineData("ReadWrite", EntityVisibility.Edit)]
    [InlineData("ReadWriteTransfer", EntityVisibility.ReadEditTransfer)]
    [InlineData("FullAccess", EntityVisibility.ReadEditTransfer)]
    [InlineData("ControlledByParent", EntityVisibility.ControlledByParent)]
    [InlineData("ControlledByCampaign", EntityVisibility.ControlledByCampaign)]
    [InlineData("ControlledByLeadOrContact", EntityVisibility.ControlledByLeadOrContact)]
    public async Task EntityDefValueMapping(string internalSharingModel, EntityVisibility expectedVisibility)
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("TestObj", internalSharingModel),
            },
        };
        var qc = new IdentityQueryClient(
            sf, owdFieldMap: new Dictionary<string, string>(), useEntityDefinitionOwd: true,
            objectNames: new List<string> { "TestObj" });
        var result = await qc.GetOrgWideDefaultsAsync();
        Assert.Equal(expectedVisibility, result["TestObj"]);
    }

    // ── Happy Path ────────────────────────────────────────────────────────────

    /// <summary>EntityDefinition resolves all objects; no Organization query needed.</summary>
    [Fact]
    public async Task AllObjectsResolvedViaEntityDefinition()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("Account", "ReadWrite"),
                OwdTestHelpers.EntityDefRecord("Contact", "ControlledByParent"),
                OwdTestHelpers.EntityDefRecord("Case", "Private"),
            },
            OrgDescribeFields = new List<string> { "DefaultAccountAccess", "DefaultCaseAccess" },
        };
        var qc = new IdentityQueryClient(
            sf,
            owdFieldMap: new Dictionary<string, string>
            {
                ["Account"] = "DefaultAccountAccess",
                ["Case"] = "DefaultCaseAccess",
            },
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account", "Contact", "Case" });
        var result = await qc.GetOrgWideDefaultsAsync();
        Assert.Equal(EntityVisibility.Edit, result["Account"]);
        Assert.Equal(EntityVisibility.ControlledByParent, result["Contact"]);
        Assert.Equal(EntityVisibility.None, result["Case"]);
    }

    // ── Fallback to Organization Table ────────────────────────────────────────

    /// <summary>Objects missing from EntityDefinition should fall back to Organization query.</summary>
    [Fact]
    public async Task PartialEntityDefFallsBackForRemaining()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("Account", "ReadWrite"),
                // Case missing from EntityDefinition
            },
            OrgRecords = new List<JsonObject>
            {
                new() { ["DefaultCaseAccess"] = "Read" },
            },
            OrgDescribeFields = new List<string> { "DefaultCaseAccess" },
        };
        var qc = new IdentityQueryClient(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Case"] = "DefaultCaseAccess" },
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account", "Case" });
        var result = await qc.GetOrgWideDefaultsAsync();
        Assert.Equal(EntityVisibility.Edit, result["Account"]);
        Assert.Equal(EntityVisibility.Read, result["Case"]);
    }

    /// <summary>If EntityDefinition query fails, all objects fall back to Organization.</summary>
    [Fact]
    public async Task EntityDefFailureFallsBackToOrgTable()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefError = new InvalidOperationException("Tooling API unavailable"),
            OrgRecords = new List<JsonObject>
            {
                new() { ["DefaultAccountAccess"] = "Edit" },
            },
            OrgDescribeFields = new List<string> { "DefaultAccountAccess" },
        };
        var qc = new IdentityQueryClient(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Account"] = "DefaultAccountAccess" },
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account" });
        var result = await qc.GetOrgWideDefaultsAsync();
        Assert.Equal(EntityVisibility.Edit, result["Account"]);
    }

    // ── Flag Off ──────────────────────────────────────────────────────────────

    /// <summary>When flag is off, only the Organization table is queried.</summary>
    [Fact]
    public async Task FlagOffUsesOrgTableOnly()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>
            {
                OwdTestHelpers.EntityDefRecord("Account", "FullAccess"),
            },
            OrgRecords = new List<JsonObject>
            {
                new() { ["DefaultAccountAccess"] = "Private" },
            },
            OrgDescribeFields = new List<string> { "DefaultAccountAccess" },
        };
        var qc = new IdentityQueryClient(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Account"] = "DefaultAccountAccess" },
            useEntityDefinitionOwd: false);
        var result = await qc.GetOrgWideDefaultsAsync();
        Assert.Equal(EntityVisibility.None, result["Account"]);  // "Private" from Org table
    }

    // ── Empty / Edge Cases ────────────────────────────────────────────────────

    /// <summary>Empty objectNames list should skip EntityDefinition and use Organization.</summary>
    [Fact]
    public async Task NoObjectNamesReturnsOrgTableResults()
    {
        var sf = new FakeSalesforceClient
        {
            OrgRecords = new List<JsonObject>
            {
                new() { ["DefaultAccountAccess"] = "Read" },
            },
            OrgDescribeFields = new List<string> { "DefaultAccountAccess" },
        };
        var qc = new IdentityQueryClient(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Account"] = "DefaultAccountAccess" },
            useEntityDefinitionOwd: true,
            objectNames: new List<string>());
        var result = await qc.GetOrgWideDefaultsAsync();
        Assert.Equal(EntityVisibility.Read, result["Account"]);
    }

    /// <summary>EntityDefinition returns 0 records → all objects fall back to Organization.</summary>
    [Fact]
    public async Task EntityDefReturnsEmptyRecords()
    {
        var sf = new FakeSalesforceClient
        {
            EntityDefRecords = new List<JsonObject>(),
            OrgRecords = new List<JsonObject>
            {
                new() { ["DefaultAccountAccess"] = "Edit" },
            },
            OrgDescribeFields = new List<string> { "DefaultAccountAccess" },
        };
        var qc = new IdentityQueryClient(
            sf,
            owdFieldMap: new Dictionary<string, string> { ["Account"] = "DefaultAccountAccess" },
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account" });
        var result = await qc.GetOrgWideDefaultsAsync();
        Assert.Equal(EntityVisibility.Edit, result["Account"]);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Mapping constant completeness
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>Verify the two mapping dicts cover the same set of EntityDefinition values.</summary>
public class MappingConsistencyTests
{
    public static IEnumerable<object[]> OwdMappingKeys()
        => OWDFetcher.EntityDefToOwdValue.Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> VisibilityMappingKeys()
        => IdentityQueryClient.EntityDefToVisibility.Keys.Select(k => new object[] { k });

    /// <summary>EntityDefToOwdValue and EntityDefToVisibility must cover the same keys.</summary>
    [Fact]
    public void BothMappingsCoverSameKeys()
    {
        Assert.True(
            OWDFetcher.EntityDefToOwdValue.Keys.ToHashSet()
                .SetEquals(IdentityQueryClient.EntityDefToVisibility.Keys));
    }

    /// <summary>Every mapped value must be a valid OWDVisibility member value.</summary>
    [Theory]
    [MemberData(nameof(OwdMappingKeys))]
    public void OwdMappingProducesValidOwdVisibilityValue(string key)
    {
        var validValues = Enum.GetValues<OWDVisibility>().Select(v => v.Value()).ToHashSet();
        Assert.Contains(OWDFetcher.EntityDefToOwdValue[key], validValues);
    }

    /// <summary>Every mapped value must be a valid EntityVisibility member.</summary>
    [Theory]
    [MemberData(nameof(VisibilityMappingKeys))]
    public void VisibilityMappingProducesValidEntityVisibility(string key)
    {
        Assert.True(Enum.IsDefined(IdentityQueryClient.EntityDefToVisibility[key]));
    }

    /// <summary>Public EntityDefinition values should map to public in both OWDFetcher and IdentityQueryClient.</summary>
    [Fact]
    public void PublicValuesAgreeBetweenMappings()
    {
        var publicEdValues = new HashSet<string> { "Read", "ReadSelect", "ReadWrite", "ReadWriteTransfer", "FullAccess" };
        foreach (var key in publicEdValues)
        {
            Assert.True(OWDFetcher.IsPublic(OWDFetcher.EntityDefToOwdValue[key]),
                $"{key} should be public in OWDFetcher");
            Assert.True(IdentityModels.IsPublicVisibility(IdentityQueryClient.EntityDefToVisibility[key]),
                $"{key} should be public in IdentityQueryClient");
        }
    }

    /// <summary>Private EntityDefinition value should map to private in both paths.</summary>
    [Fact]
    public void PrivateValuesAgreeBetweenMappings()
    {
        Assert.True(OWDFetcher.RequiresPrivateAcl(OWDFetcher.EntityDefToOwdValue["Private"]));
        Assert.True(IdentityModels.IsPrivateVisibility(IdentityQueryClient.EntityDefToVisibility["Private"]));
    }

    /// <summary>ControlledByParent variants should agree between both mappings.</summary>
    [Fact]
    public void ControlledByParentValuesAgree()
    {
        var cbpKeys = new HashSet<string> { "ControlledByParent", "ControlledByCampaign", "ControlledByLeadOrContact" };
        foreach (var key in cbpKeys)
        {
            Assert.True(OWDFetcher.IsControlledByParent(OWDFetcher.EntityDefToOwdValue[key]),
                $"{key} OWDFetcher");
            Assert.True(IdentityModels.IsControlledByParent(IdentityQueryClient.EntityDefToVisibility[key]),
                $"{key} IdentityQueryClient");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Wiring: param pass-through from caller → underlying client
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>AclResolver must forward EntityDefinition params to OWDFetcher.</summary>
public class AclResolverWiringTests
{
    [Fact]
    public void ResolverPassesEntityDefParamsToOwdFetcher()
    {
        var sf = OwdTestHelpers.DummySfClient();
        var resolver = new AclResolver(
            sfClient: sf,
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account", "Case" });
        var fetcher = resolver.OwdFetcher;
        Assert.True(fetcher._useEntityDefinitionOwd);
        Assert.Equal(new List<string> { "Account", "Case" }, fetcher._objectNames);
    }

    [Fact]
    public void ResolverDefaultsEntityDefOff()
    {
        var sf = OwdTestHelpers.DummySfClient();
        var resolver = new AclResolver(sfClient: sf);
        Assert.False(resolver.OwdFetcher._useEntityDefinitionOwd);
        Assert.Equal(new List<string>(), resolver.OwdFetcher._objectNames);
    }
}

/// <summary>GroupAclBuilder must forward EntityDefinition params to IdentityQueryClient.</summary>
public class GroupAclBuilderWiringTests
{
    [Fact]
    public void BuilderPassesEntityDefParamsToQueryClient()
    {
        var sf = OwdTestHelpers.DummySfClient();
        var builder = new GroupAclBuilder(
            sfClient: sf,
            useEntityDefinitionOwd: true,
            objectNames: new List<string> { "Account", "Contact" });
        var qc = builder._queryClient;
        Assert.True(qc._useEntityDefinitionOwd);
        Assert.Equal(new List<string> { "Account", "Contact" }, qc._objectNames);
    }

    [Fact]
    public void BuilderDefaultsEntityDefOff()
    {
        var sf = OwdTestHelpers.DummySfClient();
        var builder = new GroupAclBuilder(sfClient: sf);
        Assert.False(builder._queryClient._useEntityDefinitionOwd);
        Assert.Equal(new List<string>(), builder._queryClient._objectNames);
    }
}

/// <summary>IdentitySyncHandler must forward EntityDefinition params to IdentityQueryClient.</summary>
public class IdentitySyncHandlerWiringTests
{
    [Fact]
    public void SyncHandlerPassesEntityDefParams()
    {
        var sf = OwdTestHelpers.DummySfClient();
        var handler = new IdentitySyncHandler(
            sfClient: sf,
            objectNames: new List<string> { "Account", "Case" },
            useEntityDefinitionOwd: true);
        var qc = handler._queryClient;
        Assert.True(qc._useEntityDefinitionOwd);
        Assert.Equal(new List<string> { "Account", "Case" }, qc._objectNames);
    }

    [Fact]
    public void SyncHandlerDefaultsEntityDefOff()
    {
        var sf = OwdTestHelpers.DummySfClient();
        var handler = new IdentitySyncHandler(sfClient: sf, objectNames: new List<string> { "Account" });
        Assert.False(handler._queryClient._useEntityDefinitionOwd);
        Assert.Equal(new List<string>(), handler._queryClient._objectNames);
    }
}
