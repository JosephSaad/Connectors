// Dead-letter payload protection (DEADLETTER_PAYLOAD_MODE, Config/
// DeadLetterRedaction.cs): full mode is byte-identical to before; redacted
// mode strips record values (keeping ids, object type, error, property names,
// sizes and SHA-256 hashes) BEFORE either backend writes; an unknown mode
// fails toward redaction; and the retry-failed inputs — item_id + object_type
// driving the FindByIdDetailedAsync re-fetch, including the round-2
// oversize-inconclusive rule — survive redaction untouched.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Hdfs;

namespace HadoopConnector.Tests;

public class DeadLetterRedactionTests : IDisposable
{
    private const string Connector = "TestConnector";

    public DeadLetterRedactionTests() => DeadLetterRedaction.ResetForTests();

    public void Dispose() => DeadLetterRedaction.ResetForTests();

    private static Dictionary<string, JsonNode?> SampleRequest(string itemId) => new()
    {
        [itemId] = new JsonObject
        {
            ["id"] = itemId,
            ["properties"] = new JsonObject
            {
                ["Name"] = "Aisha Devi",
                ["Email"] = "aisha.devi@example.com",
                ["AnnualRevenue"] = 1_250_000,
            },
            ["content"] = new JsonObject { ["value"] = "Name: Aisha Devi\nEmail: ...", ["type"] = "text" },
            ["acl"] = new JsonArray(
                new JsonObject { ["type"] = "user", ["value"] = "aad-object-id-1", ["accessType"] = "grant" }),
        },
    };

    [Fact]
    public void DefaultMode_IsFull_PayloadStoredVerbatim()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, null));
        using var scope = new SyncStateScope();
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { ("C1", "HTTP 400") }, "Contact",
            SampleRequest("C1"));

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal("Aisha Devi", entry["request_body"]!["properties"]!["Name"]!.GetValue<string>());
        Assert.Null(entry["request_body"]!["redacted"]);
    }

    [Fact]
    public void RedactedMode_StripsValues_KeepsIdsTypeErrorNamesAndHashes()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redacted"));
        using var scope = new SyncStateScope();
        var request = SampleRequest("C1");
        var payloadJson = request["C1"]!.ToJsonString();
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { ("C1", "HTTP 400: schema mismatch") }, "Contact",
            request,
            new Dictionary<string, JsonNode?> { ["C1"] = new JsonObject { ["error"] = "echoed values" } });

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));

        // retry-failed inputs and diagnostics stay intact...
        Assert.Equal("C1", entry["item_id"]!.GetValue<string>());
        Assert.Equal("Contact", entry["object_type"]!.GetValue<string>());
        Assert.Equal("HTTP 400: schema mismatch", entry["error"]!.GetValue<string>());
        Assert.NotNull(entry["timestamp"]);

        // ...the payload skeleton survives (names, sizes, hashes)...
        var body = entry["request_body"]!.AsObject();
        Assert.True(body["redacted"]!.GetValue<bool>());
        Assert.Equal("C1", body["id"]!.GetValue<string>());
        Assert.Equal(
            new[] { "Name", "Email", "AnnualRevenue" },
            body["property_names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(1, body["acl_entries"]!.GetValue<int>());
        Assert.True(body["content_length"]!.GetValue<int>() > 0);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))),
            body["payload_sha256"]!.GetValue<string>());

        // ...and no record VALUE reaches the file in any form.
        var raw = File.ReadAllText(SyncState.FailedRecordsPath(Connector));
        Assert.DoesNotContain("Aisha Devi", raw);
        Assert.DoesNotContain("aisha.devi@example.com", raw);
        Assert.DoesNotContain("1250000", raw);
        Assert.DoesNotContain("aad-object-id-1", raw);
        Assert.DoesNotContain("echoed values", raw);

        var response = entry["response_body"]!.AsObject();
        Assert.True(response["redacted"]!.GetValue<bool>());
        Assert.NotNull(response["body_sha256"]);
    }

    [Fact]
    public void RedactedMode_EntriesWithoutPayloads_Unchanged()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redacted"));
        using var scope = new SyncStateScope();
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { ("WORKER_CRASH", "[Contact] boom") }, "Contact");
        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Null(entry["request_body"]);
        Assert.Equal("WORKER_CRASH", entry["item_id"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownMode_FailsClosed_ToRedacted_AndWarnsOnce()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redcated"));  // typo on purpose
        using var scope = new SyncStateScope();
        var warnings = new List<string>();
        Infrastructure.Logging.TestSink = (level, _, message) =>
        {
            if (level == Infrastructure.LogLevel.Warning)
                warnings.Add(message);
        };
        try
        {
            Assert.True(DeadLetterRedaction.RedactionEnabled);
            Assert.True(DeadLetterRedaction.RedactionEnabled);  // warned once, not per read
            SyncState.AppendFailedRecords(
                Connector, new List<(string, string)> { ("C1", "e") }, "Contact", SampleRequest("C1"));
            Assert.DoesNotContain("Aisha Devi", File.ReadAllText(SyncState.FailedRecordsPath(Connector)));
            Assert.Single(warnings, w => w.Contains("DEADLETTER_PAYLOAD_MODE"));
        }
        finally
        {
            Infrastructure.Logging.TestSink = null;
        }
    }

    [Fact]
    public void RedactRequestBody_NullAndNonObject_AreSafe()
    {
        Assert.Null(DeadLetterRedaction.RedactRequestBody(null));
        var scalar = DeadLetterRedaction.RedactRequestBody(JsonValue.Create("raw"))!.AsObject();
        Assert.True(scalar["redacted"]!.GetValue<bool>());
        Assert.NotNull(scalar["payload_sha256"]);
    }

    // ── retry-failed still works from a redacted queue ───────────────────────

    [Fact]
    public async Task RetryFailed_RefetchByIdAndObjectType_WorksFromRedactedEntries()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redacted"));
        using var scope = new SyncStateScope();

        // Dead-letter a real record id in redacted mode (as the ingest path would).
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { ("CSMALL000002", "HTTP 500: transient") },
            "Contact", SampleRequest("CSMALL000002"));
        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));

        // The retry path re-locates the record in BDH from item_id +
        // object_type alone — the redacted payload is never replayed.
        var source = new FakeBdhSource().Add(
            "Contact/dt=2026-07-15/small.jsonl",
            R2.Row("CSMALL000001") + "\n" + R2.Row("CSMALL000002") + "\n");
        var fetcher = new BdhFetcher(TestConfig.Make(), source, R2.AllowAll("Contact"));

        var find = await fetcher.FindByIdDetailedAsync(
            R2.Obj(entry["object_type"]!.GetValue<string>()),
            entry["item_id"]!.GetValue<string>());
        Assert.NotNull(find.Record);
        Assert.Equal("CSMALL000002", find.Record!.ItemId);
        Assert.False(find.Incomplete);
    }

    [Fact]
    public async Task RetryFailed_OversizeInconclusiveRule_UnaffectedByRedaction()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redacted"));
        using var scope = new SyncStateScope();
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { ("CHUGE0000007", "HTTP 500") },
            "Contact", SampleRequest("CHUGE0000007"));
        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));

        // The target hides in an oversize file: the redacted entry still drives
        // the detailed lookup, and the round-2 rule still KEEPS the entry
        // (a skipped-oversize miss is not proof the record is gone).
        var source = new FakeBdhSource();
        source.Add("Contact/dt=2026-07-15/small.jsonl", R2.Row("CSMALL000001") + "\n");
        source.Add("Contact/dt=2026-07-16/huge.jsonl",
            string.Join('\n', Enumerable.Range(0, 50).Select(i => R2.Row($"CHUGE{i:D7}"))) + "\n");
        var fetcher = new BdhFetcher(
            TestConfig.Make(maxFileBytes: 2000), source, R2.AllowAll("Contact"));

        var find = await fetcher.FindByIdDetailedAsync(
            R2.Obj(entry["object_type"]!.GetValue<string>()),
            entry["item_id"]!.GetValue<string>());
        Assert.Null(find.Record);
        Assert.True(find.SkippedOversize);
        Assert.False(RetryFailed.ShouldDropMissing(find));  // entry kept
    }
}
