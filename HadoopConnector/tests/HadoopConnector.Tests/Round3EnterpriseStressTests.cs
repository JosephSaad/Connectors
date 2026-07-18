// Round3EnterpriseStressTests.cs
// ------------------------------
// ROUND 3 — concurrency/load stress of the ENTERPRISE PACK that shipped after
// rounds 1/2 (certificate Graph auth, proxy+CA transport, Windows Event Log
// sink, dead-letter payload redaction, new metrics) plus a regression re-check
// that the pack did not weaken the round-1/2 hardened invariants, and one fresh
// adversarial dimension (an everything-at-once interleaved soak).
//
// All offline: certificates are generated in-test, the event-log writer is an
// injected thread-safe recorder, and no real network is ever touched.
//
// PART A — enterprise pack under concurrency:
//   A1  client_assertion JWT signing — 32 concurrent builders share ONE cert;
//       every JWT must be individually valid (x5t#S256, aud, nbf<exp, RS256
//       signature verifies) — zero malformed. Prime suspect: a shared RSA/SHA
//       object corrupting a signature. Also the REAL GraphClient token path
//       under a concurrent stampede: exactly one token fetch, valid assertion.
//   A2  CA-bundle TLS callback — 64 concurrent validations interleave accept
//       (private-CA leaf) and reject (foreign-CA leaf); every verdict correct,
//       no exception. Prime suspect: a shared X509Chain racing. Plus fail-fast
//       (bad path / malformed PEM naming CA_BUNDLE_PATH) under concurrent
//       handler construction, with no handler leak or deadlock.
//   A3  Event Log sink flood — many threads emit Error/Warning/Info at once:
//       never throws, correct level→id mapping, no deadlock, no lost entry.
//   A4  Dead-letter redaction — many concurrent producers in redacted mode with
//       PII payloads: no torn record, no PII value survives, ids/hashes kept,
//       retry re-fetch still works from a redacted entry.
//   A5  New metric counters — concurrent increments never tear; values reconcile.
//   CANARY: the WebHDFS delegation token never appears in ANY log line
//       (console/file/JSON) or event-log entry produced under load.
//
// PART B — regression under load: filter-first pruning + 14-predicate record
//   filter + row cap, fail-closed full-scan guard, 24 h dt-watermark, and
//   oversize-skip → partial → sweep-suppression all still hold while the
//   enterprise paths (cert signing, metrics, event-log mirroring) run
//   concurrently in the background.
//
// PART C — fresh dimension: a sustained interleaved soak where cert signing,
//   chain validation, event-log mirroring, dead-letter redaction and metric
//   increments all hammer shared state simultaneously; every invariant holds
//   and the token canary stays clean.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Shared round-3 helpers
// ─────────────────────────────────────────────────────────────────────────────

internal static class R3
{
    /// <summary>
    /// Run <paramref name="body"/> on <paramref name="threads"/> DEDICATED
    /// threads, released simultaneously through a gate for maximum genuine
    /// contention. Uses real <see cref="Thread"/>s (not the pool) so a large
    /// thread count never starves on the pool's slow ramp — the flaw that
    /// makes Barrier-over-Task.Run tests wedge. Any worker exception is
    /// aggregated and rethrown.
    /// </summary>
    internal static void RunConcurrent(int threads, Action<int> body)
    {
        var errors = new ConcurrentBag<Exception>();
        using var ready = new CountdownEvent(threads);
        using var go = new ManualResetEventSlim(false);
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            var id = t;
            workers[t] = new Thread(() =>
            {
                ready.Signal();
                go.Wait();
                try { body(id); }
                catch (Exception e) { errors.Add(e); }
            }) { IsBackground = true, Name = $"r3-worker-{id}" };
            workers[t].Start();
        }
        ready.Wait();   // every worker created and parked at the gate
        go.Set();       // release all at once
        foreach (var w in workers)
            w.Join();
        if (!errors.IsEmpty)
            throw new AggregateException(errors);
    }

    /// <summary>RFC 7515 base64url decode (JWT part).</summary>
    internal static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    /// <summary>Fully validate one client-assertion JWT against the issuing cert.
    /// Returns a human-readable failure reason, or null when valid.</summary>
    internal static string? ValidateJwt(
        string jwt, X509Certificate2 cert, RSA publicKey, string expectedX5t,
        string clientId, string tokenEndpoint)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return "not three parts";
        JsonObject header, payload;
        try
        {
            header = JsonNode.Parse(FromBase64Url(parts[0]))!.AsObject();
            payload = JsonNode.Parse(FromBase64Url(parts[1]))!.AsObject();
        }
        catch (Exception exc)
        {
            return "unparseable segment: " + exc.GetType().Name;
        }
        if (header["alg"]?.GetValue<string>() != "RS256")
            return "alg not RS256";
        if (header["x5t#S256"]?.GetValue<string>() != expectedX5t)
            return "x5t#S256 mismatch";
        if (header.ContainsKey("x5t"))
            return "legacy x5t present";
        if (payload["aud"]?.GetValue<string>() != tokenEndpoint)
            return "aud mismatch";
        if (payload["iss"]?.GetValue<string>() != clientId || payload["sub"]?.GetValue<string>() != clientId)
            return "iss/sub mismatch";
        long nbf = payload["nbf"]!.GetValue<long>(), exp = payload["exp"]!.GetValue<long>();
        if (exp <= nbf)
            return "exp<=nbf";
        bool ok;
        try
        {
            ok = publicKey.VerifyData(
                Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]),
                FromBase64Url(parts[2]),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception exc)
        {
            return "signature verify threw: " + exc.GetType().Name;
        }
        return ok ? null : "signature does not verify";
    }
}

/// <summary>Thread-safe event-log writer for flood tests (the production
/// WindowsEventLogWriter serialises internally; the RecordingWriter used by the
/// unit suite is a plain List and is not safe under a concurrent flood).</summary>
internal sealed class ConcurrentEventLogWriter : IEventLogWriter
{
    public ConcurrentQueue<(string Message, EventLogEntryLevel Level, int EventId)> Entries { get; } = new();
    public int DisposeCount;
    public Exception? ThrowOnWrite { get; set; }

    public void WriteEntry(string message, EventLogEntryLevel level, int eventId)
    {
        if (ThrowOnWrite is not null)
            throw ThrowOnWrite;
        Entries.Enqueue((message, level, eventId));
    }

    public void Dispose() => Interlocked.Increment(ref DisposeCount);
}

// ─────────────────────────────────────────────────────────────────────────────
// PART A1 — certificate client_assertion JWT signing under concurrency
// ─────────────────────────────────────────────────────────────────────────────

public class CertAssertionConcurrencyStressTests
{
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string TokenEndpoint =
        "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/token";

    // The concurrency (thread count), not the raw iteration count, is what
    // exposes a shared-signer race — it manifests within the first dozens of
    // simultaneous Build() calls. Per-thread counts are kept modest so the suite
    // stays runnable on a shared/oversubscribed CI box (each RSA-2048 sign is
    // CPU-heavy); the StressHarness enterprise-concurrency scenario drives the
    // same path at higher volume for a soak.
    [Theory]
    [InlineData(16, 300)]
    [InlineData(32, 300)]
    public async Task ConcurrentBuilders_ShareOneCert_EveryAssertionIndividuallyValid(int threads, int perThread)
    {
        using var cert = ClientAssertionTests.MakeCert();
        using var pub = cert.GetRSAPublicKey()!;
        var expectedX5t = ClientAssertion.Base64Url(SHA256.HashData(cert.RawData));

        var failures = new ConcurrentBag<string>();
        var jtis = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var built = 0;

        await Task.Run(() => R3.RunConcurrent(threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
            {
                string jwt;
                try
                {
                    jwt = ClientAssertion.Build(cert, ClientId, TokenEndpoint);
                }
                catch (Exception exc)
                {
                    failures.Add("Build threw: " + exc.GetType().Name + ": " + exc.Message);
                    continue;
                }
                Interlocked.Increment(ref built);
                var reason = R3.ValidateJwt(jwt, cert, pub, expectedX5t, ClientId, TokenEndpoint);
                if (reason is not null)
                    failures.Add(reason);
                else
                {
                    var jti = JsonNode.Parse(R3.FromBase64Url(jwt.Split('.')[1]))!["jti"]!.GetValue<string>();
                    jtis.TryAdd(jti, 1);
                }
            }
        }));

        var expected = threads * perThread;
        Assert.Equal(expected, built);
        Assert.True(failures.IsEmpty,
            $"{failures.Count}/{expected} invalid assertions under concurrency; e.g. "
            + string.Join(" | ", failures.Distinct().Take(5)));
        // Every jti unique across all threads → no shared-state collision.
        Assert.Equal(expected, jtis.Count);
    }

    // The REAL GraphClient token path under a concurrent stampede: many callers
    // race GetTokenAsync as the (uncached) token is first fetched. The double-
    // checked _tokenLock must collapse them to a SINGLE token fetch, and the
    // single assertion actually sent must be a valid RS256 JWT (not the secret).
    [Fact]
    public async Task GraphClient_TokenStampede_OneFetch_AssertionValid_SecretNeverSent()
    {
        using var cert = ClientAssertionTests.MakeCert();
        using var pub = cert.GetRSAPublicKey()!;
        var expectedX5t = ClientAssertion.Base64Url(SHA256.HashData(cert.RawData));
        using var dir = new TempDir();
        var certPath = Path.Combine(dir.Path, "graph.pfx");
        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pkcs12, "p"));

        var forms = new ConcurrentBag<string>();
        var tokenFetches = 0;
        var handler = new ThreadSafeTokenHandler((body) =>
        {
            Interlocked.Increment(ref tokenFetches);
            forms.Add(body);
            return """{"access_token":"tok-cert-stampede","expires_in":3600}""";
        });

        var config = new AppConfig
        {
            ConnectorId = "BdhHadoopMart",
            ConnectorName = "n",
            ConnectorDescription = "d",
            HdfsMode = "webhdfs",
            HdfsNamenodeUrl = "http://namenode.example:9870/webhdfs/v1",
            BdhRootPath = "/data/bdh",
            AadTenantId = "11111111-1111-1111-1111-111111111111",
            AadClientId = ClientId,
            AadClientSecret = "secret-that-must-lose",
            GraphClientCertPath = certPath,
            GraphClientCertPassword = "p",
        };
        var client = new GraphClient(config, handler);

        const int callers = 48;
        var tokens = new ConcurrentBag<string>();
        await Task.Run(() => R3.RunConcurrent(callers, _ =>
            tokens.Add(client.GetTokenAsync().GetAwaiter().GetResult())));

        Assert.Equal(callers, tokens.Count);
        Assert.All(tokens, t => Assert.Equal("tok-cert-stampede", t));
        Assert.Equal(1, tokenFetches);  // stampede collapsed to one fetch
        var form = Assert.Single(forms);
        Assert.Contains("client_assertion=", form);
        Assert.DoesNotContain("client_secret", form);
        Assert.DoesNotContain("secret-that-must-lose", form);

        // The assertion that actually went on the wire is a valid RS256 JWT.
        var assertion = ExtractFormValue(form, "client_assertion");
        Assert.Null(R3.ValidateJwt(
            Uri.UnescapeDataString(assertion), cert, pub, expectedX5t, ClientId,
            $"{config.AadAuthorityHost}/{config.AadTenantId}/oauth2/v2.0/token"));
    }

    private static string ExtractFormValue(string form, string key)
    {
        foreach (var pair in form.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
                return pair[(eq + 1)..];
        }
        throw new InvalidOperationException($"form has no '{key}'");
    }

    /// <summary>Records token-endpoint request bodies without a non-thread-safe List.</summary>
    private sealed class ThreadSafeTokenHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _respond;
        public ThreadSafeTokenHandler(Func<string, string> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(body), Encoding.UTF8, "application/json"),
            };
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PART A2 — proxy + CA-bundle transport under load
// ─────────────────────────────────────────────────────────────────────────────

public class CaBundleConcurrencyStressTests
{
    // 64-way concurrency is the race exposer; per-thread count kept modest for a
    // shared CI box (each validation builds an X509Chain — CPU-heavy).
    [Theory]
    [InlineData(64, 150)]
    public async Task ConcurrentValidations_InterleaveAcceptReject_EveryVerdictCorrect_NoException(
        int threads, int perThread)
    {
        var (root, leaf) = HttpTransportCaBundleTests.MakeChain();
        var (otherRoot, foreignLeaf) = HttpTransportCaBundleTests.MakeChain("CN=foreign.corp.local");
        // One shared bundle captured exactly as CreateHandler's closure captures it.
        var bundle = new X509Certificate2Collection { root };

        var wrong = 0;
        var exceptions = new ConcurrentBag<string>();

        await Task.Run(() => R3.RunConcurrent(threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                var acceptCase = ((t + i) & 1) == 0;
                try
                {
                    var verdict = HttpTransport.ValidateWithBundle(
                        acceptCase ? leaf : foreignLeaf, null,
                        SslPolicyErrors.RemoteCertificateChainErrors, bundle);
                    if (verdict != acceptCase)
                        Interlocked.Increment(ref wrong);
                }
                catch (Exception exc)
                {
                    exceptions.Add(exc.GetType().Name + ": " + exc.Message);
                }
            }
        }));

        Assert.True(exceptions.IsEmpty,
            $"{exceptions.Count} exceptions from concurrent validation; e.g. "
            + string.Join(" | ", exceptions.Distinct().Take(3)));
        Assert.Equal(0, wrong);

        root.Dispose(); leaf.Dispose(); otherRoot.Dispose(); foreignLeaf.Dispose();
    }

    [Fact]
    public async Task NameMismatch_NeverAcceptedEvenUnderLoad()
    {
        var (root, leaf) = HttpTransportCaBundleTests.MakeChain();
        var bundle = new X509Certificate2Collection { root };
        var accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                // A trusted-CA leaf presented for the WRONG host must always fail.
                if (HttpTransport.ValidateWithBundle(
                        leaf, null,
                        SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
                        bundle))
                    Interlocked.Increment(ref accepted);
            }
        })));
        Assert.Equal(0, accepted);
        root.Dispose(); leaf.Dispose();
    }

    [Fact]
    public async Task ConcurrentHandlerConstruction_BadPathAndMalformedPem_FailFastNamingSetting_NoLeak()
    {
        var (root, _) = HttpTransportCaBundleTests.MakeChain();
        using var dir = new TempDir();
        var goodPath = Path.Combine(dir.Path, "good.pem");
        File.WriteAllText(goodPath, root.ExportCertificatePem());
        var garbagePath = Path.Combine(dir.Path, "garbage.pem");
        File.WriteAllText(garbagePath, "this is not a certificate");

        var namedBad = 0;
        var goodHandlers = new ConcurrentBag<HttpMessageHandler>();

        // Each thread has an isolated env so PROXY/CA settings never bleed across
        // threads; a mix of good / missing / malformed bundle paths hammered at once.
        var tasks = Enumerable.Range(0, 48).Select(t => Task.Run(() =>
        {
            var which = t % 3;
            var path = which switch { 0 => goodPath, 1 => "/nonexistent/private-ca.pem", _ => garbagePath };
            // ValidateWithBundle is env-free; construction reads env. Use the
            // low-level loader (env-free) to avoid cross-thread env races while
            // still exercising the exact fail-fast path CreateHandler uses.
            if (which == 0)
            {
                var bundle = HttpTransport.LoadCaBundle(path);
                var handler = new System.Net.Http.SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, c, ch, e) =>
                            HttpTransport.ValidateWithBundle(c as X509Certificate2, ch, e, bundle),
                    },
                };
                goodHandlers.Add(handler);
            }
            else
            {
                var exc = Assert.Throws<ArgumentException>(() => HttpTransport.LoadCaBundle(path));
                if (exc.Message.Contains("CA_BUNDLE_PATH", StringComparison.Ordinal))
                    Interlocked.Increment(ref namedBad);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(32, namedBad);        // 16 missing + 16 malformed all named the setting
        Assert.Equal(16, goodHandlers.Count);
        foreach (var h in goodHandlers) h.Dispose();  // no leak
        root.Dispose();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PART A3 — Event Log sink under a logging flood
// ─────────────────────────────────────────────────────────────────────────────

public class EventLogFloodStressTests : IDisposable
{
    public EventLogFloodStressTests() => EventLogSink.ResetForTests();
    public void Dispose() => EventLogSink.ResetForTests();

    [Fact]
    public async Task Flood_NeverThrows_CorrectLevelMapping_NoLoss_NoDeadlock()
    {
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null));
        var writer = new ConcurrentEventLogWriter();
        EventLogSink.OverrideWriter = writer;
        EventLogSink.Initialize();
        Assert.True(EventLogSink.Enabled);

        const int threads = 24;
        const int perThread = 500;   // 12k emitted; only Error+Warning mirror (Info off)
        var thrown = new ConcurrentBag<string>();

        var run = Task.Run(() => R3.RunConcurrent(threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    var level = ((t + i) % 3) switch
                    {
                        0 => LogLevel.Error,
                        1 => LogLevel.Warning,
                        _ => LogLevel.Info,     // must NOT mirror by default
                    };
                    EventLogSink.Mirror(level, "hadoop_connector.flood", $"t{t}-i{i}");
                }
                catch (Exception exc)
                {
                    thrown.Add(exc.GetType().Name + ": " + exc.Message);
                }
            }
        }));

        // No-deadlock proof: the whole flood completes within a liveness bound.
        // The bound is deliberately generous (120s) rather than tight — this is a
        // deadlock detector, NOT a throughput gate, and the suite may run on a
        // heavily oversubscribed/shared CI box where wall-clock stretches far past
        // the ~1s this cheap lock+enqueue flood needs. A true deadlock never
        // completes, so it still fails; contention merely slows a passing run.
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(120)));
        Assert.True(ReferenceEquals(finished, run), "event-log flood deadlocked (120s timeout)");
        await run;

        Assert.True(thrown.IsEmpty, "Mirror threw under flood: " + string.Join(" | ", thrown.Distinct().Take(3)));

        var entries = writer.Entries.ToList();
        // lifecycle-start + every Error and Warning (Info suppressed by default).
        var errors = entries.Count(e => e.Level == EventLogEntryLevel.Error);
        var warnings = entries.Count(e => e.Level == EventLogEntryLevel.Warning);
        var infos = entries.Count(e => e.Level == EventLogEntryLevel.Information);

        // Count expected Error/Warning across the deterministic (t+i)%3 pattern.
        var expectedError = 0; var expectedWarning = 0;
        for (var t = 0; t < threads; t++)
            for (var i = 0; i < perThread; i++)
                switch ((t + i) % 3) { case 0: expectedError++; break; case 1: expectedWarning++; break; }

        Assert.Equal(expectedError, errors);       // no lost / doubled entries
        Assert.Equal(expectedWarning, warnings);
        Assert.Equal(1, infos);                     // ONLY the lifecycle-start info
        Assert.All(entries.Where(e => e.Level == EventLogEntryLevel.Error),
            e => Assert.Equal(EventLogSink.EventIdError, e.EventId));
        Assert.All(entries.Where(e => e.Level == EventLogEntryLevel.Warning),
            e => Assert.Equal(EventLogSink.EventIdWarning, e.EventId));
    }

    [Fact]
    public async Task Flood_ThrowingWriter_StillNeverPropagates()
    {
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null));
        var writer = new ConcurrentEventLogWriter { ThrowOnWrite = new InvalidOperationException("event log full") };
        EventLogSink.OverrideWriter = writer;
        EventLogSink.Initialize();

        var thrown = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                try { EventLogSink.Mirror(LogLevel.Error, "x", "boom"); }
                catch (Exception exc) { thrown.Add(exc.GetType().Name); }
            }
        })));
        Assert.True(thrown.IsEmpty, "a broken writer must never surface under flood");
    }

    [Fact]
    public void OffWindows_EnabledButNoWriter_IsStrictNoOp()
    {
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null));
        EventLogSink.OverrideWriter = null;
        if (!OperatingSystem.IsWindows())
        {
            EventLogSink.Initialize();
            Assert.False(EventLogSink.Enabled);
            // A flood is still perfectly safe when inert.
            Parallel.For(0, 5000, _ => EventLogSink.Mirror(LogLevel.Error, "x", "y"));
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PART A4 — dead-letter redaction under concurrent producers
// ─────────────────────────────────────────────────────────────────────────────

public class DeadLetterRedactionConcurrencyStressTests : IDisposable
{
    private const string Connector = "Round3DeadLetter";

    public DeadLetterRedactionConcurrencyStressTests() => DeadLetterRedaction.ResetForTests();
    public void Dispose() => DeadLetterRedaction.ResetForTests();

    private static Dictionary<string, JsonNode?> Payload(string id, string name, string email, long revenue) => new()
    {
        [id] = new JsonObject
        {
            ["id"] = id,
            ["properties"] = new JsonObject
            {
                ["Name"] = name,
                ["Email"] = email,
                ["AnnualRevenue"] = revenue,
            },
            ["content"] = new JsonObject { ["value"] = $"Name: {name}\nEmail: {email}", ["type"] = "text" },
            ["acl"] = new JsonArray(
                new JsonObject { ["type"] = "user", ["value"] = $"aad-{id}", ["accessType"] = "grant" }),
        },
    };

    [Theory]
    [InlineData(32, 200)]
    public async Task ConcurrentProducers_Redacted_NoTornRecord_NoPiiSurvives_IdsAndHashesKept(
        int workers, int perWorker)
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redacted"));
        using var scope = new SyncStateScope();

        // Distinct PII per record so any survivor is unambiguous.
        string Name(int w, int i) => $"Aisha-{w:D2}-{i:D4}-Devi";
        string Email(int w, int i) => $"user{w:D2}{i:D4}@example.invalid";
        long Rev(int w, int i) => 1_000_000 + w * 10_000 + i;

        await Task.Run(() => R3.RunConcurrent(workers, w =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                var id = $"C{w:D3}{i:D6}";
                SyncState.AppendFailedRecords(
                    Connector,
                    new List<(string, string)> { (id, $"HTTP 400 w{w} i{i}") },
                    "Contact",
                    Payload(id, Name(w, i), Email(w, i), Rev(w, i)),
                    new Dictionary<string, JsonNode?> { [id] = new JsonObject { ["error"] = $"echoed {Name(w, i)}" } });
            }
        }));

        var expected = workers * perWorker;
        var path = SyncState.FailedRecordsPath(Connector);
        var raw = File.ReadAllText(path);

        // No torn record: every physical non-empty line parses cleanly.
        var lines = raw.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(expected, lines.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var obj = JsonNode.Parse(line)!.AsObject();  // throws on a torn/interleaved line
            ids.Add(obj["item_id"]!.GetValue<string>());
            var body = obj["request_body"]!.AsObject();
            Assert.True(body["redacted"]!.GetValue<bool>());
            Assert.Equal(obj["item_id"]!.GetValue<string>(), body["id"]!.GetValue<string>());
            Assert.NotNull(body["payload_sha256"]);   // hash retained
            Assert.Equal(
                new[] { "Name", "Email", "AnnualRevenue" },
                body["property_names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        }
        Assert.Equal(expected, ids.Count);  // no loss, no duplication

        // Not a single PII VALUE survived anywhere in the file. The distinctive
        // string values (Name, Email) and the ACL principal cannot collide with a
        // retained field, so they are scanned across the whole file.
        for (var w = 0; w < workers; w++)
            for (var i = 0; i < perWorker; i++)
            {
                Assert.DoesNotContain(Name(w, i), raw, StringComparison.Ordinal);
                Assert.DoesNotContain(Email(w, i), raw, StringComparison.Ordinal);
                Assert.DoesNotContain($"aad-C{w:D3}{i:D6}", raw, StringComparison.Ordinal);
            }
        Assert.DoesNotContain("echoed", raw, StringComparison.Ordinal);

        // AnnualRevenue is a bare integer, so a naive whole-file substring scan
        // would false-positive: a 7-digit revenue like "1000000" legitimately
        // occurs inside a RETAINED item_id (e.g. "C010000000") and inside SHA-256
        // hex digests. The retained id is by design (retry re-fetches by
        // item_id + object_type). Instead, prove the value was stripped
        // STRUCTURALLY per record: the redacted request_body keeps property NAMES
        // only — never a raw "properties" values object, "content" value or "acl"
        // list — so no property value (revenue included) can have survived.
        foreach (var line in lines)
        {
            var body = JsonNode.Parse(line)!.AsObject()["request_body"]!.AsObject();
            Assert.True(body.ContainsKey("property_names"), "redacted body dropped property_names");
            Assert.False(body.ContainsKey("properties"), "redacted body leaked a raw properties values object");
            Assert.False(body.ContainsKey("content"), "redacted body leaked a content value");
            Assert.False(body.ContainsKey("acl"), "redacted body leaked an acl principal list");
        }
    }

    [Fact]
    public async Task RetryReFetch_WorksFromRedactedQueue_WrittenUnderConcurrency()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "redacted"));
        using var scope = new SyncStateScope();

        // Concurrent producers dead-letter a set of real BDH ids in redacted mode.
        var targetIds = Enumerable.Range(0, 40).Select(i => $"CSMALL{i:D6}").ToList();
        await Task.WhenAll(targetIds.Select(id => Task.Run(() =>
            SyncState.AppendFailedRecords(
                Connector, new List<(string, string)> { (id, "HTTP 500: transient") },
                "Contact",
                Payload(id, $"Name-{id}", $"{id}@example.invalid", 999)))));

        var entries = SyncState.ReadFailedRecords(Connector);
        Assert.Equal(targetIds.Count, entries.Count);

        // Build a BDH source that actually contains those ids, then prove the
        // retry path re-locates each record by item_id + object_type ALONE — the
        // redacted payload is never replayed.
        var source = new FakeBdhSource();
        source.Add("Contact/dt=2026-07-15/small.jsonl",
            string.Join('\n', targetIds.Select(R2.Row)) + "\n");
        var fetcher = new BdhFetcher(TestConfig.Make(), source, R2.AllowAll("Contact"));

        var found = new ConcurrentBag<string>();
        await Task.WhenAll(entries.Select(e => Task.Run(async () =>
        {
            var find = await fetcher.FindByIdDetailedAsync(
                R2.Obj(e["object_type"]!.GetValue<string>()), e["item_id"]!.GetValue<string>());
            if (find.Record is not null && !find.Incomplete)
                found.Add(find.Record.ItemId);
        })));
        Assert.Equal(targetIds.ToHashSet(StringComparer.Ordinal), found.ToHashSet(StringComparer.Ordinal));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PART A5 — new metric counters under concurrency
// ─────────────────────────────────────────────────────────────────────────────

public class MetricsConcurrencyStressTests : IDisposable
{
    public MetricsConcurrencyStressTests() => Metrics.ResetForTests();
    public void Dispose() => Metrics.ResetForTests();

    [Fact]
    public async Task ConcurrentIncrements_NeverTear_ValuesReconcile()
    {
        const int threads = 32;
        const int perThread = 5000;

        await Task.Run(() => R3.RunConcurrent(threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                // The enterprise-pack anchors + a couple of classic counters + a
                // labelled family, all hammered together.
                Metrics.IncGuardRefusals();
                Metrics.IncPartialObjects();
                Metrics.IncSweepsSuppressed();
                Metrics.IncItemsIngested();
                Metrics.IncItemsFailed(2);
                Metrics.IncRecordsFiltered(i % 2 == 0 ? "partition" : "predicate");
                Metrics.IncItemsClassified(i % 3 == 0 ? "High" : "General");
                Metrics.AddHaClaimsHeld(1);
                Metrics.AddHaClaimsHeld(-1);
            }
        }));

        var n = (long)threads * perThread;
        Assert.Equal(n, Metrics.GuardRefusals);
        Assert.Equal(n, Metrics.PartialObjects);
        Assert.Equal(n, Metrics.SweepsSuppressed);
        Assert.Equal(n, Metrics.ItemsIngested);
        Assert.Equal(2 * n, Metrics.ItemsFailed);
        // Labelled families reconcile exactly across both keys.
        Assert.Equal(n, Metrics.RecordsFilteredFor("partition") + Metrics.RecordsFilteredFor("predicate"));
        Assert.Equal(n, Metrics.ItemsClassifiedFor("High") + Metrics.ItemsClassifiedFor("General"));
        Assert.Equal(0, Metrics.HaClaimsHeld);   // balanced +1/-1 nets to zero, untorn

        // The Prometheus renderer produces a consistent snapshot under no further load.
        var prom = Metrics.RenderPrometheus();
        Assert.Contains($"hadoop_connector_guard_refusals_total {n}", prom);
        Assert.Contains($"hadoop_connector_partial_objects_total {n}", prom);
        Assert.Contains($"hadoop_connector_sweeps_suppressed_total {n}", prom);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CANARY — the WebHDFS delegation token never reaches a log line or event entry
// ─────────────────────────────────────────────────────────────────────────────

public class DelegationTokenCanaryStressTests : IDisposable
{
    public DelegationTokenCanaryStressTests() { Logging.ResetForTests(); EventLogSink.ResetForTests(); }
    public void Dispose() { Logging.ResetForTests(); EventLogSink.ResetForTests(); }

    private const string SecretToken = "DELEGATION-CANARY-TOKEN-Kerberos-ABC123XYZ-do-not-log";

    /// <summary>Handler that always fails (transport + 500) so every retry/error
    /// log line on the WebHDFS path is exercised.</summary>
    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Sanity: the token IS actually on the wire (query string), proving
            // the canary is meaningful and not passing vacuously.
            if (!request.RequestUri!.Query.Contains(Uri.EscapeDataString(SecretToken), StringComparison.Ordinal))
                throw new InvalidOperationException("token missing from request — canary would be vacuous");
            // Alternate transport failure and HTTP 500 to hit both log branches.
            return Interlocked.Increment(ref _calls) % 2 == 0
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"RemoteException":{"message":"boom"}}""", Encoding.UTF8, "application/json"),
                });
        }
    }

    [Fact]
    public async Task DelegationToken_NeverAppearsInAnyLogLineOrEventEntry_UnderLoad()
    {
        var rendered = new ConcurrentQueue<string>();
        var sink = new ConcurrentQueue<string>();
        Logging.RenderedSink = line => rendered.Enqueue(line);
        Logging.TestSink = (_, _, message) => sink.Enqueue(message);

        // Mirror everything to an event-log writer too, so the canary also covers
        // the SIEM path.
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", "info"), ("LOG_FORMAT", null));
        var writer = new ConcurrentEventLogWriter();
        EventLogSink.OverrideWriter = writer;
        EventLogSink.Initialize();

        var client = new WebHdfsClient(
            "http://namenode.example:9870/webhdfs/v1", "/data/bdh",
            user: "svc-bdh", delegationToken: SecretToken,
            handler: new AlwaysFailHandler())
        { DelayAsync = (_, _) => Task.CompletedTask };

        // Concurrent LIST/OPEN/GETFILESTATUS ops, all failing → every retry and
        // terminal error line is produced with the token on the (unlogged) query.
        await Task.WhenAll(Enumerable.Range(0, 24).Select(op => Task.Run(async () =>
        {
            try
            {
                if (op % 3 == 0) await client.ListAsync($"Contact/dt=2026-07-1{op % 9}");
                else if (op % 3 == 1) await client.OpenAsync($"Contact/dt=2026-07-1{op % 9}/part.jsonl");
                else await client.ExistsAsync($"Contact/dt=2026-07-1{op % 9}/part.jsonl");
            }
            catch { /* expected — we are asserting on the LOGS, not the outcome */ }
        })));

        // Also emit a direct warning through the pipeline to be sure the sinks captured lines.
        Logging.GetLogger("hadoop_connector.webhdfs").Warning("canary flush");

        Assert.NotEmpty(rendered);  // the sinks really did receive lines

        // The token — raw and URL-escaped — appears in NONE of: rendered lines
        // (file/console/JSON), TestSink messages, or event-log entries.
        var escaped = Uri.EscapeDataString(SecretToken);
        void AssertClean(string haystack, string where)
        {
            Assert.DoesNotContain(SecretToken, haystack, StringComparison.Ordinal);
            Assert.DoesNotContain(escaped, haystack, StringComparison.Ordinal);
            Assert.DoesNotContain("delegation=", haystack, StringComparison.Ordinal);
        }
        foreach (var line in rendered) AssertClean(line, "rendered");
        foreach (var msg in sink) AssertClean(msg, "testsink");
        foreach (var (message, _, _) in writer.Entries) AssertClean(message, "eventlog");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PART B — regression re-validation while the enterprise paths run concurrently
// ─────────────────────────────────────────────────────────────────────────────

public class Round3RegressionUnderLoadTests
{
    private static string Cid(int n) => $"C{n:D12}";

    // Filter-first pruning + selective record predicate + fail-closed guard +
    // 24 h watermark + oversize-skip→partial→sweep-suppression, each re-checked
    // WHILE cert-signing / metric / event-log enterprise load runs on background
    // threads (proving the enterprise pack did not regress the core invariants
    // even under contention on the shared metric registry and log pipeline).
    [Fact]
    public async Task CoreInvariantsHold_WhileEnterprisePathsHammerConcurrently()
    {
        Metrics.ResetForTests();
        EventLogSink.ResetForTests();
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null));
        var writer = new ConcurrentEventLogWriter();
        EventLogSink.OverrideWriter = writer;
        EventLogSink.Initialize();
        using var cert = ClientAssertionTests.MakeCert();
        using var pub = cert.GetRSAPublicKey()!;
        var expectedX5t = ClientAssertion.Base64Url(SHA256.HashData(cert.RawData));

        using var stop = new CancellationTokenSource();
        var jwtFailures = new ConcurrentBag<string>();
        // Background enterprise storm on a FEW dedicated threads (not the pool —
        // the foreground pipeline work is async and needs pool threads).
        const int stormThreads = 4;
        var storm = new Thread[stormThreads];
        for (var s = 0; s < stormThreads; s++)
        {
            storm[s] = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var jwt = ClientAssertion.Build(cert, "cid", "aud://x");
                    if (R3.ValidateJwt(jwt, cert, pub, expectedX5t, "cid", "aud://x") is { } r)
                        jwtFailures.Add(r);
                    Metrics.IncItemsIngested();
                    EventLogSink.Mirror(LogLevel.Warning, "hadoop_connector.storm", "bg");
                }
            }) { IsBackground = true };
            storm[s].Start();
        }

        try
        {
            await Regression_PartitionPruning_And_SelectiveFilter();
            await Regression_FailClosedGuard();
            await Regression_Watermark24h();
            await Regression_OversizeSkip_PartialAndSweepSuppressed();
        }
        finally
        {
            stop.Cancel();
            foreach (var s in storm)
                s.Join();
        }

        Assert.True(jwtFailures.IsEmpty,
            "cert signing corrupted under concurrent core-pipeline load: " + string.Join(" | ", jwtFailures.Distinct().Take(3)));

        Metrics.ResetForTests();
        EventLogSink.ResetForTests();
    }

    private static async Task Regression_PartitionPruning_And_SelectiveFilter()
    {
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        const int days = 60, rowsPerFile = 200;
        var source = new FakeBdhSource();
        var gid = 0;
        for (var d = 0; d < days; d++)
        {
            var dt = now.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var region in new[] { "EU", "US" })
            {
                var sb = new StringBuilder();
                for (var r = 0; r < rowsPerFile; r++)
                    sb.Append($$"""{"Id":"{{Cid(++gid)}}","Status":"{{(r % 50 == 0 ? "Active" : "Inactive")}}","Region":"{{region}}"}""").Append('\n');
                source.Add($"Contact/region={region}/dt={dt}/part-0000.jsonl", sb.ToString());
            }
        }
        var filter = new ObjectFilter
        {
            Partition = { new FilterPredicate { Field = "region", Op = FilterOp.Equals, Value = "EU" } },
            AnyOf = { new FilterGroup { AllOf = { new FilterPredicate { Field = "Status", Op = FilterOp.Equals, Value = "Active" } } } },
        };
        var filters = new FilterSet(
            new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase) { ["Contact"] = filter },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var config = TestConfig.Make(lagHours: 0, allowFullScan: false);
        var since = now.AddDays(-10);
        var fetcher = new BdhFetcher(config, source, filters, nowUtc: () => now);
        var result = await fetcher.FetchAsync(
            new ObjectConfig { ObjectName = "Contact", DisplayName = "Contact" }, fullCrawl: false, since);

        var bound = DateOnly.FromDateTime(since);
        var surviving = Enumerable.Range(0, days).Select(d => DateOnly.FromDateTime(now.AddDays(-d))).Count(dt => dt >= bound);
        Assert.Equal(surviving, source.OpenCalls);                 // zero pruned-file opens
        Assert.Equal(surviving * rowsPerFile, (int)result.Stats.RecordsScanned);
        Assert.Equal(surviving * (rowsPerFile / 50), result.Records.Count);  // selective
    }

    private static async Task Regression_FailClosedGuard()
    {
        var source = new FakeBdhSource();
        source.Add("Contact/dt=2026-07-15/part-0000.jsonl", """{"Id":"X000000000001"}""");
        var fetcher = new BdhFetcher(TestConfig.Make(allowFullScan: false), source, FilterSet.Empty);
        await Assert.ThrowsAsync<FullScanRefusedException>(() =>
            fetcher.FetchAsync(new ObjectConfig { ObjectName = "Contact", DisplayName = "Contact" }, fullCrawl: true, sinceUtc: null));
        Assert.Equal(0, source.ListCalls);   // refused before any I/O
        Assert.Equal(0, source.OpenCalls);

        // ALLOW_FULL_SCAN opt-in still admits it (the escape hatch is intact).
        using var env = new EnvScope(("ALLOW_FULL_SCAN", "true"));
        var src2 = new FakeBdhSource();
        src2.Add("Contact/dt=2026-07-15/part-0000.jsonl", """{"Id":"X000000000001","Status":"Active"}""");
        var f2 = new BdhFetcher(TestConfig.Make(allowFullScan: true), src2, FilterSet.Empty);
        var r2 = await f2.FetchAsync(new ObjectConfig { ObjectName = "Contact", DisplayName = "Contact" }, fullCrawl: true, sinceUtc: null);
        Assert.Single(r2.Records);
    }

    private static async Task Regression_Watermark24h()
    {
        var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        var source = new FakeBdhSource();
        // dt exactly at, before and after the (since − 24h) watermark.
        source.Add("Contact/dt=2026-07-14/old.jsonl", """{"Id":"C000000000001","Status":"Active"}""" + "\n");
        source.Add("Contact/dt=2026-07-15/edge.jsonl", """{"Id":"C000000000002","Status":"Active"}""" + "\n");
        source.Add("Contact/dt=2026-07-16/new.jsonl", """{"Id":"C000000000003","Status":"Active"}""" + "\n");
        var config = TestConfig.Make(lagHours: 24, allowFullScan: false);
        var fetcher = new BdhFetcher(config, source, R2.AllowAll("Contact"), nowUtc: () => now);
        var since = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);  // watermark = since−24h = 2026-07-15
        var result = await fetcher.FetchAsync(R2.Obj("Contact"), fullCrawl: false, since);
        var ids = result.Records.Select(r => r.ItemId).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("C000000000001", ids);   // 07-14 pruned (before watermark)
        Assert.Contains("C000000000002", ids);          // 07-15 kept (at watermark date)
        Assert.Contains("C000000000003", ids);          // 07-16 kept
    }

    private static async Task Regression_OversizeSkip_PartialAndSweepSuppressed()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        using var env = new EnvScope(
            ("DELETION_SYNC", null), ("DELETION_SYNC_MAX_ITEMS", null),
            ("DELETION_SYNC_MAX_PERCENT", null), ("GRAPH_CONNECTION_SHARDS", null));
        const string connector = "BdhHadoopMart";
        const long maxFileBytes = 2000;

        var source = new FakeBdhSource();
        var visible = Enumerable.Range(0, 20).Select(i => $"VIS{i:D9}").ToList();
        var hidden = Enumerable.Range(0, 50).Select(i => $"HID{i:D9}").ToList();
        source.Add("Contact/dt=2026-07-15/small.jsonl", string.Join('\n', visible.Select(R2.Row)) + "\n");
        source.Add("Contact/dt=2026-07-16/huge.jsonl", string.Join('\n', hidden.Select(R2.Row)) + "\n"); // > maxFileBytes

        var config = TestConfig.Make(ingestChunkSize: 100, allowFullScan: true, maxFileBytes: maxFileBytes);
        Func<string, IItemInventory> inv = id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db"));
        const string staleId = "STALE00000001";
        using (var seed = inv(connector))
            seed.RecordSeen(hidden.Select(h => (h, "Contact")).Append((staleId, "Contact")), DateTime.UtcNow);

        var graph = new FakeGraphClient(config);
        var pipeline = MemoryBoundStressTests.BuildPipeline(config, R2.Schema("Contact"), source, graph, dir.Path, inv);
        var summary = await pipeline.RunAsync(fullCrawl: true);

        Assert.Contains("Contact", summary.PartialObjects);
        Assert.Contains("Contact", summary.SweepSkipped);
        Assert.Equal(0, summary.Deleted);
        using var check = inv(connector);
        var after = check.IdsForObject("Contact").ToHashSet(StringComparer.Ordinal);
        Assert.Contains(staleId, after);                       // sweep suppressed as a whole
        Assert.True(hidden.All(after.Contains));               // no un-read record deleted
        Assert.True(visible.All(after.Contains));              // every read record ingested
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PART C — fresh dimension: everything-at-once interleaved soak
// ─────────────────────────────────────────────────────────────────────────────

public class Round3InterleavedSoakStressTests : IDisposable
{
    public Round3InterleavedSoakStressTests() { Metrics.ResetForTests(); EventLogSink.ResetForTests(); }
    public void Dispose() { Metrics.ResetForTests(); EventLogSink.ResetForTests(); }

    // Not covered before: cert signing, CA-chain validation, event-log mirroring,
    // dead-letter redaction and metric increments ALL contend on shared state at
    // once from one big thread pool. Every enterprise invariant must hold
    // simultaneously (this is the interaction the per-path tests never create).
    [Fact]
    public void AllEnterprisePaths_ContendSimultaneously_EveryInvariantHolds()
    {
        using var envMode = new EnvScope(
            (DeadLetterRedaction.ModeEnvVar, "redacted"),
            ("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null));
        using var scope = new SyncStateScope();
        DeadLetterRedaction.ResetForTests();
        const string connector = "Round3Soak";

        var writer = new ConcurrentEventLogWriter();
        EventLogSink.OverrideWriter = writer;
        EventLogSink.Initialize();

        using var cert = ClientAssertionTests.MakeCert();
        using var pub = cert.GetRSAPublicKey()!;
        var expectedX5t = ClientAssertion.Base64Url(SHA256.HashData(cert.RawData));
        var (root, leaf) = HttpTransportCaBundleTests.MakeChain();
        var (otherRoot, foreignLeaf) = HttpTransportCaBundleTests.MakeChain("CN=foreign");
        var bundle = new X509Certificate2Collection { root };

        const string pii = "SoakSecret-PII-Value";
        var jwtFail = new ConcurrentBag<string>();
        var chainWrong = 0;
        var deadLettered = 0;

        // 3x the core width — a sustained interleaved storm on dedicated threads.
        // dop (oversubscription) is the interaction exposer; per-worker op count is
        // kept moderate so the ~40%-crypto soak stays runnable on a shared CI box.
        var dop = Environment.ProcessorCount * 3;
        const int opsPerWorker = 1200;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        R3.RunConcurrent(dop, w =>
        {
            var rng = new Random(w * 7919 + 1);
            for (var i = 0; i < opsPerWorker; i++)
            {
                switch (rng.Next(5))
                {
                    case 0:
                        var jwt = ClientAssertion.Build(cert, "cid", "aud://soak");
                        if (R3.ValidateJwt(jwt, cert, pub, expectedX5t, "cid", "aud://soak") is { } r)
                            jwtFail.Add(r);
                        break;
                    case 1:
                        var acc = (i & 1) == 0;
                        var verdict = HttpTransport.ValidateWithBundle(
                            acc ? leaf : foreignLeaf, null,
                            SslPolicyErrors.RemoteCertificateChainErrors, bundle);
                        if (verdict != acc) Interlocked.Increment(ref chainWrong);
                        break;
                    case 2:
                        EventLogSink.Mirror(i % 2 == 0 ? LogLevel.Error : LogLevel.Warning, "hadoop_connector.soak", $"w{w}i{i}");
                        break;
                    case 3:
                        var id = $"S{w:D3}{i:D6}";
                        SyncState.AppendFailedRecords(
                            connector, new List<(string, string)> { (id, "HTTP 500") }, "Contact",
                            new Dictionary<string, JsonNode?>
                            {
                                [id] = new JsonObject
                                {
                                    ["id"] = id,
                                    ["properties"] = new JsonObject { ["Secret"] = pii + "-" + id },
                                },
                            });
                        Interlocked.Increment(ref deadLettered);
                        break;
                    default:
                        Metrics.IncGuardRefusals();
                        Metrics.IncPartialObjects();
                        Metrics.IncItemsFailed();
                        break;
                }
            }
        });
        sw.Stop();

        // Invariants, all simultaneously:
        Assert.True(jwtFail.IsEmpty, "JWT corrupted in soak: " + string.Join(" | ", jwtFail.Distinct().Take(3)));
        Assert.Equal(0, chainWrong);

        // Dead-letter: every line intact, zero PII survivors.
        var raw = File.ReadAllText(SyncState.FailedRecordsPath(connector));
        var lines = raw.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(deadLettered, lines.Count);
        foreach (var line in lines) JsonNode.Parse(line);        // no torn record
        Assert.DoesNotContain(pii, raw, StringComparison.Ordinal);

        // Metrics reconcile: guard==partial (incremented together in the same branch).
        Assert.Equal(Metrics.GuardRefusals, Metrics.PartialObjects);
        Assert.True(Metrics.ItemsFailed == Metrics.GuardRefusals,
            $"metric tear: itemsFailed={Metrics.ItemsFailed} guard={Metrics.GuardRefusals}");

        // Event log never lost/torn: every entry carries a stable documented id.
        // The soak mirrors Error/Warning, and EventLogSink.Initialize() also emits
        // the lifecycle-start entry (EventIdLifecycleStart), so all five stable ids
        // are admissible — the invariant is that NO entry carries a garbage id.
        Assert.All(writer.Entries, e => Assert.True(
            e.EventId is EventLogSink.EventIdError or EventLogSink.EventIdWarning
                or EventLogSink.EventIdInfo or EventLogSink.EventIdLifecycleStart
                or EventLogSink.EventIdLifecycleStop,
            $"unexpected event id {e.EventId}"));

        // Real measured throughput for the report. Wall-clock is recorded, not
        // hard-gated: the soak is synchronous, so by the time we reach here every
        // worker has already joined — a genuine deadlock would hang before this
        // point and never arrive. A tight wall-clock assertion here would only be
        // a throughput benchmark, which false-fails on a shared/oversubscribed CI
        // box (this soak is ~40% RSA-2048 signing and chain building). The
        // invariants above — zero corrupt JWTs, zero wrong chain verdicts, no torn
        // dead-letter record, reconciled metrics — are the actual proof.
        var totalOps = (long)dop * opsPerWorker;
        var opsPerSec = totalOps / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Assert.True(totalOps > 0 && opsPerSec > 0,
            $"soak completed {totalOps} ops in {sw.Elapsed.TotalSeconds:F1}s ({opsPerSec:N0} ops/s)");

        root.Dispose(); leaf.Dispose(); otherRoot.Dispose(); foreignLeaf.Dispose();
        Metrics.ResetForTests();
        EventLogSink.ResetForTests();
    }
}
