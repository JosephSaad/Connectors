// Unit tests for the webhook building blocks: signature validation, event
// parsing, and debounce/coalesce.

using System.Text;
using ClarizenConnector.Webhook;

namespace ClarizenConnector.Tests;

public class SignatureValidatorTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"events":[{"entityType":"Task","id":"/Task/1","operation":"update"}]}""");

    [Fact]
    public void ValidHexSignature_Accepted()
    {
        var validator = new SignatureValidator("s3cr3t");
        var sig = validator.ComputeHex(Body);
        Assert.True(validator.IsValid(Body, sig));
        Assert.True(validator.IsValid(Body, "sha256=" + sig));
        Assert.True(validator.IsValid(Body, sig.ToUpperInvariant()));
    }

    [Fact]
    public void ValidBase64Signature_Accepted()
    {
        var validator = new SignatureValidator("s3cr3t");
        var base64 = Convert.ToBase64String(Convert.FromHexString(validator.ComputeHex(Body)));
        Assert.True(validator.IsValid(Body, base64));
    }

    [Fact]
    public void WrongSecret_Rejected()
    {
        var signed = new SignatureValidator("real-secret").ComputeHex(Body);
        var other = new SignatureValidator("other-secret");
        Assert.False(other.IsValid(Body, signed));
    }

    [Fact]
    public void TamperedBody_Rejected()
    {
        var validator = new SignatureValidator("s3cr3t");
        var sig = validator.ComputeHex(Body);
        var tampered = Encoding.UTF8.GetBytes("""{"events":[{"entityType":"Task","id":"/Task/999","operation":"delete"}]}""");
        Assert.False(validator.IsValid(tampered, sig));
    }

    [Fact]
    public void MissingOrGarbageSignature_Rejected()
    {
        var validator = new SignatureValidator("s3cr3t");
        Assert.False(validator.IsValid(Body, null));
        Assert.False(validator.IsValid(Body, ""));
        Assert.False(validator.IsValid(Body, "   "));
        Assert.False(validator.IsValid(Body, "not-a-signature!!"));
        Assert.False(validator.IsValid(Body, "zzzz"));
    }

    [Fact]
    public void EmptySecret_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SignatureValidator(""));
    }

    [Fact]
    public void TryDecode_HexAndBase64()
    {
        Assert.NotNull(SignatureValidator.TryDecode("00ff"));
        Assert.NotNull(SignatureValidator.TryDecode(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
        Assert.Null(SignatureValidator.TryDecode("!!!!"));
    }
}

public class WebhookEventParserTests
{
    [Fact]
    public void Parse_EventsArray_UpsertAndDelete()
    {
        var events = WebhookEventParser.Parse("""
            {"events": [
                {"entityType": "Task", "id": "/Task/123", "operation": "update"},
                {"entityType": "Project", "id": "/Project/9", "operation": "deleted"}
            ]}
            """);
        Assert.Equal(2, events.Count);
        Assert.Equal(new WebhookEvent("Task", "123", ChangeKind.Upsert), events[0]);
        Assert.Equal(new WebhookEvent("Project", "9", ChangeKind.Delete), events[1]);
        Assert.Equal("Task_123", events[0].ItemId);
    }

    [Fact]
    public void Parse_BareSingleObject()
    {
        var events = WebhookEventParser.Parse(
            """{"objectType": "Issue", "entityId": "55", "action": "created"}""");
        var evt = Assert.Single(events);
        Assert.Equal(new WebhookEvent("Issue", "55", ChangeKind.Upsert), evt);
    }

    [Fact]
    public void Parse_TopLevelArray_AndFieldAliases()
    {
        var events = WebhookEventParser.Parse(
            """[{"type": "Risk", "objectId": "/Risk/7", "changeType": "modified"}]""");
        Assert.Equal(new WebhookEvent("Risk", "7", ChangeKind.Upsert), Assert.Single(events));
    }

    [Theory]
    [InlineData("create", ChangeKind.Upsert)]
    [InlineData("updated", ChangeKind.Upsert)]
    [InlineData("modify", ChangeKind.Upsert)]
    [InlineData("delete", ChangeKind.Delete)]
    [InlineData("removed", ChangeKind.Delete)]
    public void ClassifyKind_KnownOperations(string op, ChangeKind expected)
    {
        Assert.Equal(expected, WebhookEventParser.ClassifyKind(op));
    }

    [Fact]
    public void ClassifyKind_Unknown_IsNull()
    {
        Assert.Null(WebhookEventParser.ClassifyKind("frobnicate"));
        Assert.Null(WebhookEventParser.ClassifyKind(null));
    }

    [Fact]
    public void Parse_MalformedOrEmpty_YieldsNoEvents()
    {
        Assert.Empty(WebhookEventParser.Parse("not json"));
        Assert.Empty(WebhookEventParser.Parse(""));
        Assert.Empty(WebhookEventParser.Parse("null"));
        Assert.Empty(WebhookEventParser.Parse("""{"events": []}"""));
        // Missing id / unknown operation entries are dropped.
        Assert.Empty(WebhookEventParser.Parse("""{"entityType": "Task", "operation": "update"}"""));
        Assert.Empty(WebhookEventParser.Parse("""{"entityType": "Task", "id": "1", "operation": "frob"}"""));
    }

    [Fact]
    public void Parse_MixedValidAndInvalid_KeepsValid()
    {
        var events = WebhookEventParser.Parse("""
            {"events": [
                {"entityType": "Task", "id": "1", "operation": "update"},
                {"garbage": true},
                {"entityType": "Task", "id": "2", "operation": "delete"}
            ]}
            """);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void NormalizeLocalId()
    {
        Assert.Equal("123", WebhookEventParser.NormalizeLocalId("/Task/123"));
        Assert.Equal("55", WebhookEventParser.NormalizeLocalId("55"));
        Assert.Equal("9", WebhookEventParser.NormalizeLocalId("/Project/9/"));
    }

    // ── Id sanitization (post-auth injection) ────────────────────────────────
    // The local id flows into a CZQL "WHERE SYSID = {id}" clause and a Graph
    // DELETE URL, so anything that is not a plain token must be dropped.

    [Theory]
    [InlineData("123 OR 1=1")]
    [InlineData("1; DROP TABLE Tasks")]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("..%2f..%2fadmin")]
    [InlineData("1'or'1'='1")]
    [InlineData("a b")]
    public void Parse_InjectionShapedIds_Rejected(string badId)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            entityType = "Task",
            id = badId,
            operation = "update",
        });
        Assert.Empty(WebhookEventParser.Parse(body));
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("/Task/123", "123")]
    [InlineData("abc_1.2-3", "abc_1.2-3")]
    public void Parse_PlainTokenIds_Accepted(string rawId, string expectedLocalId)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            entityType = "Task",
            id = rawId,
            operation = "update",
        });
        var evt = Assert.Single(WebhookEventParser.Parse(body));
        Assert.Equal(expectedLocalId, evt.LocalId);
    }

    [Fact]
    public void IsValidLocalId_TokenRules()
    {
        Assert.True(WebhookEventParser.IsValidLocalId("1234567"));
        Assert.True(WebhookEventParser.IsValidLocalId("T-9.b_c"));
        Assert.False(WebhookEventParser.IsValidLocalId(""));
        Assert.False(WebhookEventParser.IsValidLocalId(".."));
        Assert.False(WebhookEventParser.IsValidLocalId("1 OR 1=1"));
        Assert.False(WebhookEventParser.IsValidLocalId("x/y"));
    }
}

public class EventDebouncerTests
{
    private static readonly DateTime T0 = new(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DuplicateEvents_CoalesceToOne()
    {
        var debouncer = new EventDebouncer(TimeSpan.FromMilliseconds(2000));
        Assert.False(debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Upsert), T0));
        Assert.True(debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Upsert), T0.AddMilliseconds(100)));
        Assert.True(debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Upsert), T0.AddMilliseconds(200)));
        Assert.Equal(1, debouncer.PendingCount);

        // Not ready until the window elapses since the LAST offer.
        Assert.Empty(debouncer.DrainReady(T0.AddMilliseconds(2100)));
        var ready = debouncer.DrainReady(T0.AddMilliseconds(2201));
        Assert.Single(ready);
        Assert.Equal(0, debouncer.PendingCount);
    }

    [Fact]
    public void LastWriterWins_DeleteAfterUpsert()
    {
        var debouncer = new EventDebouncer(TimeSpan.FromMilliseconds(1000));
        debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Upsert), T0);
        debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Delete), T0.AddMilliseconds(500));
        var ready = debouncer.DrainReady(T0.AddMilliseconds(1600));
        Assert.Equal(ChangeKind.Delete, Assert.Single(ready).Kind);
    }

    [Fact]
    public void LastWriterWins_UpsertAfterDelete()
    {
        var debouncer = new EventDebouncer(TimeSpan.FromMilliseconds(1000));
        debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Delete), T0);
        debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Upsert), T0.AddMilliseconds(300));
        var ready = debouncer.DrainReady(T0.AddMilliseconds(1400));
        Assert.Equal(ChangeKind.Upsert, Assert.Single(ready).Kind);
    }

    [Fact]
    public void DistinctEntities_AreIndependent()
    {
        var debouncer = new EventDebouncer(TimeSpan.FromMilliseconds(1000));
        debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Upsert), T0);
        debouncer.Offer(new WebhookEvent("Task", "2", ChangeKind.Upsert), T0.AddMilliseconds(1200));
        // Only entity 1's window has elapsed at T0+1500.
        var ready = debouncer.DrainReady(T0.AddMilliseconds(1500));
        Assert.Equal("Task_1", Assert.Single(ready).ItemId);
        Assert.Equal(1, debouncer.PendingCount);
    }

    [Fact]
    public void DrainAll_FlushesRegardlessOfWindow()
    {
        var debouncer = new EventDebouncer(TimeSpan.FromHours(1));
        debouncer.Offer(new WebhookEvent("Task", "1", ChangeKind.Upsert), T0);
        debouncer.Offer(new WebhookEvent("Task", "2", ChangeKind.Delete), T0);
        Assert.Equal(2, debouncer.DrainAll().Count);
        Assert.Equal(0, debouncer.PendingCount);
    }
}
