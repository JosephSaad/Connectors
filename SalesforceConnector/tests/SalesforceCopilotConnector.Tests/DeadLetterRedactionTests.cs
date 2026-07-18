// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for Config/DeadLetterRedaction.cs and its SyncState wiring —
// DEADLETTER_PAYLOAD_MODE=full|redacted. Verifies:
//   * full (default) keeps current behavior byte-for-byte;
//   * redacted keeps ids/object type/error/timestamp/acl and field NAMES while
//     replacing property values and content with sha256 hashes;
//   * the trade-off note is embedded in the record itself;
//   * retry-failed still works on redacted records — the retry path re-fetches
//     the item from Salesforce and never replays the stored request_body.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;
using SalesforceCopilotConnector.Tests.TestGraph;

namespace SalesforceCopilotConnector.Tests;

/// <summary>Touches DEADLETTER_PAYLOAD_MODE and SyncState.LogsDir — "EnvVars" collection.</summary>
[Collection("EnvVars")]
public sealed class DeadLetterRedactionTests : IDisposable
{
    private readonly string _tmpPath = Directory.CreateTempSubdirectory("dl_redaction_").FullName;
    private readonly string? _savedMode = Environment.GetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, _savedMode);
        try
        {
            Directory.Delete(_tmpPath, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private static string ExpectedHash(JsonNode? value)
    {
        var json = value?.ToJsonString() ?? "null";
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static JsonObject SampleRequestBody() => new()
    {
        ["id"] = "001A",
        ["acl"] = new JsonArray
        {
            new JsonObject { ["accessType"] = "grant", ["type"] = "everyone", ["value"] = "everyone" },
        },
        ["properties"] = new JsonObject
        {
            ["Name"] = "Acme Corp (confidential)",
            ["AnnualRevenue"] = 1_250_000,
        },
        ["content"] = new JsonObject { ["value"] = "Customer PII body text", ["type"] = "text" },
    };

    private List<JsonObject> ReadJsonl(string path) =>
        File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();

    // ── Mode parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void FullIsTheDefaultAndUnknownValuesStayFull()
    {
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, null);
        Assert.False(DeadLetterRedaction.RedactedMode);
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "full");
        Assert.False(DeadLetterRedaction.RedactedMode);
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "banana");
        Assert.False(DeadLetterRedaction.RedactedMode);
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "redacted");
        Assert.True(DeadLetterRedaction.RedactedMode);
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "REDACTED");
        Assert.True(DeadLetterRedaction.RedactedMode);
    }

    // ── Full mode preserves current behavior ─────────────────────────────────

    [Fact]
    public void FullModeWritesThePayloadVerbatim()
    {
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, null);
        var dlPath = Path.Combine(_tmpPath, "dl_full.jsonl");
        SyncState.AppendFailedRecords(
            dlPath,
            new List<(string, string)> { ("001A", "HTTP 500") },
            "Account",
            requestBodies: new Dictionary<string, JsonNode?> { ["001A"] = SampleRequestBody() });

        var record = Assert.Single(ReadJsonl(dlPath));
        var body = record["request_body"]!.AsObject();
        Assert.Equal("Acme Corp (confidential)", body["properties"]!["Name"]!.GetValue<string>());
        Assert.Equal("Customer PII body text", body["content"]!["value"]!.GetValue<string>());
        Assert.False(body.ContainsKey(DeadLetterRedaction.NoteKey));
    }

    // ── Redacted mode ────────────────────────────────────────────────────────

    [Fact]
    public void RedactedModeKeepsIdsAndHashesPropertyValues()
    {
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "redacted");
        var dlPath = Path.Combine(_tmpPath, "dl_redacted.jsonl");
        SyncState.AppendFailedRecords(
            dlPath,
            new List<(string, string)> { ("001A", "HTTP 500 InternalServerError") },
            "Account",
            requestBodies: new Dictionary<string, JsonNode?> { ["001A"] = SampleRequestBody() });

        var record = Assert.Single(ReadJsonl(dlPath));

        // Triage/retry fields survive untouched.
        Assert.Equal("001A", record["item_id"]!.GetValue<string>());
        Assert.Equal("Account", record["object_type"]!.GetValue<string>());
        Assert.Equal("HTTP 500 InternalServerError", record["error"]!.GetValue<string>());
        Assert.NotNull(record["timestamp"]);

        var body = record["request_body"]!.AsObject();
        // Field NAMES survive; values are hashes of the removed JSON values.
        Assert.Equal(
            ExpectedHash(JsonValue.Create("Acme Corp (confidential)")),
            body["properties"]!["Name"]!.GetValue<string>());
        Assert.Equal(
            ExpectedHash(JsonValue.Create(1_250_000)),
            body["properties"]!["AnnualRevenue"]!.GetValue<string>());
        // content.value hashed, content.type kept.
        Assert.Equal(
            ExpectedHash(JsonValue.Create("Customer PII body text")),
            body["content"]!["value"]!.GetValue<string>());
        Assert.Equal("text", body["content"]!["type"]!.GetValue<string>());
        // ACL entries (principal ids, not item content) survive.
        Assert.Equal("everyone", body["acl"]![0]!["value"]!.GetValue<string>());
        // The raw values are gone from the on-disk line entirely.
        var rawLine = File.ReadAllText(dlPath);
        Assert.DoesNotContain("Acme Corp (confidential)", rawLine);
        Assert.DoesNotContain("Customer PII body text", rawLine);
    }

    [Fact]
    public void RedactedRecordDocumentsTheTradeOffInline()
    {
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "redacted");
        var dlPath = Path.Combine(_tmpPath, "dl_note.jsonl");
        SyncState.AppendFailedRecords(
            dlPath,
            new List<(string, string)> { ("001A", "err") },
            "Account",
            requestBodies: new Dictionary<string, JsonNode?> { ["001A"] = SampleRequestBody() });

        var body = Assert.Single(ReadJsonl(dlPath))["request_body"]!.AsObject();
        var note = body[DeadLetterRedaction.NoteKey]!.GetValue<string>();
        Assert.Contains("payload redacted per DEADLETTER_PAYLOAD_MODE", note);
    }

    [Fact]
    public void ResponseErrorBodiesSurviveRedaction()
    {
        // Graph error responses carry no item content — redaction must keep the
        // error detail intact (only properties/content subtrees are hashed).
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "redacted");
        var dlPath = Path.Combine(_tmpPath, "dl_response.jsonl");
        SyncState.AppendFailedRecords(
            dlPath,
            new List<(string, string)> { ("001A", "err") },
            "Account",
            responseBodies: new Dictionary<string, JsonNode?>
            {
                ["001A"] = new JsonObject
                {
                    ["error"] = new JsonObject { ["code"] = "TooManyRequests", ["message"] = "throttled" },
                },
            });

        var response = Assert.Single(ReadJsonl(dlPath))["response_body"]!.AsObject();
        Assert.Equal("TooManyRequests", response["error"]!["code"]!.GetValue<string>());
        Assert.Equal("throttled", response["error"]!["message"]!.GetValue<string>());
        Assert.Contains("payload redacted", response[DeadLetterRedaction.NoteKey]!.GetValue<string>());
    }

    [Fact]
    public void NonObjectAndNullBodiesAreHandled()
    {
        Assert.Null(DeadLetterRedaction.RedactPayload(null));

        var envelope = DeadLetterRedaction.RedactPayload(JsonValue.Create("raw text payload"))!.AsObject();
        Assert.Equal(ExpectedHash(JsonValue.Create("raw text payload")), envelope["sha256"]!.GetValue<string>());
        Assert.Contains("payload redacted", envelope[DeadLetterRedaction.NoteKey]!.GetValue<string>());
    }

    // ── retry-failed on redacted records ─────────────────────────────────────

    [Fact]
    public async Task RetryRefetchesFromSourceSoRedactedRecordsRetryCleanly()
    {
        // 1. A failure lands in the dead-letter file in REDACTED mode.
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "redacted");
        var dlPath = Path.Combine(_tmpPath, "failed_records_TestConnector.jsonl");
        SyncState.AppendFailedRecords(
            dlPath,
            new List<(string, string)> { ("001A", "HTTP 500") },
            "Account",
            requestBodies: new Dictionary<string, JsonNode?> { ["001A"] = SampleRequestBody() });

        // 2. retry-failed reads ONLY item_id/object_type/error from the record
        //    (Commands/RetryFailed.cs) — assert a redacted record still has them.
        var entry = Assert.Single(ReadJsonl(dlPath));
        var itemId = entry["item_id"]!.GetValue<string>();
        var objectType = entry["object_type"]!.GetValue<string>();
        Assert.Equal("001A", itemId);
        Assert.Equal("Account", objectType);

        // 3. The retry path is IngestContentAsync with debug_item_id=<item_id> —
        //    it re-fetches the record from Salesforce (IterObjectChunks) and
        //    rebuilds the payload. Drive it via the test seams and prove the
        //    uploaded item carries the FULL field values, which can only have
        //    come from the source fetch (the stored record only has hashes).
        var originalGetObjectConfig = Ingest.GetObjectConfigHook;
        var originalIterChunks = Ingest.IterObjectChunksHook;
        var originalResolverFactory = Ingest.LegacyAclResolverFactory;
        var originalTransformerFactory = Ingest.TransformerFactory;
        var originalLogsDir = SyncState.LogsDir;
        try
        {
            SyncState.LogsDir = _tmpPath;
            Ingest.GetObjectConfigHook = _ => new SalesforceObjectConfig("Account", new[] { "Id" });
            Ingest.IterObjectChunksHook = (_, _, _, _) => OneChunk(new JsonObject
            {
                ["Id"] = "001A",
                ["objectType"] = "Account",
                ["Name"] = "Acme Corp (confidential)",   // re-fetched, full fidelity
                ["url"] = "https://sf.example/001A",
            });
            Ingest.LegacyAclResolverFactory = (config, _, _) => new PublicAclResolver(config);
            // Echo transformer: builds the item from the FETCHED record, so the
            // uploaded payload provably originates from the source re-read.
            Ingest.TransformerFactory = (url, schema, tenant) => new EchoTransformer(url, schema, tenant);

            var mockClient = new FakeGraphClient();
            var uploaded = new List<JsonObject>();
            mockClient.OnBatch = requests =>
            {
                uploaded.AddRange(requests);
                return requests
                    .Select(r => new JsonObject { ["id"] = r["id"]!.GetValue<string>(), ["status"] = 200 })
                    .ToList();
            };

            var baseConfig = TestFixtures.TestConfig();
            var retryConfig = Commands.CommandRegistry.ReplaceConfig(
                Commands.CommandRegistry.ReplaceConfig(baseConfig, debugItemId: itemId),
                debugObjectType: objectType);

            var stats = await Ingest.IngestContentAsync(retryConfig, mockClient, since: null);

            Assert.Equal(1, stats.SuccessCount);
            Assert.Equal(0, stats.FailedCount);
            var uploadedJson = string.Join("\n", uploaded.Select(u => u.ToJsonString()));
            // Full value present in the retried upload — proof it was re-read
            // from the source, not replayed from the redacted dead-letter record.
            Assert.Contains("Acme Corp (confidential)", uploadedJson);
        }
        finally
        {
            Ingest.GetObjectConfigHook = originalGetObjectConfig;
            Ingest.IterObjectChunksHook = originalIterChunks;
            Ingest.LegacyAclResolverFactory = originalResolverFactory;
            Ingest.TransformerFactory = originalTransformerFactory;
            SyncState.LogsDir = originalLogsDir;
        }
    }

    /// <summary>Transformer stub that copies the fetched record's fields into the item.</summary>
    private sealed class EchoTransformer : SalesforceItemTransformer
    {
        public EchoTransformer(string instanceUrl, JsonArray schema, string tenantId)
            : base(instanceUrl, schema, tenantId: tenantId)
        {
        }

        public override List<JsonObject> TransformRecord(
            JsonObject item, List<Dictionary<string, string>>? acl = null)
        {
            return new List<JsonObject>
            {
                new()
                {
                    ["id"] = item["Id"]!.GetValue<string>(),
                    ["properties"] = new JsonObject
                    {
                        ["Name"] = item["Name"]?.GetValue<string>(),
                        ["Url"] = item["url"]?.GetValue<string>(),
                    },
                    ["acl"] = new JsonArray(),
                    ["content"] = new JsonObject { ["value"] = "" },
                },
            };
        }
    }

    private static async IAsyncEnumerable<List<JsonObject>> OneChunk(params JsonObject[] records)
    {
        await Task.CompletedTask;
        yield return records.ToList();
    }

    /// <summary>Resolver stub granting public access (keeps the pipeline offline).</summary>
    private sealed class PublicAclResolver : AclResolver
    {
        public PublicAclResolver(AppConfig config) : base(config)
        {
        }

        public override Task<Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>> ResolveAsync(
            Dictionary<string, List<JsonObject>> recordsByObjectType)
        {
            var result = new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>();
            foreach (var (objectType, records) in recordsByObjectType)
            {
                result[objectType] = new Dictionary<string, List<Dictionary<string, string>>>();
                foreach (var record in records)
                {
                    result[objectType][record["Id"]!.GetValue<string>()] = new List<Dictionary<string, string>>
                    {
                        new() { ["accessType"] = "grant", ["type"] = "everyone", ["value"] = "everyone" },
                    };
                }
            }
            return Task.FromResult(result);
        }

        public override Task PrewarmCachesAsync() => Task.CompletedTask;
    }
}
