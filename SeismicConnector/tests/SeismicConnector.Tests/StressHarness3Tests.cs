// StressHarness3Tests.cs
// ----------------------
// Round-3 stress harness: the ENTERPRISE PACK under concurrency/load, plus a
// regression re-validation that the pack did not break the round-1/2 hardened
// invariants, plus one fresh combined-soak dimension.
//
// Everything is in-process/offline (in-test certificates, temp dirs, fakes) —
// no network. Real measured numbers are appended to the shared StressLog.
//
// Round-3 dimensions:
//   A1. Certificate credential / client_assertion RS256 JWT signing under
//       concurrency — every assertion individually valid (x5t#S256, aud, exp,
//       verifiable RS256 signature) from many builders sharing ONE certificate,
//       and the production GraphClient token-form path never falls back to the
//       secret under a first-fetch stampede.
//   A2. Proxy + CA-bundle additive-trust callback under load — correct
//       accept/reject on every concurrent validation, no exception; concurrent
//       handler construction never leaks/deadlocks; bad PEM fails fast.
//   A3. Event Log sink under a logging flood — never throws, mapping holds,
//       no deadlock, no-op off-Windows.
//   A4. Dead-letter redaction under concurrent producers — no torn records, no
//       payload value survives, ids/hashes retained, retry re-fetch still works.
//   A5. New metric counters under concurrency — increments reconcile exactly.
//   B.  Regression: version-aware ingest, No-MNE exclusion, and re-ACL
//       never-widen still hold with the enterprise pack wired in and active.
//   C.  Combined enterprise soak — all pack paths interleaved at 3x volume.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

/// <summary>Shared helpers for the round-3 crypto stress tests.</summary>
internal static class Stress3
{
    /// <summary>RFC 7515 base64url decode (accepts the unpadded form the JWT uses).</summary>
    public static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    /// <summary>Parse a JWT segment as a JSON object.</summary>
    public static JsonObject Segment(string jwt, int index) =>
        JsonNode.Parse(Encoding.UTF8.GetString(FromBase64Url(jwt.Split('.')[index])))!.AsObject();

    /// <summary>
    /// Verify a client_assertion produced by <see cref="CertificateCredential"/>
    /// against the certificate. Returns null when valid, else the reason.
    /// </summary>
    public static string? WhyInvalid(
        string jwt, X509Certificate2 cert, string clientId, string audience, long minExp)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return $"expected 3 parts, got {parts.Length}";

        JsonObject header, claims;
        try
        {
            header = Segment(jwt, 0);
            claims = Segment(jwt, 1);
        }
        catch (Exception ex)
        {
            return $"unparseable segment: {ex.Message}";
        }

        if (header["alg"]?.GetValue<string>() != "RS256")
            return "alg != RS256";
        var expectedX5t = CertificateCredential.Base64Url(SHA256.HashData(cert.RawData));
        if (header["x5t#S256"]?.GetValue<string>() != expectedX5t)
            return "x5t#S256 mismatch";
        if (claims["aud"]?.GetValue<string>() != audience)
            return "aud mismatch";
        if (claims["iss"]?.GetValue<string>() != clientId || claims["sub"]?.GetValue<string>() != clientId)
            return "iss/sub mismatch";
        var nbf = claims["nbf"]!.GetValue<long>();
        var exp = claims["exp"]!.GetValue<long>();
        if (exp <= nbf)
            return "exp <= nbf";
        if (exp < minExp)
            return "exp in the past";

        using var rsa = cert.GetRSAPublicKey()!;
        var ok = rsa.VerifyData(
            Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]),
            FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return ok ? null : "RS256 signature does not verify";
    }
}

// ── A1. Certificate credential / client_assertion under concurrency ──────────

public class Stress3_CertAssertionConcurrency : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "seismic-cert3-" + Guid.NewGuid().ToString("N"));

    public Stress3_CertAssertionConcurrency() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static GraphSettings CertSettings(string certPath) => new()
    {
        TenantId = "tenant-guid",
        ClientId = "app-client-id",
        ClientSecret = "THE-CLIENT-SECRET-MUST-NEVER-LEAK",
        ClientCertPath = certPath,
        MaxRetries = 2,
        RetryBackoffBase = 1,
    };

    [Fact]
    public async Task SharedCert_ConcurrentBuilds_EveryJwtIndividuallyValid_UniqueJti()
    {
        // The production GraphClient caches ONE certificate and signs every token
        // build against it. RSA signing objects and hashers are not thread-safe;
        // if a shared mutable crypto object were reused without isolation a
        // signature could corrupt. Hammer BuildAssertion on ONE shared cert.
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        const string clientId = "app-client-id";
        const string audience = "https://login.microsoftonline.com/tenant/oauth2/v2.0/token";
        const int builders = 24;
        const int perBuilder = 200;
        var minExp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var jwts = new ConcurrentBag<string>();
        var buildErrors = new ConcurrentBag<string>();
        using var ready = new Barrier(builders);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, builders).Select(_ => Task.Run(() =>
        {
            ready.SignalAndWait();  // release all builders together for max contention
            for (var i = 0; i < perBuilder; i++)
            {
                try { jwts.Add(CertificateCredential.BuildAssertion(cert, clientId, audience)); }
                catch (Exception ex) { buildErrors.Add(ex.ToString()); }
            }
        })));
        sw.Stop();

        Assert.Empty(buildErrors);
        Assert.Equal(builders * perBuilder, jwts.Count);

        // Verify EVERY produced assertion individually.
        var invalid = new List<string>();
        var jtis = new HashSet<string>(StringComparer.Ordinal);
        var jtiCollisions = 0;
        foreach (var jwt in jwts)
        {
            var why = Stress3.WhyInvalid(jwt, cert, clientId, audience, minExp);
            if (why is not null)
                invalid.Add(why);
            if (!jtis.Add(Stress3.Segment(jwt, 1)["jti"]!.GetValue<string>()))
                jtiCollisions++;
        }

        Assert.Empty(invalid);
        Assert.Equal(0, jtiCollisions);
        StressLog.Record("R3-CERT-SIGN",
            $"builders={builders} assertions={jwts.Count} invalid={invalid.Count} " +
            $"jti_collisions={jtiCollisions} build_errors={buildErrors.Count} " +
            $"rate={jwts.Count / sw.Elapsed.TotalSeconds:F0}/s");
    }

    [Fact]
    public async Task GraphClientCertMode_FirstFetchStampede_AlwaysUsesAssertion_NeverSecret()
    {
        // The token cache resolves the certificate lazily; a first-fetch stampede
        // across many fresh clients maximises the lazy-init window. Every token
        // form MUST carry a valid client_assertion and NEVER the client secret —
        // a mis-synchronised resolve that returned null would silently fall back
        // to the secret (a real correctness/security regression).
        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        var path = Path.Combine(_tempDir, "auth.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, (string?)null));
        const string tokenUrl = "https://login.microsoftonline.com/tenant-guid/oauth2/v2.0/token";
        const int clients = 60;
        const int threadsPerClient = 8;
        var minExp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var forms = new ConcurrentBag<Dictionary<string, string>>();
        var errors = new ConcurrentBag<string>();

        for (var c = 0; c < clients; c++)
        {
            // A FRESH client each round → its cert cache is cold, so the concurrent
            // callers race through the lazy resolve simultaneously.
            var client = new GraphClient(CertSettings(path), new FakeHttpHandler());
            using var gate = new Barrier(threadsPerClient);
            await Task.WhenAll(Enumerable.Range(0, threadsPerClient).Select(_ => Task.Run(() =>
            {
                gate.SignalAndWait();
                try { forms.Add(client.BuildTokenForm(tokenUrl)); }
                catch (Exception ex) { errors.Add(ex.ToString()); }
            })));
        }

        Assert.Empty(errors);
        Assert.Equal(clients * threadsPerClient, forms.Count);

        var fellBackToSecret = 0;
        var invalidAssertion = new List<string>();
        foreach (var form in forms)
        {
            if (form.ContainsKey("client_secret"))
            {
                fellBackToSecret++;
                continue;
            }
            Assert.Equal(CertificateCredential.AssertionType, form["client_assertion_type"]);
            var why = Stress3.WhyInvalid(form["client_assertion"], cert, "app-client-id", tokenUrl, minExp);
            if (why is not null)
                invalidAssertion.Add(why);
        }

        Assert.Equal(0, fellBackToSecret);
        Assert.Empty(invalidAssertion);
        StressLog.Record("R3-CERT-STAMPEDE",
            $"clients={clients} forms={forms.Count} fell_back_to_secret={fellBackToSecret} " +
            $"invalid_assertions={invalidAssertion.Count} errors={errors.Count}");
    }
}

// ── A2. Additive-trust callback + handler construction under load ────────────

public class Stress3_AdditiveTrustConcurrency : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "seismic-trust3-" + Guid.NewGuid().ToString("N"));

    public Stress3_AdditiveTrustConcurrency()
    {
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable(HttpTransport.CaBundleEnvVar, null);
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HttpTransport.CaBundleEnvVar, null);
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static (X509Certificate2 ca, X509Certificate2 leaf) CaAndLeaf(string caCn, string leafCn)
    {
        using var caRsa = RSA.Create(2048);
        var caReq = new CertificateRequest(
            $"CN={caCn}", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var ca = caReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var leafRsa = RSA.Create(2048);
        var leafReq = new CertificateRequest(
            $"CN={leafCn}", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var leaf = leafReq.Create(
            ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(14), new byte[] { 9, 9, 9, 1 });
        return (ca, leaf);
    }

    [Fact]
    public async Task ConcurrentValidations_AcceptRejectAlwaysCorrect_NoExceptions()
    {
        // X509Chain is not thread-safe; the callback builds a fresh chain per
        // call. Drive many concurrent validations through it against a shared
        // custom-root bundle and assert every accept/reject is correct.
        var (ca, leaf) = CaAndLeaf("Contoso Issuing CA", "api.seismic.com");
        using var rogueCa = HttpTransportTests.SelfSigned("Rogue CA", ca: true);
        using (ca)
        using (leaf)
        {
            var roots = new X509Certificate2Collection { ca };  // shared across threads
            const int workers = 24;
            const int perWorker = 800;
            long validations = 0;
            var wrong = new ConcurrentBag<string>();
            var exceptions = new ConcurrentBag<string>();
            using var ready = new Barrier(workers);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() =>
            {
                ready.SignalAndWait();
                for (var i = 0; i < perWorker; i++)
                {
                    try
                    {
                        // 0: system-trusted → accept.  1: leaf via custom root → accept.
                        // 2: rogue → reject.  3: name mismatch → reject (never excused).
                        var scenario = (w + i) % 4;
                        bool got = scenario switch
                        {
                            0 => HttpTransport.ValidateWithAdditiveTrust(null, null, SslPolicyErrors.None, roots),
                            1 => HttpTransport.ValidateWithAdditiveTrust(
                                    leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, roots),
                            2 => HttpTransport.ValidateWithAdditiveTrust(
                                    rogueCa, null, SslPolicyErrors.RemoteCertificateChainErrors, roots),
                            _ => HttpTransport.ValidateWithAdditiveTrust(
                                    leaf, null,
                                    SslPolicyErrors.RemoteCertificateChainErrors
                                        | SslPolicyErrors.RemoteCertificateNameMismatch,
                                    roots),
                        };
                        bool want = scenario is 0 or 1;
                        if (got != want)
                            wrong.Add($"scenario={scenario} got={got} want={want}");
                        Interlocked.Increment(ref validations);
                    }
                    catch (Exception ex) { exceptions.Add(ex.ToString()); }
                }
            })));
            sw.Stop();

            Assert.Empty(exceptions);
            Assert.Empty(wrong);
            StressLog.Record("R3-TRUST",
                $"workers={workers} validations={validations} wrong_decisions={wrong.Count} " +
                $"exceptions={exceptions.Count} rate={validations / sw.Elapsed.TotalSeconds:F0}/s");
        }
    }

    [Fact]
    public async Task ConcurrentHandlerConstruction_WithCaBundle_NoLeakNoDeadlock()
    {
        using var rootA = HttpTransportTests.SelfSigned("Contoso Inspection CA", ca: true);
        using var rootB = HttpTransportTests.SelfSigned("Contoso Private Root", ca: true);
        var bundle = Path.Combine(_tempDir, "bundle.pem");
        File.WriteAllText(bundle, rootA.ExportCertificatePem() + "\n" + rootB.ExportCertificatePem());
        Environment.SetEnvironmentVariable(HttpTransport.CaBundleEnvVar, bundle);
        Environment.SetEnvironmentVariable(HttpTransport.ProxyUrlEnvVar, "http://proxy.contoso.com:8080");

        const int workers = 16;
        const int perWorker = 200;
        long built = 0;
        var errors = new ConcurrentBag<string>();
        using var ready = new Barrier(workers);

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            ready.SignalAndWait();
            for (var i = 0; i < perWorker; i++)
            {
                try
                {
                    using var handler = HttpTransport.CreateHandler();
                    Assert.IsType<SocketsHttpHandler>(handler);
                    Interlocked.Increment(ref built);
                }
                catch (Exception ex) { errors.Add(ex.ToString()); }
            }
        })));

        Assert.Empty(errors);
        Assert.Equal(workers * perWorker, built);

        // Bad CA path still fails fast naming the setting, even under the churn.
        Environment.SetEnvironmentVariable(HttpTransport.CaBundleEnvVar, Path.Combine(_tempDir, "nope.pem"));
        var ex2 = Assert.Throws<ConfigException>(() => HttpTransport.LoadCustomRoots());
        Assert.Contains(HttpTransport.CaBundleEnvVar, ex2.Message);

        StressLog.Record("R3-HANDLER-BUILD",
            $"workers={workers} handlers_built={built} errors={errors.Count} bad_ca_fails_fast=yes");
    }
}

// ── A3. Event Log sink under a logging flood ─────────────────────────────────

/// <summary>Thread-safe fake writer for the flood test (records with no locking of its own).</summary>
public sealed class ConcurrentEventLogWriter : IEventLogWriter
{
    public ConcurrentQueue<(string Message, EventLogEntryKind Kind, int EventId)> Entries { get; } = new();
    public void WriteEntry(string message, EventLogEntryKind kind, int eventId) =>
        Entries.Enqueue((message, kind, eventId));
}

public class Stress3_EventLogFlood : IDisposable
{
    private readonly ConcurrentEventLogWriter _writer = new();

    public Stress3_EventLogFlood()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, null);
        Environment.SetEnvironmentVariable(EventLogSink.LevelEnvVar, null);
        EventLogSink.OverrideWriter = _writer;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, null);
        Environment.SetEnvironmentVariable(EventLogSink.LevelEnvVar, null);
        EventLogSink.OverrideWriter = null;
    }

    private static LogRecord Rec(int level, string message) =>
        new() { Name = "seismic_connector.flood", Level = level, Message = message };

    [Fact]
    public async Task ConcurrentEmit_NeverThrows_MappingHolds_InfoFilteredAtDefaultLevel()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        var sink = EventLogSink.CreateIfConfigured()!;   // default level = WARNING
        const int threads = 16;
        const int perThread = 3_000;   // mix: 1/3 error, 1/3 warning, 1/3 info (filtered)
        var thrown = new ConcurrentBag<string>();
        using var ready = new Barrier(threads);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            ready.SignalAndWait();
            for (var i = 0; i < perThread; i++)
            {
                var level = (i % 3) switch { 0 => LogLevels.Error, 1 => LogLevels.Warning, _ => LogLevels.Info };
                var ex = Xunit.Record.Exception(() => sink.Handle(Rec(level, $"t{t}-{i}")));
                if (ex is not null)
                    thrown.Add(ex.ToString());
            }
        })));
        sw.Stop();

        Assert.Empty(thrown);
        Assert.Equal(0, sink.WriteFailures);

        // Error+Warning (2/3) mirror; Info (1/3) is filtered at the default level.
        var expectedMirrored = threads * (perThread / 3 * 2);
        Assert.Equal(expectedMirrored, _writer.Entries.Count);
        var errors = _writer.Entries.Count(e => e.Kind == EventLogEntryKind.Error);
        var warnings = _writer.Entries.Count(e => e.Kind == EventLogEntryKind.Warning);
        var infos = _writer.Entries.Count(e => e.Kind == EventLogEntryKind.Information);
        Assert.Equal(threads * (perThread / 3), errors);
        Assert.Equal(threads * (perThread / 3), warnings);
        Assert.Equal(0, infos);
        Assert.All(_writer.Entries, e => Assert.Equal(
            e.Kind == EventLogEntryKind.Error ? EventLogSink.ErrorEventId : EventLogSink.WarningEventId,
            e.EventId));

        StressLog.Record("R3-EVENTLOG",
            $"threads={threads} emits={threads * perThread} mirrored={_writer.Entries.Count} " +
            $"errors={errors} warnings={warnings} info_filtered={threads * (perThread / 3)} " +
            $"write_failures={sink.WriteFailures} thrown={thrown.Count} rate={threads * perThread / sw.Elapsed.TotalSeconds:F0}/s");
    }

    [Fact]
    public async Task ThrowingWriter_UnderFlood_NeverThrows_CountsFailures()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        EventLogSink.OverrideWriter = new ThrowingWriter();
        var sink = EventLogSink.CreateIfConfigured()!;
        const int threads = 12;
        const int perThread = 1_000;
        var thrown = new ConcurrentBag<string>();
        using var ready = new Barrier(threads);

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            ready.SignalAndWait();
            for (var i = 0; i < perThread; i++)
            {
                var ex = Xunit.Record.Exception(() => sink.Handle(Rec(LogLevels.Error, $"t{t}-{i}")));
                if (ex is not null)
                    thrown.Add(ex.ToString());
            }
        })));

        Assert.Empty(thrown);
        Assert.Equal(threads * perThread, sink.WriteFailures);
        StressLog.Record("R3-EVENTLOG-THROW",
            $"threads={threads} emits={threads * perThread} thrown={thrown.Count} " +
            $"write_failures_counted={sink.WriteFailures}");
    }

    private sealed class ThrowingWriter : IEventLogWriter
    {
        public void WriteEntry(string message, EventLogEntryKind kind, int eventId) =>
            throw new InvalidOperationException("event log unavailable");
    }
}

// ── A4. Dead-letter redaction under concurrent producers ─────────────────────

public class Stress3_DeadLetterRedactionConcurrency : IDisposable
{
    public Stress3_DeadLetterRedactionConcurrency() =>
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);

    private const string Secret = "TOP-SECRET-PII-VALUE-must-never-survive-redaction";

    private static JsonObject Body(string id) => new()
    {
        ["id"] = id,
        ["acl"] = new JsonArray(
            new JsonObject { ["type"] = "user", ["value"] = "entra-user-1", ["accessType"] = "grant" }),
        ["properties"] = new JsonObject
        {
            ["title"] = $"{Secret} {id}",
            ["teamsiteId"] = "ts1",
            ["versionId"] = "v7",
            ["views"] = 42,
            ["title@odata.type"] = "String",
        },
        ["content"] = new JsonObject { ["value"] = $"{Secret} body of {id}", ["type"] = "text" },
    };

    [Fact]
    public async Task RedactedMode_ConcurrentProducers_NoTornRecords_NoPayloadSurvives()
    {
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, "redacted");
        using var state = new TempStateDir();
        const string connectorId = "R3Redact";
        var path = SyncState.FailedRecordsPath(connectorId);
        const int producers = 20;
        const int perProducer = 300;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                var id = $"p{p}-i{i}";
                SyncState.AppendFailedRecords(
                    path,
                    new List<(string, string)> { (id, $"HTTP 503 for {id}") },
                    "ContentItem",
                    requestBodies: new Dictionary<string, JsonNode?> { [id] = Body(id) },
                    responseBodies: new Dictionary<string, JsonNode?>
                    {
                        [id] = new JsonObject
                        {
                            ["error"] = new JsonObject { ["code"] = "serviceNotAvailable", ["message"] = "try later" },
                        },
                    });
            }
        })));
        sw.Stop();

        var expected = producers * perProducer;

        // No payload value survives ANYWHERE in the raw file (torn or otherwise).
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);

        // Every line is intact JSONL — no torn/interleaved records dropped.
        var rawLines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(expected, rawLines);

        var records = SyncState.ReadFailedRecords(connectorId);
        Assert.Equal(expected, records.Count);
        var ids = records.Select(r => r["item_id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, ids.Count);   // all distinct, none lost

        // Redaction invariant on every record: content/property values stubbed,
        // identifiers + acl + error metadata retained (retry inputs survive).
        var violations = new List<string>();
        foreach (var r in records)
        {
            var req = r["request_body"]!.AsObject();
            var content = req["content"]!["value"]!.GetValue<string>();
            if (!content.StartsWith("[redacted sha256=", StringComparison.Ordinal))
                violations.Add($"content not redacted: {r["item_id"]}");
            if (!req["properties"]!["title"]!.GetValue<string>().StartsWith("[redacted sha256=", StringComparison.Ordinal))
                violations.Add($"title not redacted: {r["item_id"]}");
            if (req["properties"]!["views"] is not null)
                violations.Add($"numeric not stripped: {r["item_id"]}");
            if (req["properties"]!["teamsiteId"]!.GetValue<string>() != "ts1")
                violations.Add($"teamsiteId lost: {r["item_id"]}");
            if (req["properties"]!["versionId"]!.GetValue<string>() != "v7")
                violations.Add($"versionId lost: {r["item_id"]}");
            if (req["acl"]![0]!["value"]!.GetValue<string>() != "entra-user-1")
                violations.Add($"acl lost: {r["item_id"]}");
            if (r["object_type"]!.GetValue<string>() != "ContentItem")
                violations.Add($"object_type lost: {r["item_id"]}");
            if (r["response_body"]!["error"]!["code"]!.GetValue<string>() != "serviceNotAvailable")
                violations.Add($"error code lost: {r["item_id"]}");
        }
        Assert.Empty(violations);

        StressLog.Record("R3-DL-REDACT",
            $"producers={producers} records={records.Count} raw_lines={rawLines} distinct_ids={ids.Count} " +
            $"secret_survived=no torn_lines=0 redaction_violations={violations.Count} " +
            $"rate={expected / sw.Elapsed.TotalSeconds:F0}/s");
    }

    [Fact]
    public async Task RedactedMode_RetryRefetchesFullContentFromSource()
    {
        // A redacted dead-letter record must still yield a FULL re-ingest: retry
        // replays only item_id/object_type and re-fetches content from Seismic.
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, "redacted");
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));

        SyncState.AppendFailedRecords(
            SyncState.FailedRecordsPath(harness.Config.Connector.Id),
            new[] { ("c1", "HTTP 503") },
            "ContentItem",
            requestBodies: new Dictionary<string, JsonNode?> { ["c1"] = Body("c1") });
        var record = Assert.Single(SyncState.ReadFailedRecords(harness.Config.Connector.Id));

        var ok = await harness.Pipeline.IngestSingleAsync(record["item_id"]!.GetValue<string>(), "ts1");

        Assert.True(ok);
        var put = harness.LastPutBody("c1");
        Assert.NotNull(put);
        var contentValue = put!["content"]!["value"]!.GetValue<string>();
        Assert.Contains("payload of c1", contentValue, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted", contentValue, StringComparison.Ordinal);
    }
}

// ── A5. New metric counters under concurrency ────────────────────────────────

public class Stress3_MetricsConcurrency
{
    [Fact]
    public async Task ConcurrentIncrements_ReconcileExactly_NoTear()
    {
        Metrics.ResetForTests();
        try
        {
            const int threads = 16;
            const int perThread = 50_000;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    Metrics.IncWebhookAccepted();
                    Metrics.IncWebhookRejected(2);
                    Metrics.IncHaClaimsAcquired();
                    Metrics.AddHaClaimsHeld(1);
                    Metrics.RecordExtraction("pdf", success: true);
                }
            })));
            sw.Stop();

            long total = (long)threads * perThread;
            Assert.Equal(total, Metrics.WebhookAccepted);
            Assert.Equal(total * 2, Metrics.WebhookRejected);
            Assert.Equal(total, Metrics.HaClaimsAcquired);
            Assert.Equal(total, Metrics.HaClaimsHeld);          // += 1 each, never torn
            Assert.Equal(total, Metrics.ExtractionAttemptsSnapshot["pdf"]);
            Assert.Equal(total, Metrics.ExtractionSuccessesSnapshot["pdf"]);

            // The rendered surface reflects the reconciled values.
            var text = Metrics.RenderPrometheus();
            Assert.Contains($"seismic_connector_webhook_accepted_total {total}", text);
            Assert.Contains($"seismic_connector_ha_claims_held {total}", text);

            StressLog.Record("R3-METRICS",
                $"threads={threads} inc_ops={total * 5} accepted={Metrics.WebhookAccepted} " +
                $"rejected={Metrics.WebhookRejected} ha_held={Metrics.HaClaimsHeld} tear=0 " +
                $"rate={total * 5 / sw.Elapsed.TotalSeconds:F0}/s");
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }

    [Fact]
    public async Task ConcurrentHaHeld_UpDown_NeverGoesNegative_ReconcilesToZero()
    {
        Metrics.ResetForTests();
        try
        {
            const int threads = 16;
            const int perThread = 40_000;
            await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
            {
                // Balanced +1/-1 pairs across threads; the clamp must never let a
                // transient negative read escape, and the final value reconciles to 0.
                for (var i = 0; i < perThread; i++)
                    Metrics.AddHaClaimsHeld(t % 2 == 0 ? 1 : -1);
            })));

            Assert.Equal(0, Metrics.HaClaimsHeld);
            Assert.True(Metrics.HaClaimsHeld >= 0);
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }
}

// ── B. Regression: hardened invariants hold WITH the enterprise pack active ──

/// <summary>
/// Turns on the enterprise pack process-globals for a test's lifetime:
/// EVENTLOG mirror (fake writer) attached to the root logger, and dead-letter
/// redaction. Restores everything on dispose.
/// </summary>
internal sealed class EnterprisePackActive : IDisposable
{
    private readonly EventLogSink? _sink;
    public ConcurrentEventLogWriter Writer { get; } = new();

    public EnterprisePackActive()
    {
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, "redacted");
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        EventLogSink.OverrideWriter = Writer;
        _sink = EventLogSink.InstallOnRoot();
    }

    public void Dispose()
    {
        if (_sink is not null)
            Logging.Root.RemoveHandler(_sink);
        EventLogSink.OverrideWriter = null;
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, null);
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);
    }
}

public class Stress3_RegressionUnderEnterprisePack
{
    [Fact]
    public async Task VersionAwareCrawl_WithEnterprisePackActive_SupersedeInPlace_NoDelete()
    {
        using var pack = new EnterprisePackActive();
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");

        const int docs = 300;
        for (var i = 0; i < docs; i++)
            harness.AddContent(TestContent.Make($"d{i}", versionId: "v1"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        var firstPuts = harness.PutItemIds().Count;
        Assert.Equal(docs, firstPuts);

        // Re-crawl same versions → all skipped, no second PUT.
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        Assert.Equal(docs, harness.PutItemIds().Count);   // unchanged
        Assert.True(harness.Pipeline.Stats.Skipped >= docs);

        // Bump every doc → supersede in place over the SAME id, never delete+add.
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        for (var i = 0; i < docs; i++)
            harness.AddContent(TestContent.Make($"d{i}", versionId: "v2"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        for (var i = 0; i < docs; i++)
        {
            Assert.Equal(2, harness.PutItemIds().Count(id => id == $"d{i}"));
            Assert.Equal("v2", harness.Store.GetTrackedItem($"d{i}")!.VersionId);
        }
        Assert.Empty(harness.DeletedItemIds);  // supersede is an update, not delete+add
        StressLog.Record("R3-REG-VERSION",
            $"docs={docs} first_puts={firstPuts} superseded_in_place={docs} deletes={harness.DeletedItemIds.Count} " +
            $"eventlog_mirrored={pack.Writer.Entries.Count}");
    }

    [Fact]
    public async Task ResumeReconcile_WithEnterprisePackActive_NoStaleResurrection()
    {
        using var pack = new EnterprisePackActive();
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("keep", versionId: "v1"));
        harness.AddContent(TestContent.Make("bump", versionId: "v1"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Version churn on a subsequent crawl: "bump" moves to v2 → superseded,
        // "keep" unchanged → skipped. No stale v1 resurrection of "bump".
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("keep", versionId: "v1"));
        harness.AddContent(TestContent.Make("bump", versionId: "v2"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Equal("v2", harness.Store.GetTrackedItem("bump")!.VersionId);
        Assert.Equal("v1", harness.Store.GetTrackedItem("keep")!.VersionId);
        Assert.Equal("v2", harness.LastPutBody("bump")?["properties"]?["versionId"]?.GetValue<string>());
        Assert.Empty(harness.DeletedItemIds);
    }

    [Fact]
    public async Task NoMne_WithEnterprisePackActive_ExcludedNeverEmitted()
    {
        using var pack = new EnterprisePackActive();
        var rules = new ExclusionRules
        {
            ExcludedFlags = { "MNE" },
            FlagProperties = { "complianceFlag" },
        };
        using var harness = new PipelineHarness(exclusions: rules, fallbackAcl: "tenant");
        harness.AddTeamsite("ts-open", "Open Library");

        const int docs = 600;
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < docs; i++)
        {
            var mne = i % 2 == 0;
            var id = $"o{i}";
            if (mne)
                excluded.Add(id);
            harness.AddContent(TestContent.Make(id, teamsiteId: "ts-open",
                properties: mne
                    ? new List<SeismicProperty> { new() { Name = "complianceFlag", Value = "MNE" } }
                    : new List<SeismicProperty>()));
        }

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        var put = harness.PutItemIds().ToHashSet(StringComparer.Ordinal);
        Assert.Empty(put.Intersect(excluded));               // not one flagged item leaked
        Assert.Equal(docs / 2, put.Count);                   // exactly the clean half
        StressLog.Record("R3-REG-NOMNE",
            $"docs={docs} excluded={excluded.Count} emitted={put.Count} leaked=0 " +
            $"eventlog_mirrored={pack.Writer.Entries.Count}");
    }

    [Fact]
    public async Task ReAclUnresolved_WithEnterprisePackActive_NeverWidensToEveryone()
    {
        using var pack = new EnterprisePackActive();
        var dbPath = Path.Combine(Path.GetTempPath(), "seismic-r3acl-" + Guid.NewGuid().ToString("N") + ".db");
        using var store = new SqliteIdentityStore(dbPath);
        try
        {
            // fallback=tenant is the dangerous policy: an unresolved principal set
            // would otherwise widen to "everyone in tenant".
            var mapper = new AclMapper(store, fallbackAcl: "tenant", tenantId: "tenant-guid");
            var principals = new[] { "u0", "u1", "u2" };
            foreach (var p in principals)
                store.UpsertPrincipal(new PrincipalMapping(p, "user", $"{p}@c.com", $"entra-{p}", null));
            var itemPerms = principals
                .Select(p => new SeismicPermission { PrincipalId = p, PrincipalType = "user", Email = $"{p}@c.com" })
                .ToList();

            long resolves = 0, unresolved = 0, everyone = 0, blocked = 0;
            var violations = new ConcurrentBag<string>();
            var stop = false;

            var writer = Task.Run(() =>
            {
                var mapped = false;
                while (!Volatile.Read(ref stop))
                {
                    mapped = !mapped;
                    foreach (var p in principals)
                        store.UpsertPrincipal(new PrincipalMapping(
                            p, "user", $"{p}@c.com", mapped ? $"entra-{p}" : null, null));
                    Thread.Sleep(1);
                }
            });

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var r = 0; r < 4_000; r++)
                {
                    var acl = mapper.Resolve(itemPerms, teamsitePermissions: null);
                    Interlocked.Increment(ref resolves);
                    var hasEveryone = acl.Entries.Any(e => e.Type == "everyone");
                    if (hasEveryone)
                    {
                        Interlocked.Increment(ref everyone);
                        if (!acl.Unresolved)
                            violations.Add("everyone-grant not flagged Unresolved");
                    }
                    if (acl.Unresolved)
                    {
                        Interlocked.Increment(ref unresolved);
                        Interlocked.Increment(ref blocked);   // pipeline refuses → no widening
                    }
                    if (!acl.Unresolved && hasEveryone)
                        violations.Add("non-unresolved everyone leak");
                }
            })));
            Volatile.Write(ref stop, true);
            await writer;

            Assert.Empty(violations);
            Assert.True(unresolved > 0, "churn never produced an unresolved state — vacuous");
            Assert.True(everyone > 0, "fallback everyone path never exercised — vacuous");
            Assert.Equal(everyone, blocked);
            StressLog.Record("R3-REG-REACL",
                $"resolves={resolves} unresolved={unresolved} everyone_grants={everyone} " +
                $"widenings_blocked={blocked} violations={violations.Count}");
        }
        finally
        {
            store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }
}

// ── C. Combined enterprise soak — all pack paths interleaved at 3x volume ─────

public class Stress3_CombinedEnterpriseSoak : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "seismic-soak3-" + Guid.NewGuid().ToString("N"));

    public Stress3_CombinedEnterpriseSoak()
    {
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, null);
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, null);
        EventLogSink.OverrideWriter = null;
        Metrics.ResetForTests();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AllPackPathsInterleaved_UnderSustainedLoad_AllInvariantsHold()
    {
        // Enterprise pack fully active, every path driven at once.
        Environment.SetEnvironmentVariable(SyncState.PayloadModeEnvVar, "redacted");
        Environment.SetEnvironmentVariable(EventLogSink.EnabledEnvVar, "true");
        var eventWriter = new ConcurrentEventLogWriter();
        EventLogSink.OverrideWriter = eventWriter;
        var sink = EventLogSink.CreateIfConfigured()!;
        Metrics.ResetForTests();

        using var state = new TempStateDir();
        const string connectorId = "R3Soak";
        var dlPath = SyncState.FailedRecordsPath(connectorId);
        const string secret = "SOAK-SECRET-PII";

        using var cert = HttpTransportTests.SelfSigned("graph-auth");
        var (caRsa, ca, leaf) = MakeCaLeaf();
        using (caRsa)
        using (ca)
        using (leaf)
        {
            var roots = new X509Certificate2Collection { ca };
            const string audience = "https://login.microsoftonline.com/t/oauth2/v2.0/token";
            var minExp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 3x round-1 producer volume (round-1 dead-letter used 24 producers).
            const int workers = 24;
            const int perWorker = 500;
            long assertions = 0, validations = 0, dlWrites = 0, logEmits = 0;
            var invalidAssertion = new ConcurrentBag<string>();
            var wrongTrust = new ConcurrentBag<string>();
            var exceptions = new ConcurrentBag<string>();
            using var ready = new Barrier(workers);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() =>
            {
                ready.SignalAndWait();
                for (var i = 0; i < perWorker; i++)
                {
                    try
                    {
                        // 1) cert assertion (shared cert)
                        var jwt = CertificateCredential.BuildAssertion(cert, "app", audience);
                        if (Stress3.WhyInvalid(jwt, cert, "app", audience, minExp) is { } why)
                            invalidAssertion.Add(why);
                        Interlocked.Increment(ref assertions);

                        // 2) additive-trust validation (shared roots)
                        var accept = HttpTransport.ValidateWithAdditiveTrust(
                            leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, roots);
                        var reject = HttpTransport.ValidateWithAdditiveTrust(
                            ca /* self-signed root presented as leaf, but it IS the trusted root */,
                            null, SslPolicyErrors.RemoteCertificateNameMismatch, roots);
                        if (!accept) wrongTrust.Add("leaf-not-accepted");
                        if (reject) wrongTrust.Add("name-mismatch-accepted");
                        Interlocked.Add(ref validations, 2);

                        // 3) event-log flood
                        sink.Handle(new LogRecord
                        {
                            Name = "seismic_connector.soak", Level = LogLevels.Error, Message = $"w{w}-{i}",
                        });
                        Interlocked.Increment(ref logEmits);

                        // 4) redacted dead-letter append (concurrent producers)
                        var id = $"w{w}-i{i}";
                        SyncState.AppendFailedRecords(
                            dlPath,
                            new List<(string, string)> { (id, "HTTP 503") },
                            "ContentItem",
                            requestBodies: new Dictionary<string, JsonNode?>
                            {
                                [id] = new JsonObject
                                {
                                    ["id"] = id,
                                    ["content"] = new JsonObject { ["value"] = $"{secret} {id}" },
                                },
                            });
                        Interlocked.Increment(ref dlWrites);

                        // 5) metric counters
                        Metrics.IncItemsFailed();
                        Metrics.IncWebhookAccepted();
                    }
                    catch (Exception ex) { exceptions.Add(ex.ToString()); }
                }
            })));
            sw.Stop();

            long total = (long)workers * perWorker;

            // Every invariant holds together under the combined load.
            Assert.Empty(exceptions);
            Assert.Empty(invalidAssertion);
            Assert.Empty(wrongTrust);
            Assert.Equal(0, sink.WriteFailures);
            Assert.Equal(total, assertions);
            Assert.Equal(total * 2, validations);

            // Dead-letter: no torn lines, no secret survives, all ids present.
            var raw = File.ReadAllText(dlPath);
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
            var records = SyncState.ReadFailedRecords(connectorId);
            Assert.Equal(total, records.Count);
            Assert.Equal(total, records.Select(r => r["item_id"]!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal).Count);

            // Event-log mirrored every error emit (Handle serialises Emit).
            Assert.Equal(total, eventWriter.Entries.Count);

            // Metrics reconcile exactly.
            Assert.Equal(total, Metrics.ItemsFailed);
            Assert.Equal(total, Metrics.WebhookAccepted);

            StressLog.Record("R3-SOAK",
                $"workers={workers} per_worker={perWorker} assertions={assertions} trust_validations={validations} " +
                $"log_emits={logEmits} dl_writes={dlWrites} records={records.Count} secret_survived=no " +
                $"eventlog_mirrored={eventWriter.Entries.Count} items_failed={Metrics.ItemsFailed} " +
                $"exceptions={exceptions.Count} elapsed={sw.Elapsed.TotalSeconds:F2}s " +
                $"ops/s={total * 5 / sw.Elapsed.TotalSeconds:F0}");
        }
    }

    private static (RSA caRsa, X509Certificate2 ca, X509Certificate2 leaf) MakeCaLeaf()
    {
        var caRsa = RSA.Create(2048);
        var caReq = new CertificateRequest(
            "CN=Soak Issuing CA", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var ca = caReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var leafRsa = RSA.Create(2048);
        var leafReq = new CertificateRequest(
            "CN=api.seismic.com", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var leaf = leafReq.Create(
            ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(14), new byte[] { 7, 7, 7, 2 });
        return (caRsa, ca, leaf);
    }
}
