// AdversarialProbeRound3.cs
// -------------------------
// INDEPENDENT adversarial probes written by the round-3 VERIFIER (not the stress
// agent). Purpose: try to DISPROVE that the two round-3 "flawed test assertion"
// fixes are complete and that the src they exonerate is actually correct.
//
//   Probe 1  Dead-letter redaction is what strips PII (the A4 structural proof is
//            NOT vacuous): the identical concurrent producer flow leaks PII in
//            `full` mode, and a NESTED-object property value (a shape A4's
//            scalar-only payload never exercises) is fully stripped in `redacted`
//            mode — proving the "no raw properties object" structural invariant
//            really implies "no property VALUE, however nested, survives".
//
//   Probe 2  The soak's admissible event-id set is COMPLETE and still has teeth:
//            every id EventLogSink can actually emit (Initialize→1000,
//            Shutdown→1001, Info→1100, Warning→2000, Error→3000) is admitted, and
//            a fabricated garbage id is still rejected by the same predicate.
//
// These do NOT weaken or replace the stress agent's tests; they run alongside.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class AdversarialProbeRound3DeadLetter : IDisposable
{
    private const string Connector = "VerifierProbeDeadLetter";

    public AdversarialProbeRound3DeadLetter() => DeadLetterRedaction.ResetForTests();
    public void Dispose() => DeadLetterRedaction.ResetForTests();

    // Non-vacuity: in `full` mode the SAME shape A4 uses stores PII verbatim, so
    // the redacted-mode assertions in A4 would genuinely fail if redaction broke.
    [Fact]
    public void FullMode_StoresPiiVerbatim_ProvingRedactedAssertionsHaveTeeth()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "full"));
        using var scope = new SyncStateScope();
        const string name = "Priya-VERIFIER-Kaur";
        const string email = "priya.verifier@example.invalid";
        const string id = "C000000000";

        SyncState.AppendFailedRecords(
            Connector,
            new[] { (id, "HTTP 400") },
            "Contact",
            new Dictionary<string, JsonNode?>
            {
                [id] = new JsonObject
                {
                    ["id"] = id,
                    ["properties"] = new JsonObject { ["Name"] = name, ["Email"] = email },
                    ["content"] = new JsonObject { ["value"] = $"Name: {name}", ["type"] = "text" },
                    ["acl"] = new JsonArray(new JsonObject { ["value"] = $"aad-{id}" }),
                },
            });

        var raw = File.ReadAllText(SyncState.FailedRecordsPath(Connector));
        // full mode keeps the raw payload — so the redacted-mode scan is meaningful.
        Assert.Contains(name, raw, StringComparison.Ordinal);
        Assert.Contains(email, raw, StringComparison.Ordinal);
        var body = JsonNode.Parse(raw.Trim())!.AsObject()["request_body"]!.AsObject();
        Assert.True(body.ContainsKey("properties"));   // raw values object present in full mode
    }

    // The structural invariant A4 relies on ("no raw properties object survives")
    // must imply "no property VALUE survives, however deeply nested". A4 only uses
    // scalar property values; probe a NESTED-object value + array value.
    [Fact]
    public void RedactedMode_StripsNestedAndArrayPropertyValues_NoSentinelSurvives()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redacted"));
        using var scope = new SyncStateScope();
        const string nested = "NESTED-PII-SENTINEL-4477";
        const string arr = "ARRAY-PII-SENTINEL-9931";
        const string id = "CDEEP00001";

        SyncState.AppendFailedRecords(
            Connector,
            new[] { (id, "HTTP 400") },
            "Contact",
            new Dictionary<string, JsonNode?>
            {
                [id] = new JsonObject
                {
                    ["id"] = id,
                    ["properties"] = new JsonObject
                    {
                        // A property whose VALUE is itself an object holding PII.
                        ["Address"] = new JsonObject { ["street"] = nested, ["zip"] = "90210" },
                        // A property whose VALUE is an array holding PII.
                        ["Aliases"] = new JsonArray(arr, "second"),
                    },
                },
            });

        var raw = File.ReadAllText(SyncState.FailedRecordsPath(Connector));
        Assert.DoesNotContain(nested, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(arr, raw, StringComparison.Ordinal);

        var body = JsonNode.Parse(raw.Trim())!.AsObject()["request_body"]!.AsObject();
        Assert.False(body.ContainsKey("properties"));   // structural invariant
        // Only the NAMES survive.
        Assert.Equal(
            new[] { "Address", "Aliases" },
            body["property_names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    // Fail-closed on a shape the redactor does not special-case: `properties` that
    // is NOT an object (an array). The whole values blob must be dropped, never
    // copied through — an unknown shape must lose data, not leak it.
    [Fact]
    public void RedactedMode_PropertiesNotAnObject_DropsEntireBlob()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redacted"));
        using var scope = new SyncStateScope();
        const string sentinel = "MALFORMED-PROPS-PII-7788";
        const string id = "CMAL000001";

        SyncState.AppendFailedRecords(
            Connector,
            new[] { (id, "HTTP 400") },
            "Contact",
            new Dictionary<string, JsonNode?>
            {
                [id] = new JsonObject
                {
                    ["id"] = id,
                    ["properties"] = new JsonArray(new JsonObject { ["x"] = sentinel }),
                },
            });

        var raw = File.ReadAllText(SyncState.FailedRecordsPath(Connector));
        Assert.DoesNotContain(sentinel, raw, StringComparison.Ordinal);
        var body = JsonNode.Parse(raw.Trim())!.AsObject()["request_body"]!.AsObject();
        Assert.False(body.ContainsKey("properties"));
        Assert.False(body.ContainsKey("property_names"));  // non-object → no names either
    }
}

public class AdversarialProbeRound3EventLog : IDisposable
{
    public AdversarialProbeRound3EventLog() => EventLogSink.ResetForTests();
    public void Dispose() => EventLogSink.ResetForTests();

    // The soak admits {1000,1001,1100,2000,3000}. Prove that set is exactly the
    // set of ids the sink can EMIT: drive every write path and confirm each id is
    // admitted by the soak's own predicate. If the sink could emit an id the soak
    // excludes, the soak would false-pass a real regression; this catches that.
    [Fact]
    public void EverySinkEmittedId_IsAdmittedBySoakPredicate()
    {
        static bool Admitted(int id) =>
            id is EventLogSink.EventIdError or EventLogSink.EventIdWarning
                or EventLogSink.EventIdInfo or EventLogSink.EventIdLifecycleStart
                or EventLogSink.EventIdLifecycleStop;

        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", "info"));
        var writer = new ConcurrentEventLogWriter();
        EventLogSink.OverrideWriter = writer;

        EventLogSink.Initialize();                                   // → 1000
        EventLogSink.Mirror(LogLevel.Error, "probe", "e");           // → 3000
        EventLogSink.Mirror(LogLevel.Warning, "probe", "w");         // → 2000
        EventLogSink.Mirror(LogLevel.Info, "probe", "i");            // → 1100 (level=info)
        EventLogSink.Mirror(LogLevel.Debug, "probe", "d");           // suppressed (no entry)
        EventLogSink.Shutdown();                                     // → 1001

        var ids = writer.Entries.Select(e => e.EventId).ToList();
        // All five documented ids appeared, and Debug produced nothing.
        Assert.Equal(
            new[]
            {
                EventLogSink.EventIdLifecycleStart, EventLogSink.EventIdError,
                EventLogSink.EventIdWarning, EventLogSink.EventIdInfo,
                EventLogSink.EventIdLifecycleStop,
            }.OrderBy(x => x).ToArray(),
            ids.OrderBy(x => x).ToArray());
        Assert.All(ids, id => Assert.True(Admitted(id), $"sink emitted id {id} the soak would reject"));

        // Teeth: the predicate still rejects a fabricated garbage id.
        Assert.False(Admitted(9999));
        Assert.False(Admitted(0));
    }
}
