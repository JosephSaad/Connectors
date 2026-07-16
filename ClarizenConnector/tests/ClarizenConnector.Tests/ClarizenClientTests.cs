using System.Net;
using ClarizenConnector.Clarizen;

namespace ClarizenConnector.Tests;

public class ClarizenClientTests
{
    private static ClarizenClient Make(MockHttpHandler handler, ApiBudget? budget = null)
    {
        var client = new ClarizenClient(
            TestConfig.Make(),
            budget ?? new ApiBudget(100_000, callsPerMinute: 6_000_000),
            handler);
        client.DelayAsync = (_, _) => Task.CompletedTask;
        return client;
    }

    [Fact]
    public async Task Login_StoresSessionId()
    {
        var handler = new MockHttpHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/authentication/login")
                ? MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "sess-123"}""")
                : MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var client = Make(handler);

        var session = await client.LoginAsync();
        Assert.Equal("sess-123", session);
    }

    [Fact]
    public async Task Query_SendsSessionHeader_AndPagesUntilDone()
    {
        var queryCalls = 0;
        string? authHeader = null;
        var handler = new MockHttpHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "sess-1"}""");

            queryCalls++;
            authHeader = request.Headers.TryGetValues("Authorization", out var values)
                ? values.First()
                : null;
            return queryCalls == 1
                ? MockHttpHandler.Json(HttpStatusCode.OK, """
                    {"entities": [{"id": "/Task/1"}, {"id": "/Task/2"}],
                     "paging": {"from": 2, "limit": 2, "hasMore": true}}
                    """)
                : MockHttpHandler.Json(HttpStatusCode.OK, """
                    {"entities": [{"id": "/Task/3"}],
                     "paging": {"from": 4, "limit": 2, "hasMore": false}}
                    """);
        });
        var client = Make(handler);

        var rows = await client.QueryAllAsync("SELECT Name FROM Task");
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, queryCalls);
        Assert.Equal("Session sess-1", authHeader);
    }

    [Fact]
    public async Task Query_RowLimit_StopsEarly()
    {
        var handler = new MockHttpHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/authentication/login")
                ? MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""")
                : MockHttpHandler.Json(HttpStatusCode.OK, """
                    {"entities": [{"id": "/Task/1"}, {"id": "/Task/2"}, {"id": "/Task/3"}],
                     "paging": {"hasMore": true}}
                    """));
        var client = Make(handler);

        var rows = await client.QueryAllAsync("SELECT Name FROM Task", rowLimit: 2);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task SessionExpiry_TriggersOneRelogin()
    {
        var logins = 0;
        var queries = 0;
        var handler = new MockHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
            {
                logins++;
                return MockHttpHandler.Json(HttpStatusCode.OK, $$"""{"sessionId": "sess-{{logins}}"}""");
            }
            queries++;
            return queries == 1
                ? MockHttpHandler.Json(HttpStatusCode.Unauthorized, """{"errorCode": "SessionTimeout"}""")
                : MockHttpHandler.Json(HttpStatusCode.OK, """{"entities": [], "paging": {"hasMore": false}}""");
        });
        var client = Make(handler);

        var rows = await client.QueryAllAsync("SELECT Name FROM Task");
        Assert.Empty(rows);
        Assert.Equal(2, logins);   // initial + re-login
        Assert.Equal(2, queries);  // failed + replay
    }

    [Fact]
    public async Task Throttle429_RetriesWithRetryAfter()
    {
        var queries = 0;
        var delays = new List<TimeSpan>();
        var handler = new MockHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            queries++;
            if (queries == 1)
            {
                var throttled = MockHttpHandler.Json((HttpStatusCode)429, "{}");
                throttled.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
                return throttled;
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, """{"entities": [], "paging": {"hasMore": false}}""");
        });
        var client = Make(handler);
        client.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        await client.QueryAllAsync("SELECT Name FROM Task");
        Assert.Contains(delays, d => Math.Abs(d.TotalSeconds - 9.0) < 0.01);
    }

    [Fact]
    public async Task QuotaExhaustion_PropagatesException()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        var budget = new ApiBudget(1, callsPerMinute: 6_000_000, utcNow: () => now);
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}"""));
        var client = Make(handler, budget);

        await client.LoginAsync();  // consumes the single budgeted call
        await Assert.ThrowsAsync<ClarizenQuotaExceededException>(
            () => client.QueryAllAsync("SELECT Name FROM Task"));
    }

    [Fact]
    public async Task Retrieve_BuildsSysIdQuery()
    {
        string? lastBody = null;
        var handler = new MockHttpHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            lastBody = body;
            return MockHttpHandler.Json(HttpStatusCode.OK,
                """{"entities": [{"id": "/Task/77", "Name": "Found"}], "paging": {"hasMore": false}}""");
        });
        var client = Make(handler);
        var config = new ClarizenConnector.Config.ObjectConfig
        {
            ObjectName = "Task",
            SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
        };

        var row = await client.RetrieveAsync(config, "/Task/77");
        Assert.NotNull(row);
        Assert.Equal("Found", row!["Name"]!.GetValue<string>());
        Assert.Contains("WHERE SYSID = 77", lastBody);
    }

    [Fact]
    public void IsSessionExpired_Detection()
    {
        Assert.True(ClarizenClient.IsSessionExpired(new GraphLikeResponse
        {
            StatusCode = HttpStatusCode.Unauthorized,
        }));
        Assert.True(ClarizenClient.IsSessionExpired(new GraphLikeResponse
        {
            StatusCode = HttpStatusCode.OK,
            Body = System.Text.Json.Nodes.JsonNode.Parse("""{"errorCode": "SessionTimeout"}"""),
        }));
        Assert.False(ClarizenClient.IsSessionExpired(new GraphLikeResponse
        {
            StatusCode = HttpStatusCode.OK,
            Body = System.Text.Json.Nodes.JsonNode.Parse("""{"entities": []}"""),
        }));
    }
}
