// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// TestAclEngine/PrincipalMapperTests.cs
// -------------------------------------
// Unit tests for AclEngine.PrincipalMapper and its helpers.
//
// Covers:
//   - LooksLikeGuid
//   - StripSfUsernameSuffix
//   - ResolvePrincipalAsync (candidate generation + ordering)
//   - ResolveIdentifierAsync (cache, GUID short-circuit, no-graph-client path)
//   - LookupGraphUserIdAsync (direct path, filter path, ConsistencyLevel header,
//                             onPremisesUserPrincipalName, error handling)
//   - ToAclEntriesAsync (public sentinel, empty ids, deny-all, dedup, prewarm cache)
//   - PrewarmUsersAsync (SOQL batching, inactive users excluded, error tolerance)

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

// ---------------------------------------------------------------------------
// Helpers / fixtures
// ---------------------------------------------------------------------------

/// <summary>Fake SalesforceClient with a pluggable QueryAllAsync handler.</summary>
file sealed class FakeSfClient : SalesforceClient
{
    public Func<string, List<JsonObject>> QueryAllHandler = _ => new List<JsonObject>();
    public int QueryAllCallCount;

    public FakeSfClient()
        : base("https://test.my.salesforce.com", "60.0", "mock-token")
    {
    }

    public override Task<List<JsonObject>> QueryAllAsync(string soql, bool tooling = false)
    {
        QueryAllCallCount++;
        return Task.FromResult(QueryAllHandler(soql));
    }
}

/// <summary>Fake GraphClient with a pluggable GetAsync handler (Python `graph.get` mock).</summary>
file sealed class FakeGraphClient : GraphClient
{
    public Func<string, Dictionary<string, string>?, JsonNode?> Handler = (_, _) => null;
    public readonly List<(string Path, Dictionary<string, string>? Headers)> Calls = new();

    public override Task<JsonNode?> GetAsync(string pathOrUrl, Dictionary<string, string>? headers = null)
    {
        Calls.Add((pathOrUrl, headers));
        return Task.FromResult(Handler(pathOrUrl, headers));
    }
}

/// <summary>Log handler that records emitted records (Python `caplog`).</summary>
file sealed class CapturingLogHandler : LogHandler
{
    public readonly List<LogRecord> Records = new();

    protected override void Emit(LogRecord record) => Records.Add(record);
}

file static class MapperHelpers
{
    public const string TenantId = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string ValidGuid = "12345678-1234-1234-1234-123456789abc";

    public static PrincipalMapper MakeMapper(
        GraphClient? graphClient = null,
        SalesforceClient? sfClient = null,
        string tenantId = TenantId)
    {
        sfClient ??= new FakeSfClient();
        return new PrincipalMapper(
            sfClient: sfClient,
            graphClient: graphClient,
            tenantId: tenantId,
            batchSize: 100);
    }

    public static AclResult AclResultOf(
        IEnumerable<string> userIds,
        string objectType = "Account",
        string recordId = "001X",
        bool isPublic = false)
    {
        return new AclResult(
            objectType: objectType,
            recordId: recordId,
            isPublic: isPublic,
            userIds: new HashSet<string>(userIds));
    }

    public static JsonObject UserRow(string id, string? fedId, string? userName, string? email)
        => new() { ["Id"] = id, ["FederationIdentifier"] = fedId, ["UserName"] = userName, ["Email"] = email };

    public static FakeSfClient SfWithUsers(params JsonObject[] users)
    {
        var sf = new FakeSfClient
        {
            QueryAllHandler = _ => users.Select(u => (JsonObject)u.DeepClone()).ToList(),
        };
        return sf;
    }
}

// ---------------------------------------------------------------------------
// LooksLikeGuid
// ---------------------------------------------------------------------------

public class LooksLikeGuidTests
{
    [Fact]
    public void ValidGuid()
    {
        Assert.True(PrincipalMapper.LooksLikeGuid("12345678-abcd-abcd-abcd-abcdef012345"));
    }

    [Fact]
    public void UppercaseGuid()
    {
        Assert.True(PrincipalMapper.LooksLikeGuid("12345678-ABCD-ABCD-ABCD-ABCDEF012345"));
    }

    [Fact]
    public void NotGuidEmail()
    {
        Assert.False(PrincipalMapper.LooksLikeGuid("user@nokia.com"));
    }

    [Fact]
    public void NotGuidTooFewParts()
    {
        Assert.False(PrincipalMapper.LooksLikeGuid("12345678-abcd-abcd-abcd"));
    }

    [Fact]
    public void NotGuidWrongLength()
    {
        Assert.False(PrincipalMapper.LooksLikeGuid("1234567-abcd-abcd-abcd-abcdef012345"));
    }

    [Fact]
    public void NotGuidNonHex()
    {
        Assert.False(PrincipalMapper.LooksLikeGuid("1234567g-abcd-abcd-abcd-abcdef012345"));
    }

    [Fact]
    public void EmptyString()
    {
        Assert.False(PrincipalMapper.LooksLikeGuid(""));
    }

    [Fact]
    public void WhitespaceStripped()
    {
        Assert.True(PrincipalMapper.LooksLikeGuid($"  {MapperHelpers.ValidGuid}  "));
    }
}

// ---------------------------------------------------------------------------
// StripSfUsernameSuffix
// ---------------------------------------------------------------------------

public class StripSfUsernameSuffixTests
{
    [Fact]
    public void StripsOrgSuffix()
    {
        Assert.Equal("john@nokia.com", PrincipalMapper.StripSfUsernameSuffix("john@nokia.com.cape2104"));
    }

    [Fact]
    public void StripsSandboxSuffix()
    {
        Assert.Equal("rohith@acme.co.uk", PrincipalMapper.StripSfUsernameSuffix("rohith@acme.co.uk.sandboxDev"));
    }

    [Fact]
    public void NoStripTwoLabelDomain()
    {
        Assert.Null(PrincipalMapper.StripSfUsernameSuffix("john@nokia.com"));
    }

    [Fact]
    public void NoStripSingleLabelDomain()
    {
        Assert.Null(PrincipalMapper.StripSfUsernameSuffix("john@nokia"));
    }

    [Fact]
    public void NoAtSign()
    {
        Assert.Null(PrincipalMapper.StripSfUsernameSuffix("johnnokia.com.suffix"));
    }

    [Fact]
    public void ExtUserWithSuffix()
    {
        // Nokia external user pattern
        Assert.Equal(
            "rohith.kakumani.ext@nokia.com",
            PrincipalMapper.StripSfUsernameSuffix("rohith.kakumani.ext@nokia.com.cape2104"));
    }

    [Fact]
    public void ThreeLabelDomain()
    {
        Assert.Equal("user@sub.nokia.com", PrincipalMapper.StripSfUsernameSuffix("user@sub.nokia.com.suffix"));
    }
}

// ---------------------------------------------------------------------------
// ResolvePrincipalAsync – candidate generation
// ---------------------------------------------------------------------------

public class ResolvePrincipalTests
{
    [Fact]
    public async Task UsesFederationIdentifierFirst()
    {
        var mapper = MapperHelpers.MakeMapper();
        var details = new JsonObject
        {
            ["FederationIdentifier"] = "fed@nokia.com",
            ["UserName"] = "user@nokia.com.cape2104",
            ["Email"] = "email@nokia.com",
        };
        var result = await mapper.ResolvePrincipalAsync(details);
        // No graph client → first non-empty identifier returned directly
        Assert.Equal("fed@nokia.com", result);
    }

    [Fact]
    public async Task FallsBackToUsernameWhenNoFedId()
    {
        var mapper = MapperHelpers.MakeMapper();
        var details = new JsonObject
        {
            ["FederationIdentifier"] = "",
            ["UserName"] = "user@nokia.com.cape2104",
            ["Email"] = "email@nokia.com",
        };
        var result = await mapper.ResolvePrincipalAsync(details);
        // Raw UserName returned (no graph client, no GUID)
        Assert.Equal("user@nokia.com.cape2104", result);
    }

    [Fact]
    public async Task FallsBackToEmailWhenNoFedOrUsername()
    {
        var mapper = MapperHelpers.MakeMapper();
        var details = new JsonObject
        {
            ["FederationIdentifier"] = null,
            ["UserName"] = null,
            ["Email"] = "email@nokia.com",
        };
        var result = await mapper.ResolvePrincipalAsync(details);
        Assert.Equal("email@nokia.com", result);
    }

    [Fact]
    public async Task ReturnsNoneWhenAllFieldsEmpty()
    {
        var mapper = MapperHelpers.MakeMapper();
        var details = new JsonObject
        {
            ["FederationIdentifier"] = "",
            ["UserName"] = "",
            ["Email"] = "",
        };
        Assert.Null(await mapper.ResolvePrincipalAsync(details));
    }

    /// <summary>When Graph resolves the stripped username but not the raw one, it is returned.</summary>
    [Fact]
    public async Task StrippedUsernameTriedAfterRaw()
    {
        var graph = new FakeGraphClient
        {
            Handler = (path, _) =>
            {
                // Direct lookup always 404s
                if (path.Contains("/users/") && !path.Contains("filter"))
                    throw new GraphApiError(404, "not found");
                // Filter lookup: only succeeds for the stripped address
                if (path.Contains("cape2104"))
                    return new JsonObject { ["value"] = new JsonArray() };
                return new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = MapperHelpers.ValidGuid }) };
            },
        };
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        var details = new JsonObject
        {
            ["FederationIdentifier"] = "",
            ["UserName"] = "john@nokia.com.cape2104",
            ["Email"] = "",
        };
        var result = await mapper.ResolvePrincipalAsync(details);
        Assert.Equal(MapperHelpers.ValidGuid, result);
    }

    /// <summary>FederationIdentifier == Email should not be looked up twice.</summary>
    [Fact]
    public async Task NoDuplicateCandidates()
    {
        var callLog = new List<string>();
        var graph = new FakeGraphClient
        {
            Handler = (path, _) =>
            {
                callLog.Add(path);
                throw new GraphApiError(404, "nf");
            },
        };
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        var details = new JsonObject
        {
            ["FederationIdentifier"] = "same@nokia.com",
            ["UserName"] = "same@nokia.com",
            ["Email"] = "same@nokia.com",
        };
        await mapper.ResolvePrincipalAsync(details);
        // Each unique candidate is tried once (direct + filter = 2 calls max per candidate)
        // All calls should be for "same@nokia.com" only, not duplicated 3x
        Assert.True(callLog.Count <= 4);  // 2 attempts × 1 unique candidate (+ possibly stripped)
    }
}

// ---------------------------------------------------------------------------
// ResolveIdentifierAsync
// ---------------------------------------------------------------------------

public class ResolveIdentifierTests
{
    [Fact]
    public async Task CacheHitReturnsCached()
    {
        var mapper = MapperHelpers.MakeMapper();
        mapper._principalCache["user@nokia.com"] = MapperHelpers.ValidGuid;
        Assert.Equal(MapperHelpers.ValidGuid, await mapper.ResolveIdentifierAsync("user@nokia.com"));
    }

    [Fact]
    public async Task GuidPassthrough()
    {
        var mapper = MapperHelpers.MakeMapper();
        Assert.Equal(MapperHelpers.ValidGuid, await mapper.ResolveIdentifierAsync(MapperHelpers.ValidGuid));
        Assert.Equal(MapperHelpers.ValidGuid, mapper._principalCache[MapperHelpers.ValidGuid]);
    }

    [Fact]
    public async Task NoGraphClientReturnsIdentifierDirectly()
    {
        var mapper = MapperHelpers.MakeMapper(graphClient: null);
        var result = await mapper.ResolveIdentifierAsync("user@nokia.com");
        Assert.Equal("user@nokia.com", result);
        Assert.Equal("user@nokia.com", mapper._principalCache["user@nokia.com"]);
    }

    [Fact]
    public async Task WithGraphClientCallsLookup()
    {
        var graph = new FakeGraphClient
        {
            Handler = (_, _) => new JsonObject { ["id"] = MapperHelpers.ValidGuid },
        };
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        var result = await mapper.ResolveIdentifierAsync("user@nokia.com");
        Assert.Equal(MapperHelpers.ValidGuid, result);
    }

    [Fact]
    public async Task CacheNoneOnMiss()
    {
        var graph = new FakeGraphClient
        {
            Handler = (_, _) => throw new GraphApiError(404, "nf"),
        };
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        var result = await mapper.ResolveIdentifierAsync("ghost@nokia.com");
        Assert.Null(result);
        Assert.True(mapper._principalCache.ContainsKey("ghost@nokia.com"));
        Assert.Null(mapper._principalCache["ghost@nokia.com"]);
    }
}

// ---------------------------------------------------------------------------
// LookupGraphUserIdAsync
// ---------------------------------------------------------------------------

public class LookupGraphUserIdTests
{
    private static GraphClient GraphClientOf(
        JsonObject? directResponse = null,
        JsonObject? filterResponse = null,
        Exception? directExc = null,
        Exception? filterExc = null)
    {
        return new FakeGraphClient
        {
            Handler = (path, _) =>
            {
                if (!path.Contains("$filter"))
                {
                    if (directExc is not null)
                        throw directExc;
                    return directResponse?.DeepClone() ?? new JsonObject();
                }
                if (filterExc is not null)
                    throw filterExc;
                return filterResponse?.DeepClone() ?? new JsonObject { ["value"] = new JsonArray() };
            },
        };
    }

    [Fact]
    public async Task DirectLookupSuccess()
    {
        var graph = GraphClientOf(directResponse: new JsonObject { ["id"] = MapperHelpers.ValidGuid });
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        Assert.Equal(MapperHelpers.ValidGuid, await mapper.LookupGraphUserIdAsync("user@nokia.com"));
    }

    [Fact]
    public async Task FallbackToFilterWhenDirect404()
    {
        var graph = GraphClientOf(
            directExc: new GraphApiError(404, "not found"),
            filterResponse: new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = MapperHelpers.ValidGuid }) });
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        Assert.Equal(MapperHelpers.ValidGuid, await mapper.LookupGraphUserIdAsync("user@nokia.com"));
    }

    [Fact]
    public async Task FilterSendsConsistencyLevelHeader()
    {
        var graph = new FakeGraphClient();
        graph.Handler = (path, _) =>
        {
            if (!path.Contains("$filter"))
                throw new GraphApiError(404, "nf");
            return new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = MapperHelpers.ValidGuid }) };
        };
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        await mapper.LookupGraphUserIdAsync("user@nokia.com");

        var filterCall = graph.Calls.First(c => c.Path.Contains("$filter"));
        Assert.NotNull(filterCall.Headers);
        Assert.Single(filterCall.Headers!);
        Assert.Equal("eventual", filterCall.Headers!["ConsistencyLevel"]);
    }

    [Fact]
    public async Task FilterIncludesOnPremisesUpn()
    {
        var graph = new FakeGraphClient();
        graph.Handler = (path, _) =>
        {
            if (!path.Contains("$filter"))
                throw new GraphApiError(404, "nf");
            return new JsonObject { ["value"] = new JsonArray() };
        };
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        await mapper.LookupGraphUserIdAsync("user@nokia.com");

        var filterPath = graph.Calls.Select(c => c.Path).First(p => p.Contains("$filter"));
        Assert.Contains("onPremisesUserPrincipalName", filterPath);
    }

    [Fact]
    public async Task FilterIncludesCountParam()
    {
        var graph = new FakeGraphClient();
        graph.Handler = (path, _) =>
        {
            if (!path.Contains("$filter"))
                throw new GraphApiError(404, "nf");
            return new JsonObject { ["value"] = new JsonArray() };
        };
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        await mapper.LookupGraphUserIdAsync("user@nokia.com");

        var filterPath = graph.Calls.Select(c => c.Path).First(p => p.Contains("$filter"));
        Assert.Contains("$count=true", filterPath);
    }

    [Fact]
    public async Task ReturnsNoneWhenBothAttemptsFail()
    {
        var graph = GraphClientOf(
            directExc: new GraphApiError(404, "nf"),
            filterResponse: new JsonObject { ["value"] = new JsonArray() });
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        Assert.Null(await mapper.LookupGraphUserIdAsync("ghost@nokia.com"));
    }

    [Fact]
    public async Task Non404ErrorPropagates()
    {
        var graph = GraphClientOf(directExc: new GraphApiError(500, "server error"));
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        var excInfo = await Assert.ThrowsAsync<GraphApiError>(
            () => mapper.LookupGraphUserIdAsync("user@nokia.com"));
        Assert.Equal(500, excInfo.StatusCode);
    }

    [Fact]
    public async Task ApostropheEscapedInFilter()
    {
        var graph = new FakeGraphClient();
        graph.Handler = (path, _) =>
        {
            if (!path.Contains("$filter"))
                throw new GraphApiError(404, "nf");
            return new JsonObject { ["value"] = new JsonArray() };
        };
        var mapper = MapperHelpers.MakeMapper(graphClient: graph);
        await mapper.LookupGraphUserIdAsync("o'brien@nokia.com");

        var filterPath = graph.Calls.Select(c => c.Path).First(p => p.Contains("$filter"));
        Assert.Contains("o''brien", filterPath);
    }
}

// ---------------------------------------------------------------------------
// ToAclEntriesAsync
// ---------------------------------------------------------------------------

public class ToAclEntriesTests
{
    [Fact]
    public async Task PublicSentinelReturnsEveryoneGrant()
    {
        var mapper = MapperHelpers.MakeMapper();
        var result = await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new[] { Models.PublicSentinel }));
        var entry = Assert.Single(result);
        Assert.Equal(
            new Dictionary<string, string> { ["accessType"] = "grant", ["type"] = "everyone", ["value"] = "everyone" },
            entry);
    }

    [Fact]
    public async Task IsPublicFlagReturnsEveryoneGrant()
    {
        var mapper = MapperHelpers.MakeMapper();
        var result = await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new string[] { }, isPublic: true));
        var entry = Assert.Single(result);
        Assert.Equal(
            new Dictionary<string, string> { ["accessType"] = "grant", ["type"] = "everyone", ["value"] = "everyone" },
            entry);
    }

    [Fact]
    public async Task EmptyUserIdsReturnsDenyAll()
    {
        var mapper = MapperHelpers.MakeMapper();
        var result = await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new string[] { }));
        var entry = Assert.Single(result);
        Assert.Equal(
            new Dictionary<string, string> { ["accessType"] = "deny", ["type"] = "everyone", ["value"] = "everyone" },
            entry);
    }

    [Fact]
    public async Task UnresolvableUsersReturnsDenyAll()
    {
        var sf = MapperHelpers.SfWithUsers();
        var mapper = MapperHelpers.MakeMapper(sfClient: sf);
        var result = await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new[] { "005USER1" }));
        var entry = Assert.Single(result);
        Assert.Equal(
            new Dictionary<string, string> { ["accessType"] = "deny", ["type"] = "everyone", ["value"] = "everyone" },
            entry);
    }

    [Fact]
    public async Task ResolvedUserReturnsGrantEntry()
    {
        var sf = MapperHelpers.SfWithUsers(
            MapperHelpers.UserRow("005USER1", "user@nokia.com", "user@nokia.com.cape2104", "user@nokia.com"));
        var mapper = MapperHelpers.MakeMapper(sfClient: sf);
        var result = await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new[] { "005USER1" }));
        Assert.Single(result);
        Assert.Equal(
            new Dictionary<string, string> { ["accessType"] = "grant", ["type"] = "user", ["value"] = "user@nokia.com" },
            result[0]);
    }

    [Fact]
    public async Task DeduplicationOfSamePrincipal()
    {
        var sf = MapperHelpers.SfWithUsers(
            MapperHelpers.UserRow("005USER1", "user@nokia.com", null, null),
            MapperHelpers.UserRow("005USER2", "USER@NOKIA.COM", null, null));
        var mapper = MapperHelpers.MakeMapper(sfClient: sf);
        var result = await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new[] { "005USER1", "005USER2" }));
        // Case-insensitive dedup — only one entry
        Assert.Single(result);
    }

    [Fact]
    public async Task UsesPrewarmCache()
    {
        var sf = new FakeSfClient();  // should NOT be called
        var mapper = MapperHelpers.MakeMapper(sfClient: sf);
        // Pre-populate cache
        mapper._userDetailsCache["005USER1"] = new JsonObject
        {
            ["Id"] = "005USER1",
            ["FederationIdentifier"] = "cached@nokia.com",
            ["UserName"] = null,
            ["Email"] = null,
        };
        var result = await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new[] { "005USER1" }));
        Assert.Equal(0, sf.QueryAllCallCount);
        Assert.Equal("cached@nokia.com", result[0]["value"]);
    }

    [Fact]
    public async Task GraphGuidUsedAsAclValue()
    {
        var sf = MapperHelpers.SfWithUsers(
            MapperHelpers.UserRow("005USER1", "user@nokia.com", null, null));
        var graph = new FakeGraphClient
        {
            Handler = (_, _) => new JsonObject { ["id"] = MapperHelpers.ValidGuid },
        };
        var mapper = MapperHelpers.MakeMapper(sfClient: sf, graphClient: graph);
        var result = await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new[] { "005USER1" }));
        Assert.Equal(MapperHelpers.ValidGuid, result[0]["value"]);
    }

    [Fact]
    public async Task WarningEmittedOncePerUser()
    {
        var sf = MapperHelpers.SfWithUsers(
            MapperHelpers.UserRow("005GHOST", null, null, "ghost@nokia.com"));
        var graph = new FakeGraphClient
        {
            Handler = (_, _) => throw new GraphApiError(404, "nf"),
        };
        var mapper = MapperHelpers.MakeMapper(sfClient: sf, graphClient: graph);

        // Python: caplog.at_level(logging.WARNING, logger="salesforce_connector.acl_engine")
        var logger = Logging.GetLoggerObject("salesforce_connector.acl_engine");
        var handler = new CapturingLogHandler();
        var previousLevel = logger.Level;
        logger.Level = LogLevels.Warning;
        logger.AddHandler(handler);
        try
        {
            await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new[] { "005GHOST" }));
            await mapper.ToAclEntriesAsync(MapperHelpers.AclResultOf(new[] { "005GHOST" }));
        }
        finally
        {
            logger.RemoveHandler(handler);
            logger.Level = previousLevel;
        }

        var warnings = handler.Records.Where(r => r.Message.Contains("no M365 principal found")).ToList();
        Assert.Single(warnings);  // second call suppressed
    }
}

// ---------------------------------------------------------------------------
// PrewarmUsersAsync
// ---------------------------------------------------------------------------

public class PrewarmUsersTests
{
    [Fact]
    public async Task PopulatesCache()
    {
        var sf = MapperHelpers.SfWithUsers(
            MapperHelpers.UserRow("005A", "a@nokia.com", "a@nokia.com.sf", "a@nokia.com"));
        var mapper = MapperHelpers.MakeMapper(sfClient: sf);
        await mapper.PrewarmUsersAsync(new HashSet<string> { "005A" });
        Assert.True(mapper._userDetailsCache.ContainsKey("005A"));
        Assert.Equal("a@nokia.com", (string?)mapper._userDetailsCache["005A"]["FederationIdentifier"]);
    }

    [Fact]
    public async Task SkipsAlreadyCached()
    {
        var sf = new FakeSfClient();
        var mapper = MapperHelpers.MakeMapper(sfClient: sf);
        mapper._userDetailsCache["005A"] = new JsonObject { ["Id"] = "005A" };
        await mapper.PrewarmUsersAsync(new HashSet<string> { "005A" });
        Assert.Equal(0, sf.QueryAllCallCount);
    }

    [Fact]
    public async Task BatchesLargeSets()
    {
        var ids = Enumerable.Range(0, 250).Select(i => $"005{i:D15}").ToHashSet();
        var sf = new FakeSfClient();
        var mapper = MapperHelpers.MakeMapper(sfClient: sf);
        await mapper.PrewarmUsersAsync(ids, batchSize: 100);
        // 250 ids / batchSize=100 → 3 SOQL calls
        Assert.Equal(3, sf.QueryAllCallCount);
    }

    [Fact]
    public async Task ToleratesSoqlError()
    {
        var sf = new FakeSfClient
        {
            QueryAllHandler = _ => throw new InvalidOperationException("SOQL failed"),
        };
        var mapper = MapperHelpers.MakeMapper(sfClient: sf);
        // Should not raise
        await mapper.PrewarmUsersAsync(new HashSet<string> { "005A" });
        Assert.False(mapper._userDetailsCache.ContainsKey("005A"));
    }
}
