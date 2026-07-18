// DeadLetterRedactionTests.cs — DEADLETTER_PAYLOAD_MODE=full|redacted at the
// SyncState.AppendFailedRecords choke point: redacted records keep ids/object
// type/error and per-field SHA-256 hashes but never a property value, content
// text, ACL entry or response body — including on the financial-classification
// dead-letter paths — and retry-failed still works because it re-fetches every
// record from source.

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;
using ClarizenConnector.Commands;

namespace ClarizenConnector.Tests;

public class DeadLetterRedactionTests
{
    private const string Connector = "ClarizenAdaptiveWork";

    private static ExternalItem SecretItem()
    {
        var item = new ExternalItem { Id = "Project_9" };
        item.Properties["Title"] = "Secret plan";
        item.Properties["PlannedCost"] = 123456.78;
        item.Content = "The plan: spend 123456.78 on rockets";
        item.Acl.Add(new AclEntry(AclEntryType.User, "entra-user-1", AclAccessType.Grant));
        item.Acl.Add(new AclEntry(AclEntryType.Group, "entra-group-2", AclAccessType.Deny));
        return item;
    }

    private static void AppendSecretFailure()
    {
        var item = SecretItem();
        SyncState.AppendFailedRecords(
            Connector,
            new List<(string, string)> { (item.Id, "HTTP 400: schema mismatch") },
            "Project",
            new Dictionary<string, JsonNode?> { [item.Id] = item.ToJson() },
            new Dictionary<string, JsonNode?>
            {
                [item.Id] = new JsonObject { ["echo"] = "PlannedCost 123456.78 rejected" },
            });
    }

    [Fact]
    public void FullMode_Default_KeepsThePayloadVerbatim()
    {
        using var scope = new SyncStateScope();
        using var env = new EnvScope((DeadLetterRedactor.ModeEnvVar, null));
        AppendSecretFailure();

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal("Secret plan", entry["request_body"]!["properties"]!["Title"]!.GetValue<string>());
        Assert.NotNull(entry["response_body"]);
    }

    [Fact]
    public void RedactedMode_KeepsIdsErrorAndHashes_NeverValues()
    {
        using var scope = new SyncStateScope();
        using var env = new EnvScope((DeadLetterRedactor.ModeEnvVar, "redacted"));
        AppendSecretFailure();

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));

        // Kept: id, object type, error, timestamp.
        Assert.Equal("Project_9", entry["item_id"]!.GetValue<string>());
        Assert.Equal("Project", entry["object_type"]!.GetValue<string>());
        Assert.Equal("HTTP 400: schema mismatch", entry["error"]!.GetValue<string>());
        Assert.NotNull(entry["timestamp"]);

        // Request body: field HASHES instead of values; ACL reduced to a count.
        var request = entry["request_body"]!.AsObject();
        Assert.True(request["redacted"]!.GetValue<bool>());
        Assert.Equal("Project_9", request["id"]!.GetValue<string>());
        Assert.StartsWith("sha256:", request["properties"]!["Title"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("sha256:", request["properties"]!["PlannedCost"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(
            DeadLetterRedactor.Sha256Hex("The plan: spend 123456.78 on rockets"),
            request["content_sha256"]!.GetValue<string>());
        Assert.Equal(2, request["acl_count"]!.GetValue<int>());
        Assert.Null(request["content"]);
        Assert.Null(request["acl"]);

        // Response bodies can echo request values — dropped entirely.
        Assert.Null(entry["response_body"]);

        // And the RAW dead-letter line never contains any of the values.
        var raw = File.ReadAllText(SyncState.FailedRecordsPath(Connector));
        Assert.DoesNotContain("Secret plan", raw);
        Assert.DoesNotContain("123456.78", raw);
        Assert.DoesNotContain("rockets", raw);
        Assert.DoesNotContain("entra-user-1", raw);
    }

    [Fact]
    public void RedactedMode_HashesAreStableForEqualValues()
    {
        var a = DeadLetterRedactor.HashNode(JsonValue.Create("Secret plan")!);
        var b = DeadLetterRedactor.HashNode(JsonValue.Create("Secret plan")!);
        var c = DeadLetterRedactor.HashNode(JsonValue.Create("Different plan")!);
        Assert.Equal(a, b);   // "same payload failed again" stays answerable
        Assert.NotEqual(a, c);
        Assert.StartsWith("sha256:", a, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactedMode_SqlBackend_RedactsBeforeTheGateway()
    {
        using var scope = new SyncStateScope();
        var gateway = new FakeSqlGateway();
        SqlStateStore.Gateway = gateway;
        try
        {
            using var env = new EnvScope(
                (DeadLetterRedactor.ModeEnvVar, "redacted"),
                ("USE_SQL_SERVER", "true"),
                ("SQL_CONNECTION_STRING", "Server=fake;Database=fake;"));
            SyncState.ResetProviderCache();
            AppendSecretFailure();

            var insert = Assert.Single(
                gateway.Calls, c => c.Sql.Contains("INSERT INTO dbo.DeadLetter"));
            var request = insert.Args.First(a => a.Name == "@request").Value as string;
            Assert.NotNull(request);
            Assert.Contains("sha256:", request);
            Assert.DoesNotContain("Secret plan", request);
            Assert.DoesNotContain("123456.78", request);
            // The response body is dropped, not stored.
            Assert.Null(insert.Args.First(a => a.Name == "@response").Value);
        }
        finally
        {
            SqlStateStore.ResetForTests();
            SyncState.ResetProviderCache();
        }
    }

    [Fact]
    public void AppConfigLoad_RejectsUnknownDeadLetterPayloadMode()
    {
        using var env = new EnvScope(
            ("CONNECTOR_ID", "ClarizenAdaptiveWork"),
            ("CLARIZEN_USERNAME", "svc@example.com"),
            ("SECRET_CLARIZEN_PASSWORD", "pw"),
            ("AAD_APP_TENANT_ID", "t"),
            ("AAD_APP_CLIENT_ID", "c"),
            ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
            (DeadLetterRedactor.ModeEnvVar, "redcated"));  // typo must fail, not mean "full"
        var exc = Assert.Throws<ArgumentException>(AppConfig.Load);
        Assert.Contains("DEADLETTER_PAYLOAD_MODE", exc.Message);
    }

    // ── Financial-field governance interplay ─────────────────────────────────

    /// <summary>Minimal pipeline fixture: one financial-tagged Project record,
    /// mocked Clarizen + Graph (reuses FakeGraphClient from GraphIngestTests).</summary>
    private sealed class FinancialFixture : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly SyncStateScope StateScope = new();
        public readonly AppConfig Config;
        public readonly SchemaConfig Schema;
        public readonly FakeGraphClient Graph;
        public readonly ClarizenClient Clarizen;
        public readonly IdentityStore Store;

        public FinancialFixture()
        {
            Config = TestConfig.Make(financialMode: "tag");
            Schema = new SchemaConfig
            {
                ObjectList = new List<ObjectConfig>
                {
                    new()
                    {
                        ObjectName = "Project",
                        DisplayName = "Project",
                        AclMode = "ownerOnly",
                        SelectedFields = new Dictionary<string, string>
                        {
                            ["Name"] = "Title",
                            ["PlannedCost"] = "PlannedCost",
                            ["Owner"] = "Owner",
                        },
                        FinancialFields = new List<string> { "PlannedCost" },
                    },
                },
            };

            var handler = new MockHttpHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                    return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
                var page = new JsonObject
                {
                    ["entities"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "/Project/1",
                        ["Name"] = "Apollo",
                        ["PlannedCost"] = 987654.21,
                        ["Owner"] = new JsonObject { ["id"] = "/User/1", ["name"] = "Owner One" },
                    }),
                    ["paging"] = new JsonObject { ["hasMore"] = false },
                };
                return MockHttpHandler.Json(HttpStatusCode.OK, page.ToJsonString());
            });
            Clarizen = new ClarizenClient(
                Config, new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler);
            Clarizen.DelayAsync = (_, _) => Task.CompletedTask;

            Graph = new FakeGraphClient(Config);
            Store = new IdentityStore("RedactionTests", Path.Combine(Dir.Path, "identity.db"));
            Store.Upsert(new PrincipalMapping(
                "/User/1", "user", "owner@example.com", "entra-owner", DateTime.UtcNow));
        }

        public IngestPipeline Pipeline()
        {
            var mapper = new PrincipalMapper(Store, "{}");
            var resolver = new AclResolver(
                mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
            return new IngestPipeline(
                Config, Schema, Clarizen, Graph, resolver, new ItemConverter(Config),
                ha: null,
                inventoryFactory: id => new ItemInventory(id, Path.Combine(Dir.Path, $"inv_{id}.db")));
        }

        public void Dispose()
        {
            Store.Dispose();
            StateScope.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task RedactedMode_FinancialClassifiedFailure_NeverCarriesTheFinancialValue()
    {
        using var fixture = new FinancialFixture();
        using var env = new EnvScope((DeadLetterRedactor.ModeEnvVar, "redacted"));
        fixture.Graph.FailingItemIds.Add("Project_1");  // force the item onto the dead-letter path

        var summary = await fixture.Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Failed);

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        var request = entry["request_body"]!.AsObject();
        // The FINANCIAL property was classified (tag mode keeps the value in
        // the item) — but the dead-letter record must only carry its hash.
        Assert.StartsWith(
            "sha256:", request["properties"]!["PlannedCost"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith(
            "sha256:", request["properties"]!["ContainsFinancialData"]!.GetValue<string>(), StringComparison.Ordinal);

        var raw = File.ReadAllText(SyncState.FailedRecordsPath(Connector));
        Assert.DoesNotContain("987654.21", raw);   // a redacted record never contains a financial value
        Assert.DoesNotContain("987654", raw);
        Assert.DoesNotContain("Apollo", raw);
    }

    [Fact]
    public async Task RedactedMode_RetryFailed_ReFetchesFromSource_AndSucceeds()
    {
        using var fixture = new FinancialFixture();
        JsonObject deadLettered;
        using (new EnvScope((DeadLetterRedactor.ModeEnvVar, "redacted")))
        {
            fixture.Graph.FailingItemIds.Add("Project_1");
            await fixture.Pipeline().RunAsync(fullCrawl: true);
            deadLettered = Assert.Single(SyncState.ReadFailedRecords(Connector));
        }

        // The redacted record carries NO usable payload — retry must therefore
        // re-fetch from Clarizen by id, exactly what retry-failed does.
        var itemId = deadLettered["item_id"]!.GetValue<string>();
        var parsed = RetryFailed.ParseItemId(itemId);
        Assert.NotNull(parsed);
        var objectConfig = fixture.Schema.FindObject(parsed!.Value.ObjectType)!;

        fixture.Graph.FailingItemIds.Clear();  // upstream issue fixed
        var row = await fixture.Clarizen.RetrieveAsync(objectConfig, parsed.Value.LocalId);
        Assert.NotNull(row);

        var (ok, error) = await fixture.Pipeline().IngestSingleAsync(
            new ClarizenRecord(objectConfig.ObjectName, row!), objectConfig);
        Assert.True(ok, error);

        // The re-ingested payload was rebuilt from SOURCE data (full values),
        // not reconstructed from the redacted dead-letter record.
        var put = fixture.Graph.Sent.Last(s => s.Method == HttpMethod.Put);
        Assert.Equal("Apollo", put.Body!["properties"]!["Title"]!.GetValue<string>());
        Assert.Equal(987654.21, put.Body!["properties"]!["PlannedCost"]!.GetValue<double>());
    }
}
