// StressHarness3PartCTests.cs
// ---------------------------
// Round-3 PART C — one fresh adversarial dimension not exercised by the rest of
// the round-3 suite (StressHarness3Tests.cs covers A1-A5, B, and a combined 3x
// soak with tracing OFF and the assertion clock at "now"). This file adds:
//
//   C1. Clock-skew BOUNDARY math on the client_assertion under concurrency.
//       BuildAssertion takes an injectable `now`; drive it from many threads at
//       extreme wall-clock values (year 2000, live now, year 2999) and prove the
//       nbf/iat/exp arithmetic is thread-STABLE and correct at every boundary
//       (nbf = now-skew, exp = now+lifetime, exp-nbf = skew+lifetime), the RS256
//       signature verifies against the cert every time, and jti never collides.
//
//   C2. OTel tracing ENABLED (a live ActivityListener registered, so
//       ActivitySource.StartActivity returns real spans) racing the enterprise
//       dead-letter path. Each concurrent producer opens its own trace scope —
//       whose correlation id IS the W3C trace id — and writes a redacted
//       dead-letter record inside it. Assert every persisted record carries
//       EXACTLY its own producer's correlation id (AsyncLocal isolation, no
//       cross-thread bleed), all trace ids are unique, no torn lines, and no
//       payload value survives. This is the "tracing enabled adds a race"
//       dimension the round-3 brief calls out, driven fully offline.
//
// Deterministic by construction: `now` is injected (no timing flakiness) and
// correlation ids are captured per-write and compared to the persisted value.
// No loopback listeners are bound here, so these classes stay OUT of the
// "LoopbackWebhook" collection and run in parallel with everything else.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using Xunit;

namespace SeismicConnector.Tests;

// ── C1. Clock-skew boundary on the client_assertion under concurrency ─────────

public class Stress3_ClockSkewAssertionConcurrency
{
    private static void VerifyOne(
        string jwt, X509Certificate2 cert, string clientId, string audience,
        DateTimeOffset now, List<string> problems)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) { problems.Add($"parts={parts.Length}"); return; }

        JsonObject header, claims;
        try { header = Stress3.Segment(jwt, 0); claims = Stress3.Segment(jwt, 1); }
        catch (Exception ex) { problems.Add($"unparseable: {ex.Message}"); return; }

        if (header["alg"]?.GetValue<string>() != "RS256") problems.Add("alg");
        var expectedX5t = CertificateCredential.Base64Url(SHA256.HashData(cert.RawData));
        if (header["x5t#S256"]?.GetValue<string>() != expectedX5t) problems.Add("x5t");
        if (claims["aud"]?.GetValue<string>() != audience) problems.Add("aud");
        if (claims["iss"]?.GetValue<string>() != clientId) problems.Add("iss");
        if (claims["sub"]?.GetValue<string>() != clientId) problems.Add("sub");

        // Exact skew arithmetic at this (possibly extreme) wall-clock value.
        var expectedNbf = (now - CertificateCredential.ClockSkew).ToUnixTimeSeconds();
        var expectedExp = (now + CertificateCredential.AssertionLifetime).ToUnixTimeSeconds();
        var nbf = claims["nbf"]!.GetValue<long>();
        var iat = claims["iat"]!.GetValue<long>();
        var exp = claims["exp"]!.GetValue<long>();
        if (nbf != expectedNbf) problems.Add($"nbf {nbf}!={expectedNbf}");
        if (iat != expectedNbf) problems.Add($"iat {iat}!={expectedNbf}");
        if (exp != expectedExp) problems.Add($"exp {exp}!={expectedExp}");
        if (exp - nbf != (long)(CertificateCredential.ClockSkew + CertificateCredential.AssertionLifetime).TotalSeconds)
            problems.Add($"window {exp - nbf}");
        if (exp <= nbf) problems.Add("exp<=nbf");

        using var pub = cert.GetRSAPublicKey()!;
        var ok = pub.VerifyData(
            Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]),
            Stress3.FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!ok) problems.Add("signature");
    }

    [Fact]
    public async Task AssertionExpiryMath_AtClockBoundaries_UnderConcurrency_AlwaysExactAndVerifiable()
    {
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        const string clientId = "app-client-id";
        const string audience = "https://login.microsoftonline.com/tenant/oauth2/v2.0/token";

        // Extreme, fixed wall-clock instants that stress the unix-seconds math on
        // both sides of "now" — plus live now for good measure.
        var clocks = new[]
        {
            DateTimeOffset.Parse("2000-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow,
            DateTimeOffset.Parse("2999-12-31T23:59:59Z", System.Globalization.CultureInfo.InvariantCulture),
        };

        const int builders = 20;
        const int perBuilder = 300;
        var jtis = new ConcurrentBag<string>();
        var problems = new ConcurrentBag<string>();
        var buildErrors = new ConcurrentBag<string>();
        using var ready = new Barrier(builders);
        var sw = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, builders).Select(b => Task.Run(() =>
        {
            ready.SignalAndWait();
            var local = new List<string>();
            for (var i = 0; i < perBuilder; i++)
            {
                var now = clocks[(b + i) % clocks.Length];
                try
                {
                    var jwt = CertificateCredential.BuildAssertion(cert, clientId, audience, now);
                    VerifyOne(jwt, cert, clientId, audience, now, local);
                    jtis.Add(Stress3.Segment(jwt, 1)["jti"]!.GetValue<string>());
                }
                catch (Exception ex) { buildErrors.Add(ex.ToString()); }
            }
            foreach (var p in local) problems.Add(p);
        })));
        sw.Stop();

        var total = builders * perBuilder;
        Assert.Empty(buildErrors);
        Assert.Empty(problems);
        Assert.Equal(total, jtis.Count);
        Assert.Equal(total, jtis.Distinct().Count());   // jti unique under concurrency

        StressLog.Record("R3C-CLOCKSKEW",
            $"builders={builders} assertions={total} clock_boundaries={clocks.Length} " +
            $"math_or_sig_problems={problems.Count} build_errors={buildErrors.Count} " +
            $"jti_unique={jtis.Distinct().Count()}/{total} " +
            $"rate={total / sw.Elapsed.TotalSeconds:F0}/s");
    }
}

// ── C2. Tracing ENABLED — per-thread correlation-id isolation under load ──────

public class Stress3_TracingCorrelationIsolation : IDisposable
{
    public Stress3_TracingCorrelationIsolation()
    {
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);
        Tracing.ResetCorrelationForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);
        Tracing.ResetCorrelationForTests();
    }

    private const string Secret = "TRACE-SECRET-PII-must-never-survive";

    [Fact]
    public async Task ConcurrentRedactedDeadLetter_WithLiveSpans_EachRecordKeepsItsOwnCorrelationId()
    {
        // Register a live listener so ActivitySource.StartActivity returns real
        // spans — i.e. tracing is genuinely "on", the code path that mints a
        // correlation id from the W3C trace id and pushes it onto the AsyncLocal.
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Tracing.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, "redacted");
        using var state = new TempStateDir();
        const string connectorId = "R3CTrace";
        var path = SyncState.FailedRecordsPath(connectorId);

        const int producers = 16;
        const int perProducer = 200;
        // item_id -> the correlation id the producing scope actually held.
        var expected = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var traceIds = new ConcurrentBag<string>();
        var exceptions = new ConcurrentBag<string>();
        var spanLess = 0;
        using var ready = new Barrier(producers);
        var sw = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            ready.SignalAndWait();
            for (var i = 0; i < perProducer; i++)
            {
                try
                {
                    // Each write happens inside its own trace scope; the scope's
                    // correlation id is the live span's W3C trace id and rides the
                    // AsyncLocal to the dead-letter record built below.
                    using var scope = Tracing.BeginNamedScope("webhook.handle", connectorId, "webhook");
                    if (Activity.Current is null) Interlocked.Increment(ref spanLess);
                    var id = $"p{p}-i{i}";
                    expected[id] = scope.CorrelationId;
                    traceIds.Add(scope.CorrelationId);
                    SyncState.AppendFailedRecords(
                        path,
                        new List<(string, string)> { (id, $"HTTP 503 {id}") },
                        "ContentItem",
                        requestBodies: new Dictionary<string, JsonNode?>
                        {
                            [id] = new JsonObject
                            {
                                ["id"] = id,
                                ["content"] = new JsonObject { ["value"] = $"{Secret} {id}" },
                                ["properties"] = new JsonObject { ["title"] = $"{Secret} {id}" },
                            },
                        });
                }
                catch (Exception ex) { exceptions.Add(ex.ToString()); }
            }
        })));
        sw.Stop();

        var total = producers * perProducer;
        Assert.Empty(exceptions);
        Assert.Equal(0, spanLess);                         // listener really made spans live

        // No torn lines; every record present exactly once.
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);   // redaction held under load
        var rawLines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(total, rawLines);

        var records = SyncState.ReadFailedRecords(connectorId);
        Assert.Equal(total, records.Count);

        // The core isolation invariant: every persisted record's correlation id
        // equals the id its OWN producer scope held — no cross-thread bleed.
        var bleeds = new List<string>();
        var distinctSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            var id = r["item_id"]!.GetValue<string>();
            var corr = r["correlation_id"]?.GetValue<string>();
            distinctSeen.Add(corr ?? "<null>");
            if (corr is null) { bleeds.Add($"{id}: no correlation_id"); continue; }
            if (corr.Length != 32) bleeds.Add($"{id}: not a W3C trace id ({corr})");
            if (!expected.TryGetValue(id, out var want) || corr != want)
                bleeds.Add($"{id}: corr={corr} want={(expected.TryGetValue(id, out var w) ? w : "<none>")}");
        }
        Assert.Empty(bleeds);

        // Every span produced a unique trace id (no id reuse across concurrent scopes).
        Assert.Equal(total, traceIds.Distinct().Count());
        Assert.Equal(total, distinctSeen.Count);

        StressLog.Record("R3C-TRACE-ISOLATION",
            $"producers={producers} records={records.Count} live_spans={total - spanLess} " +
            $"unique_correlation_ids={distinctSeen.Count} cross_thread_bleeds={bleeds.Count} " +
            $"secret_survived=no torn_lines=0 rate={total / sw.Elapsed.TotalSeconds:F0}/s");
    }
}
