// DiagnosabilityTests.cs
// ----------------------
// Troubleshooting-hardening coverage: failure paths must (a) isolate at the
// right granularity (one bad event/record never kills its batch), (b) leave a
// subject-rich log trail, and (c) keep the dead-letter queue readable even
// when a record is torn. No loopback listeners are bound here, so this class
// does NOT join the "LoopbackWebhook" collection.

using System.Net;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

/// <summary>FakeSeismicClient whose GetContentAsync throws for chosen content ids.</summary>
file sealed class ThrowingSeismicClient : FakeSeismicClient
{
    /// <summary>Content ids whose lookup throws a SeismicApiError 500.</summary>
    public HashSet<string> ThrowApiErrorFor { get; } = new(StringComparer.Ordinal);

    /// <summary>Content ids whose lookup throws OperationCanceledException.</summary>
    public HashSet<string> ThrowCanceledFor { get; } = new(StringComparer.Ordinal);

    public override Task<SeismicContent?> GetContentAsync(
        string teamsiteId, string contentId, CancellationToken ct = default)
    {
        if (ThrowApiErrorFor.Contains(contentId))
            throw new SeismicApiError(500, "boom (simulated)");
        if (ThrowCanceledFor.Contains(contentId))
            throw new OperationCanceledException("graceful stop (simulated)");
        return base.GetContentAsync(teamsiteId, contentId, ct);
    }
}

/// <summary>Log handler that collects formatted lines for assertions.</summary>
file sealed class CollectingHandler : LogHandler
{
    public List<string> Lines { get; } = new();

    protected override void Emit(LogRecord record)
    {
        lock (Lines)
            Lines.Add(Format(record));
    }
}

public sealed class DiagnosabilityTests
{
    // ── shared builder ───────────────────────────────────────────────────────

    private static (AppConfig Config, GraphClient Graph, FakeHttpHandler GraphHandler,
        SqliteIdentityStore Store, string DbPath) BuildGraphStack()
    {
        var config = TestConfig.Build(objects: new[] { "ContentItem" });
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Post, "/$batch", FakeHttpHandler.BatchSuccess);
        handler.When(HttpMethod.Delete, "/items/",
            (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var graph = new GraphClient(config.Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };
        var dbPath = Path.Combine(Path.GetTempPath(), "seismic-diag-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteIdentityStore(dbPath);
        store.UpsertPrincipal(new PrincipalMapping("seismic-user-1", "user", "amy@contoso.com", "entra-user-1", "Amy"));
        return (config, graph, handler, store, dbPath);
    }

    private static void Cleanup(SqliteIdentityStore store, string dbPath)
    {
        store.Dispose();
        try
        {
            File.Delete(dbPath);
        }
        catch
        {
            // temp-file cleanup only
        }
    }

    // ── webhook event-dispatch boundary ──────────────────────────────────────

    [Fact]
    public async Task WebhookEvent_Failure_DeadLettersAndContinuesWithRemainingEvents()
    {
        using var state = new TempStateDir();
        var (config, graph, graphHandler, store, dbPath) = BuildGraphStack();
        try
        {
            var seismic = new ThrowingSeismicClient();
            seismic.ThrowApiErrorFor.Add("boom");
            seismic.Teamsites.Add(new SeismicTeamsite { Id = "ts1", Name = "Teamsite ts1" });
            seismic.ContentsByTeamsite["ts1"] = new List<SeismicContent> { TestContent.Make("ok1") };

            var pipeline = new IngestPipeline(config, seismic, graph, store);
            await pipeline.ProcessEventsAsync(new[]
            {
                new ContentEvent { Type = "contentPublished", ContentId = "boom", TeamsiteId = "ts1" },
                new ContentEvent { Type = "contentPublished", ContentId = "ok1", TeamsiteId = "ts1" },
            });

            // The event AFTER the failing one was still processed (ok1 was PUT).
            var putIds = graphHandler.Requests
                .Where(r => r.Method == HttpMethod.Post && r.Url.Contains("/$batch"))
                .SelectMany(r => JsonNode.Parse(r.Body!)!["requests"]!.AsArray()
                    .Select(req => req!["id"]!.GetValue<string>()))
                .ToList();
            Assert.Contains("ok1", putIds);
            Assert.DoesNotContain("boom", putIds);

            // The failing event was dead-lettered with the webhook context.
            var records = SyncState.ReadFailedRecords(config.Connector.Id);
            var record = Assert.Single(records);
            Assert.Equal("boom", record["item_id"]?.GetValue<string>());
            Assert.Equal("ContentItem", record["object_type"]?.GetValue<string>());
            Assert.Contains("webhook event 'contentPublished' failed", record["error"]?.GetValue<string>());
            Assert.Equal(1, pipeline.Stats.Failed);
        }
        finally
        {
            Cleanup(store, dbPath);
        }
    }

    [Fact]
    public async Task WebhookEvent_GracefulStopCancellation_PropagatesAndIsNotDeadLettered()
    {
        using var state = new TempStateDir();
        var (config, graph, graphHandler, store, dbPath) = BuildGraphStack();
        try
        {
            var seismic = new ThrowingSeismicClient();
            seismic.ThrowCanceledFor.Add("boom");
            seismic.Teamsites.Add(new SeismicTeamsite { Id = "ts1", Name = "Teamsite ts1" });
            seismic.ContentsByTeamsite["ts1"] = new List<SeismicContent> { TestContent.Make("ok1") };

            var pipeline = new IngestPipeline(config, seismic, graph, store);
            await Assert.ThrowsAsync<OperationCanceledException>(() => pipeline.ProcessEventsAsync(new[]
            {
                new ContentEvent { Type = "contentPublished", ContentId = "boom", TeamsiteId = "ts1" },
                new ContentEvent { Type = "contentPublished", ContentId = "ok1", TeamsiteId = "ts1" },
            }));

            // A graceful stop is NOT an event failure: nothing dead-lettered,
            // nothing counted as failed, and the later events were not run.
            Assert.Empty(SyncState.ReadFailedRecords(config.Connector.Id));
            Assert.Equal(0, pipeline.Stats.Failed);
            Assert.DoesNotContain(graphHandler.Requests,
                r => r.Method == HttpMethod.Post && r.Url.Contains("/$batch"));
        }
        finally
        {
            Cleanup(store, dbPath);
        }
    }

    // ── dead-letter file resilience ──────────────────────────────────────────

    [Fact]
    public void DeadLetter_TornLine_IsSkippedAndParseableRecordsSurvive()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        SyncState.AppendFailedRecords(path, new List<string> { "good-1" }, "ContentItem", "err1");
        // Simulate a crash mid-append: a torn, unterminated JSON line.
        File.AppendAllText(path, "{\"item_id\": \"torn\n");
        SyncState.AppendFailedRecords(path, new List<string> { "good-2" }, "Withdrawal", "err2");

        var records = SyncState.ReadFailedRecords("Conn");

        Assert.Equal(2, records.Count);
        Assert.Equal("good-1", records[0]["item_id"]?.GetValue<string>());
        Assert.Equal("good-2", records[1]["item_id"]?.GetValue<string>());
    }

    [Fact]
    public void DeadLetter_NonObjectLine_IsSkippedNotFatal()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        SyncState.AppendFailedRecords(path, new List<string> { "good-1" }, "ContentItem", "err1");
        // Parseable JSON that is not an object must not blow up the reader either.
        File.AppendAllText(path, "[1, 2, 3]\n");

        var records = SyncState.ReadFailedRecords("Conn");

        var record = Assert.Single(records);
        Assert.Equal("good-1", record["item_id"]?.GetValue<string>());
    }

    // ── $batch envelope failure log content ──────────────────────────────────

    [Fact]
    public async Task BatchEnvelopeFailure_LogsSubjectAndDeadLetterDisposition()
    {
        using var state = new TempStateDir();
        var config = TestConfig.Build(objects: new[] { "ContentItem" });
        var handler = new FakeHttpHandler();
        // Whole envelope is rejected outright (non-retryable 400).
        handler.When(HttpMethod.Post, "/$batch", (_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"bad envelope"}}"""));
        var graph = new GraphClient(config.Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };
        var dbPath = Path.Combine(Path.GetTempPath(), "seismic-diag-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteIdentityStore(dbPath);
        store.UpsertPrincipal(new PrincipalMapping("seismic-user-1", "user", "amy@contoso.com", "entra-user-1", "Amy"));

        var collector = new CollectingHandler();
        var ingestLogger = Logging.GetLoggerObject("seismic_connector.ingest");
        ingestLogger.AddHandler(collector);
        try
        {
            var seismic = new FakeSeismicClient();
            seismic.Teamsites.Add(new SeismicTeamsite { Id = "ts1", Name = "Sales Site" });
            seismic.ContentsByTeamsite["ts1"] = new List<SeismicContent>
            {
                TestContent.Make("c1"),
                TestContent.Make("c2"),
            };

            var pipeline = new IngestPipeline(config, seismic, graph, store);
            Assert.False(await pipeline.RunCrawlAsync(fullCrawl: true));

            // Operator-facing trail: WHAT failed (both ids at least sampled),
            // WHERE (teamsite name + id), and WHAT HAPPENS NEXT (dead-letter).
            string? line;
            lock (collector.Lines)
                line = collector.Lines.FirstOrDefault(l => l.Contains("Graph $batch envelope failed"));
            Assert.NotNull(line);
            Assert.Contains("2 ContentItem item(s)", line);
            Assert.Contains("Sales Site", line);
            Assert.Contains("ts1", line);
            Assert.Contains("c1", line);
            Assert.Contains("dead-lettered", line, StringComparison.OrdinalIgnoreCase);

            // And the queue really holds both items with the request bodies.
            var records = SyncState.ReadFailedRecords(config.Connector.Id);
            Assert.Equal(2, records.Count);
            Assert.All(records, r => Assert.NotNull(r["request_body"]));
        }
        finally
        {
            ingestLogger.RemoveHandler(collector);
            Cleanup(store, dbPath);
        }
    }

    // ── identity crawl: one bad principal never kills the crawl ──────────────

    [Fact]
    public async Task IdentityCrawl_BadRequestOnOnePrincipal_LeavesItUnmappedAndContinues()
    {
        var config = TestConfig.Build();
        var handler = new FakeHttpHandler();
        // Order matters: the FakeHttpHandler answers with the FIRST matching route.
        handler.When(
            r => r.Method == HttpMethod.Get && r.RequestUri!.ToString().Contains("/users/")
                && r.RequestUri!.ToString().Contains("DOMAIN"),
            (_, _) => FakeHttpHandler.Json(
                HttpStatusCode.BadRequest, """{"error":{"message":"invalid UPN"}}"""));
        handler.When(HttpMethod.Get, "/users/",
            (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"id":"entra-good"}"""));
        handler.When(
            r => r.Method == HttpMethod.Get && r.RequestUri!.ToString().Contains("/groups?")
                && r.RequestUri!.ToString().Contains("Bad"),
            (_, _) => FakeHttpHandler.Json(
                HttpStatusCode.BadRequest, """{"error":{"message":"invalid filter"}}"""));
        handler.When(HttpMethod.Get, "/groups?",
            (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"value":[{"id":"entra-group"}]}"""));

        var graph = new GraphClient(config.Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };
        var dbPath = Path.Combine(Path.GetTempPath(), "seismic-diag-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteIdentityStore(dbPath);
        try
        {
            var seismic = new FakeSeismicClient();
            seismic.Users.Add(new SeismicUser { Id = "u-good", Email = "good@contoso.com" });
            seismic.Users.Add(new SeismicUser { Id = "u-bad", Username = @"DOMAIN\jdoe" });
            seismic.Groups.Add(new SeismicGroup { Id = "g-good", Name = "Good Group" });
            seismic.Groups.Add(new SeismicGroup { Id = "g-bad", Name = "Bad Group" });

            var identity = new IdentitySync(seismic, graph, store);
            var stats = await identity.RunAsync(persist: true);

            // The crawl completed; only the malformed principals stayed unmapped.
            Assert.Equal(2, stats.UsersScanned);
            Assert.Equal(1, stats.UsersMapped);
            Assert.Equal(2, stats.GroupsScanned);
            Assert.Equal(1, stats.GroupsMapped);
            Assert.Equal("entra-good", store.GetEntraObjectId("u-good"));
            Assert.Null(store.GetEntraObjectId("u-bad"));
            Assert.Equal("entra-group", store.GetEntraObjectId("g-good"));
            Assert.Null(store.GetEntraObjectId("g-bad"));
        }
        finally
        {
            Cleanup(store, dbPath);
        }
    }

    // ── auth/permission failures still fail fast (no over-catching) ──────────

    [Fact]
    public async Task IdentityCrawl_AuthFailure_StillFailsFast()
    {
        var config = TestConfig.Build();
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Get, "/users/",
            (_, _) => FakeHttpHandler.Json(
                HttpStatusCode.Forbidden, """{"error":{"message":"insufficient privileges"}}"""));
        var graph = new GraphClient(config.Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };
        var dbPath = Path.Combine(Path.GetTempPath(), "seismic-diag-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteIdentityStore(dbPath);
        try
        {
            var seismic = new FakeSeismicClient();
            seismic.Users.Add(new SeismicUser { Id = "u1", Email = "someone@contoso.com" });

            var identity = new IdentitySync(seismic, graph, store);
            var ex = await Assert.ThrowsAsync<GraphApiError>(() => identity.RunAsync(persist: false));
            Assert.Equal(403, ex.StatusCode);
        }
        finally
        {
            Cleanup(store, dbPath);
        }
    }

    // ── withdrawal failure log + dead-letter disposition ─────────────────────

    [Fact]
    public async Task WithdrawFailure_LogsSubjectAndDeadLetters()
    {
        using var state = new TempStateDir();
        var config = TestConfig.Build(objects: new[] { "ContentItem" });
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Delete, "/items/",
            (_, _) => FakeHttpHandler.Json(
                HttpStatusCode.InternalServerError, """{"error":{"message":"delete exploded"}}"""));
        var graph = new GraphClient(config.Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };
        var dbPath = Path.Combine(Path.GetTempPath(), "seismic-diag-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteIdentityStore(dbPath);

        var collector = new CollectingHandler();
        var ingestLogger = Logging.GetLoggerObject("seismic_connector.ingest");
        ingestLogger.AddHandler(collector);
        try
        {
            var seismic = new FakeSeismicClient();
            var pipeline = new IngestPipeline(config, seismic, graph, store);

            await pipeline.WithdrawItemAsync("doc-9", "expired", "content expired", CancellationToken.None);

            string? line;
            lock (collector.Lines)
                line = collector.Lines.FirstOrDefault(l => l.Contains("WITHDRAW FAILED"));
            Assert.NotNull(line);
            Assert.Contains("doc-9", line);
            Assert.Contains("expired", line);
            Assert.Contains("remains in the index", line);

            var record = Assert.Single(SyncState.ReadFailedRecords(config.Connector.Id));
            Assert.Equal("doc-9", record["item_id"]?.GetValue<string>());
            Assert.Equal("Withdrawal", record["object_type"]?.GetValue<string>());
        }
        finally
        {
            ingestLogger.RemoveHandler(collector);
            Cleanup(store, dbPath);
        }
    }
}
