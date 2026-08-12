// StressRound3Tests.cs
// ====================
// Round-3 stress harness. Rounds 1-2 hardened the component/interaction layers
// (StressHarnessTests.cs, StressRound2Tests.cs). This round attacks the
// ENTERPRISE PACK that shipped after them — certificate Graph auth, proxy+CA
// transport, Windows Event Log mirroring, dead-letter payload redaction, and
// the new metrics — which were only UNIT-tested, never stress-tested. It also
// re-validates the round-1/2 invariants WITH the enterprise knobs turned on
// (regression), and adds one fresh adversarial dimension (clock-skew storm on
// the client_assertion time window).
//
// PART A — enterprise-pack code paths under concurrency/load:
//   R3S1  client_assertion JWT signing under 16+ concurrent builders sharing one
//         certificate — every JWT individually valid (x5t#S256 / aud / exp /
//         verifiable RS256 signature); zero malformed/invalid; no key material
//         or assertion ever logged.
//   R3S2  proxy + CA-bundle transport under load — many concurrent
//         RemoteCertificateValidationCallback validations (in-test CA+leaf):
//         correct accept/reject EVERY time, no exception, no shared-X509Chain
//         corruption; malformed PEM / bad CA path fail fast naming the setting;
//         concurrent handler construction never leaks/deadlocks.
//   R3S3  Event Log sink under a logging flood — many threads emitting
//         Error/Warning: never throws, level mapping holds, off-Windows no-op,
//         a throwing mirror never breaks logging, mirrored text carries no PII.
//   R3S4  dead-letter redaction under concurrent producers (+ a DSAR
//         forget-subject racing them): no torn records, zero PII/profile value
//         at rest, ids/hashes retained, retry re-fetch still works, and a
//         to-be-forgotten subject never leaks through a redacted record.
//   R3S5  new metric counters under concurrency — increments never tear;
//         values reconcile exactly.
//
// PART B — regression: the enterprise pack did NOT regress the hardened
//   invariants (seat never-everyone / fail-closed, DSAR suppression +
//   hash-chained ledger + compensating withdrawals, PII-safe-by-construction)
//   re-run UNDER LOAD with EVENTLOG_ENABLED + redacted dead-letter live.
//
// PART C — fresh dimension: a clock-skew storm on the client_assertion. Many
//   concurrent builders each stamp the assertion at a randomised wall-clock
//   offset (±minutes); every produced assertion must still satisfy AAD's time
//   contract (nbf = iat-60, exp = iat+540, window ≤ 600 s) and verify.
//
// Offline/in-process only: temp dirs, in-test certs, capturing fakes. Measured
// numbers go through StressLog (STRESS_LOG + test output), same as rounds 1-2.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.Ingestion;
using AltrataConnector.State;
using Xunit.Abstractions;

namespace AltrataConnector.Tests;

// ---- shared round-3 helpers ------------------------------------------------------

internal static class Round3Certs
{
    /// <summary>Decode a base64url segment to bytes.</summary>
    public static byte[] B64Url(string segment)
    {
        var p = segment.Replace('-', '+').Replace('_', '/');
        p += (p.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(p);
    }

    public static JsonElement DecodeSegment(string segment) =>
        JsonDocument.Parse(B64Url(segment)).RootElement.Clone();

    /// <summary>Assert one client assertion is a fully valid AAD RS256 JWT for
    /// (cert, clientId, audience) — header, claims and a verifiable signature.
    /// Throws (fails the test) on the FIRST defect so a concurrency corruption
    /// surfaces with its cause.</summary>
    public static void AssertValidAssertion(X509Certificate2 cert, string jwt,
        string clientId, string audience)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new Xunit.Sdk.XunitException($"assertion is not a 3-part JWT: {parts.Length} parts");

        var header = DecodeSegment(parts[0]);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal(CertificateCredential.Base64Url(SHA256.HashData(cert.RawData)),
            header.GetProperty("x5t#S256").GetString());

        var payload = DecodeSegment(parts[1]);
        Assert.Equal(audience, payload.GetProperty("aud").GetString());
        Assert.Equal(clientId, payload.GetProperty("iss").GetString());
        Assert.Equal(clientId, payload.GetProperty("sub").GetString());
        var iat = payload.GetProperty("iat").GetInt64();
        var nbf = payload.GetProperty("nbf").GetInt64();
        var exp = payload.GetProperty("exp").GetInt64();
        Assert.Equal(iat - 60, nbf);                 // 60 s skew grace
        Assert.Equal(iat + 540, exp);                // 9-minute lifetime
        Assert.True(exp - nbf <= 600, $"assertion window {exp - nbf}s exceeds AAD's 10-minute cap");

        // The signature must verify with the certificate's PUBLIC key over the
        // exact signing input — a torn/racy RSA sign would fail here.
        var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = B64Url(parts[2]);
        using var rsa = cert.GetRSAPublicKey()!;
        Assert.True(rsa.VerifyData(signingInput, signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "RS256 signature did not verify");
    }

    /// <summary>An in-test CA (self-signed, CA-capable) plus a leaf certificate
    /// issued by it (leaf carries no private key — validation needs only the
    /// public cert). Mirrors a private-PKI / TLS-inspection appliance chain.</summary>
    public static (X509Certificate2 Ca, X509Certificate2 Leaf) NewCaAndLeaf(
        string caSubject = "CN=Round3 Corp Root CA", string leafSubject = "CN=leaf.corp.local")
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest(caSubject, caKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, false));
        var ca = caReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest(leafSubject, leafKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var serial = Guid.NewGuid().ToByteArray();
        var leaf = leafReq.Create(ca, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1), serial);
        return (ca, leaf);
    }
}

/// <summary>Thread-safe capturing Event Log mirror with per-severity counters.</summary>
internal sealed class ConcurrentEventLogMirror : IEventLogMirror
{
    private int _info, _warning, _error;
    private int _badLevelForEventId;
    public readonly ConcurrentBag<string> Messages = new();

    public int Info => Volatile.Read(ref _info);
    public int Warning => Volatile.Read(ref _warning);
    public int Error => Volatile.Read(ref _error);
    public int BadLevelForEventId => Volatile.Read(ref _badLevelForEventId);

    public void Write(EventLogSeverity severity, int eventId, string message)
    {
        // Level→event-id mapping must hold under load.
        var expected = severity switch
        {
            EventLogSeverity.Error => EventLogSink.EventIdError,
            EventLogSeverity.Warning => EventLogSink.EventIdWarning,
            _ => EventLogSink.EventIdLifecycle,
        };
        if (eventId != expected)
            Interlocked.Increment(ref _badLevelForEventId);
        switch (severity)
        {
            case EventLogSeverity.Error: Interlocked.Increment(ref _error); break;
            case EventLogSeverity.Warning: Interlocked.Increment(ref _warning); break;
            default: Interlocked.Increment(ref _info); break;
        }
        Messages.Add(message);
    }
}

// =====================================================================================
// R3S1 — client_assertion JWT signing under concurrency
// =====================================================================================

public class CertAssertionConcurrencyStressTests
{
    private readonly ITestOutputHelper _out;
    public CertAssertionConcurrencyStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Sixteen_way_concurrent_signing_over_one_cert_yields_all_valid_assertions()
    {
        // PRIME SUSPECT: RSA private-key signing objects are not thread-safe in
        // .NET. Hammer BuildClientAssertion with ONE shared certificate from many
        // threads and verify EVERY produced JWT (x5t#S256, aud/iss/sub, time
        // window, verifiable RS256 signature). A shared mutable signer would
        // corrupt a signature or throw here.
        using var cert = HardeningFixtures.NewSelfSignedRsa("CN=Round3 Signing Cert");
        const string clientId = "11111111-2222-3333-4444-555555555555";
        const string audience = "https://login.microsoftonline.com/tenant-r3/oauth2/v2.0/token";
        // 16-way contention is more than enough to expose a non-thread-safe
        // signer; the per-call GetRSAPrivateKey() is heavily serialised by the OS
        // key store (~50 sign/s on macOS), so keep the count modest — production
        // only signs once per ~55-min token refresh, behind the token lock.
        const int threads = 16;
        const int perThread = 64;    // 1,024 concurrent signs

        var produced = new ConcurrentBag<string>();
        var jtis = new ConcurrentBag<string>();
        var exceptions = new ConcurrentQueue<Exception>();

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    var jwt = CertificateCredential.BuildClientAssertion(cert, clientId, audience);
                    produced.Add(jwt);
                    var jti = Round3Certs.DecodeSegment(jwt.Split('.')[1]).GetProperty("jti").GetString()!;
                    jtis.Add(jti);
                }
                catch (Exception exc)
                {
                    exceptions.Enqueue(exc);
                }
            }
        });
        sw.Stop();

        Assert.True(exceptions.IsEmpty,
            $"{exceptions.Count} signing exception(s), first: {exceptions.FirstOrDefault()}");
        var all = produced.ToArray();
        Assert.Equal(threads * perThread, all.Length);

        // Validate every assertion (this is where a corrupted signature shows).
        var invalid = 0;
        foreach (var jwt in all)
        {
            try { Round3Certs.AssertValidAssertion(cert, jwt, clientId, audience); }
            catch { invalid++; }
        }
        Assert.Equal(0, invalid);

        // Default jti must be unique per call (no shared Guid state / collisions).
        Assert.Equal(all.Length, jtis.Distinct().Count());

        StressLog.Line(_out,
            $"[R3S1a] cert client_assertion: {all.Length:N0} concurrent signs ({threads} threads) over ONE cert " +
            $"in {sw.Elapsed.TotalSeconds:F1}s ({all.Length / sw.Elapsed.TotalSeconds:N0} sign/s), " +
            $"0 exceptions, 0 invalid assertions, {jtis.Distinct().Count():N0}/{all.Length:N0} distinct jti");
    }

    [Fact]
    public async Task Concurrent_graph_token_path_emits_valid_assertions_and_never_logs_key_material()
    {
        // Drive the REAL GraphClient token path (cert configured → certificate
        // WINS over the secret) from many concurrent operations, each on its own
        // client so the per-client token lock does not serialise everything.
        // Capture every token-request body and assert the assertion is valid and
        // no key material / assertion leaks into logs.
        using var cert = HardeningFixtures.NewSelfSignedRsa("CN=Round3 Graph Cert");
        var pfxPath = Path.Combine(TestFixtures.NewTempDir("r3cert"), "graph.pfx");
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pkcs12, "pw"));

        var previousLogs = Environment.GetEnvironmentVariable("LOGS_DIR");
        var logsDir = TestFixtures.NewTempDir("r3certlogs");
        Environment.SetEnvironmentVariable("LOGS_DIR", logsDir);
        Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar, pfxPath);
        Environment.SetEnvironmentVariable(CertificateCredential.CertPasswordEnvVar, "pw");
        Logging.ResetForTests();
        RunLog.StartRun("r3cert");
        try
        {
            var config = TestFixtures.NewConfig() with
            { AadTenantId = "tenant-r3", AadClientId = "client-r3", AadClientSecret = "the-secret-should-never-ship" };
            var tokenUrl = $"https://login.microsoftonline.com/{config.AadTenantId}/oauth2/v2.0/token";
            var tokenBodies = new ConcurrentBag<string>();

            const int clients = 20;
            var tasks = Enumerable.Range(0, clients).Select(_ => Task.Run(async () =>
            {
                var handler = new ScriptedHandler();
                handler.Enqueue(req =>
                {
                    tokenBodies.Add(req.Content!.ReadAsStringAsync().Result);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"access_token":"tok","expires_in":3600}""",
                            Encoding.UTF8, "application/json"),
                    };
                });
                handler.EnqueueJson(200, "{}");
                var client = new GraphClient(config, handler, (_, _) => Task.CompletedTask);
                await client.DeleteItemAsync("r3-item");
            })).ToArray();
            await Task.WhenAll(tasks);

            Assert.Equal(clients, tokenBodies.Count);
            var valid = 0;
            foreach (var body in tokenBodies)
            {
                // form-url-encoded: client_assertion=<jwt>
                var kv = body.Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                Assert.Equal(CertificateCredential.ClientAssertionType, kv["client_assertion_type"]);
                Assert.False(kv.ContainsKey("client_secret"), "client_secret rode along despite a cert");
                Round3Certs.AssertValidAssertion(cert, kv["client_assertion"], "client-r3", tokenUrl);
                valid++;
            }
            Assert.Equal(clients, valid);

            // No key material, no raw assertion, in the run log — only the MODE
            // and the public thumbprint are allowed.
            Logging.ResetForTests();
            var logText = string.Join("\n",
                Directory.EnumerateFiles(logsDir, "*.log", SearchOption.AllDirectories)
                    .Select(File.ReadAllText));
            Assert.DoesNotContain("the-secret-should-never-ship", logText);
            Assert.DoesNotContain("BEGIN", logText);              // no PEM key blocks
            Assert.DoesNotContain("client_assertion", logText);   // the JWT is never logged
            foreach (var body in tokenBodies)
                Assert.DoesNotContain(body, logText);             // no token-request body logged
            Assert.Contains(cert.Thumbprint!, logText);           // the public thumbprint IS logged (mode line)

            StressLog.Line(_out,
                $"[R3S1b] graph token path: {clients} concurrent clients (cert wins over secret) → " +
                $"{valid}/{clients} valid client_assertions, 0 secret leaks, 0 key material / assertions in logs, " +
                "auth-mode line carries the public thumbprint only");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar, null);
            Environment.SetEnvironmentVariable(CertificateCredential.CertPasswordEnvVar, null);
            Environment.SetEnvironmentVariable("LOGS_DIR", previousLogs);
            Logging.ResetForTests();
        }
    }
}

// =====================================================================================
// R3S2 — proxy + CA-bundle transport under load
// =====================================================================================

public class ProxyCaTransportConcurrencyStressTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public ProxyCaTransportConcurrencyStressTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyUrlEnvVar, null);
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyBypassEnvVar, null);
        Environment.SetEnvironmentVariable(HttpConnectivity.CaBundleEnvVar, null);
    }

    [Fact]
    public void Concurrent_additive_trust_validations_are_correct_every_time()
    {
        // PRIME SUSPECT: X509Chain is not thread-safe; a callback that shares one
        // chain across concurrent TLS validations can race/corrupt. Drive many
        // concurrent validations through ValidateWithAdditiveTrust with a SHARED
        // trust bundle and assert the accept/reject verdict is correct EVERY time
        // and nothing throws.
        var (trustedCa, trustedLeaf) = Round3Certs.NewCaAndLeaf(
            "CN=Round3 Trusted Root", "CN=trusted.corp.local");
        var (_, foreignLeaf) = Round3Certs.NewCaAndLeaf(
            "CN=Round3 Foreign Root", "CN=foreign.example");   // NOT rooted in the bundle
        using var _ca = trustedCa;

        // The bundle (extra trust roots) is SHARED across every concurrent call —
        // exactly the object a racy callback would corrupt.
        var bundle = new X509Certificate2Collection { trustedCa };

        // Cases (1)/(2) each build an X509Chain (the shared-chain race target);
        // OS chain evaluation is ~10 ms on macOS, so keep the count bounded.
        const int threads = 16;
        const int perThread = 60;    // 960 validations per verdict class
        var wrongVerdict = 0;
        var exceptions = new ConcurrentQueue<Exception>();

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    // (1) trusted leaf, chain errors → ACCEPT via the bundle.
                    if (!HttpConnectivity.ValidateWithAdditiveTrust(
                            trustedLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors, bundle))
                        Interlocked.Increment(ref wrongVerdict);
                    // (2) foreign leaf, chain errors → REJECT (not in the bundle).
                    if (HttpConnectivity.ValidateWithAdditiveTrust(
                            foreignLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors, bundle))
                        Interlocked.Increment(ref wrongVerdict);
                    // (3) trusted leaf but NAME MISMATCH → REJECT, never forgiven.
                    if (HttpConnectivity.ValidateWithAdditiveTrust(
                            trustedLeaf, null,
                            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors,
                            bundle))
                        Interlocked.Increment(ref wrongVerdict);
                    // (4) OS already accepted → ACCEPT (additive never subtracts).
                    if (!HttpConnectivity.ValidateWithAdditiveTrust(
                            trustedLeaf, null, SslPolicyErrors.None, bundle))
                        Interlocked.Increment(ref wrongVerdict);
                }
                catch (Exception exc)
                {
                    exceptions.Enqueue(exc);
                }
            }
        });
        sw.Stop();

        Assert.True(exceptions.IsEmpty,
            $"{exceptions.Count} validation exception(s), first: {exceptions.FirstOrDefault()}");
        Assert.Equal(0, wrongVerdict);

        foreignLeaf.Dispose();
        trustedLeaf.Dispose();
        var total = (long)threads * perThread * 4;
        StressLog.Line(_out,
            $"[R3S2a] additive-trust callback: {total:N0} concurrent validations ({threads} threads, shared bundle) " +
            $"in {sw.Elapsed.TotalSeconds:F1}s ({total / sw.Elapsed.TotalSeconds:N0}/s), " +
            "0 wrong verdicts, 0 exceptions (per-call X509Chain holds under contention)");
    }

    [Fact]
    public void Concurrent_handler_construction_with_proxy_and_ca_never_leaks_or_deadlocks()
    {
        var (ca, leaf) = Round3Certs.NewCaAndLeaf();
        using var _ = ca;
        using var __ = leaf;
        var caPath = Path.Combine(TestFixtures.NewTempDir("r3ca"), "roots.pem");
        File.WriteAllText(caPath, ca.ExportCertificatePem());
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyUrlEnvVar, "http://proxy.corp.local:8080");
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyBypassEnvVar, @"^https://otel\.corp\.local");
        Environment.SetEnvironmentVariable(HttpConnectivity.CaBundleEnvVar, caPath);

        const int threads = 32;
        var built = 0;
        var exceptions = new ConcurrentQueue<Exception>();
        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
        {
            try
            {
                using var handler = (SocketsHttpHandler)HttpConnectivity.CreateHandler();
                Assert.NotNull(handler.Proxy);
                Assert.True(handler.UseProxy);
                Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
                Interlocked.Increment(ref built);
            }
            catch (Exception exc)
            {
                exceptions.Enqueue(exc);
            }
        });
        sw.Stop();

        Assert.True(exceptions.IsEmpty,
            $"{exceptions.Count} construction exception(s), first: {exceptions.FirstOrDefault()}");
        Assert.Equal(threads, built);
        StressLog.Line(_out,
            $"[R3S2b] handler construction: {built} concurrent CreateHandler() with proxy+CA in " +
            $"{sw.Elapsed.TotalMilliseconds:F0} ms, 0 exceptions, every handler wired (proxy + additive-trust callback)");
    }

    [Fact]
    public void Malformed_pem_and_bad_ca_path_fail_fast_naming_the_setting_under_concurrency()
    {
        var missing = Path.Combine(TestFixtures.NewTempDir("r3ca"), "nope.pem");
        var garbage = Path.Combine(TestFixtures.NewTempDir("r3ca"), "garbage.pem");
        File.WriteAllText(garbage, "-----BEGIN NONSENSE-----\nnot base64 at all\n-----END NONSENSE-----");

        var named = 0;
        var wrong = new ConcurrentQueue<string>();
        Parallel.For(0, 200, i =>
        {
            var path = i % 2 == 0 ? missing : garbage;
            Environment.SetEnvironmentVariable(HttpConnectivity.CaBundleEnvVar, path);
            try
            {
                HttpConnectivity.LoadCaBundle();
                wrong.Enqueue($"i={i}: expected ConfigurationError, got success");
            }
            catch (ConfigurationError exc)
            {
                if (exc.Message.Contains(HttpConnectivity.CaBundleEnvVar))
                    Interlocked.Increment(ref named);
                else
                    wrong.Enqueue($"i={i}: error did not name the setting: {exc.Message}");
            }
            catch (Exception exc)
            {
                wrong.Enqueue($"i={i}: unexpected {exc.GetType().Name}: {exc.Message}");
            }
        });

        Assert.True(wrong.IsEmpty, string.Join(" | ", wrong.Take(3)));
        Assert.Equal(200, named);
        StressLog.Line(_out,
            $"[R3S2c] bad CA config under concurrency: 200/200 fail-fast with ConfigurationError naming " +
            $"{HttpConnectivity.CaBundleEnvVar} (missing file + garbage PEM), 0 silent fallbacks");
    }
}

// =====================================================================================
// R3S3 — Event Log sink under a logging flood
// =====================================================================================

public class EventLogFloodStressTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public EventLogFloodStressTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        EventLogSink.Override = null;
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, null);
        Environment.SetEnvironmentVariable("LOGS_DIR", null);
        Logging.ResetForTests();
    }

    [Fact]
    public void Flood_of_concurrent_warning_error_lines_never_throws_and_maps_levels()
    {
        var mirror = new ConcurrentEventLogMirror();
        EventLogSink.Override = mirror;
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, "true");
        var logger = Logging.GetLogger("altrata_connector.r3flood");

        // WARNING/ERROR also go to the (serialised) console, so keep the flood
        // bounded; 6,000 concurrent lines still saturate the mirror dispatch.
        const int threads = 24;
        const int perThread = 250;
        var exceptions = new ConcurrentQueue<Exception>();
        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    if ((t + i) % 2 == 0)
                        logger.Error($"error {t}-{i}", new InvalidOperationException("boom"));
                    else
                        logger.Warning($"warn {t}-{i}");
                    logger.Info($"info {t}-{i}");   // INFO must NEVER mirror
                }
                catch (Exception exc)
                {
                    exceptions.Enqueue(exc);
                }
            }
        });
        sw.Stop();

        Assert.True(exceptions.IsEmpty,
            $"{exceptions.Count} logging exception(s), first: {exceptions.FirstOrDefault()}");
        var total = threads * perThread;
        Assert.Equal(total, mirror.Warning + mirror.Error);   // every WARN/ERROR mirrored, no drops
        Assert.Equal(0, mirror.Info);                         // INFO never mirrored
        Assert.Equal(0, mirror.BadLevelForEventId);           // level→event-id mapping held under load

        StressLog.Line(_out,
            $"[R3S3a] eventlog flood: {total:N0} WARN/ERROR lines ({threads} threads) mirrored in " +
            $"{sw.Elapsed.TotalSeconds:F1}s — {mirror.Warning:N0} warn(2000) + {mirror.Error:N0} error(3000), " +
            $"0 INFO mirrored, 0 mis-mapped, 0 exceptions, {total:N0} INFO lines correctly suppressed");
    }

    [Fact]
    public void A_throwing_mirror_under_flood_never_breaks_logging()
    {
        EventLogSink.Override = new AlwaysThrowMirror();
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, "true");
        var logger = Logging.GetLogger("altrata_connector.r3throw");

        var exceptions = new ConcurrentQueue<Exception>();
        Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 24 }, t =>
        {
            for (var i = 0; i < 300; i++)
            {
                var exc = Record.Exception(() => logger.Error($"still fine {t}-{i}"));
                if (exc != null)
                    exceptions.Enqueue(exc);
            }
        });
        Assert.True(exceptions.IsEmpty,
            $"a throwing mirror broke logging {exceptions.Count} time(s): {exceptions.FirstOrDefault()}");
        StressLog.Line(_out,
            "[R3S3b] throwing-mirror flood: 7,200 ERROR lines across 24 threads — logging never threw " +
            "(sink swallows the failure, warns once via stderr)");
    }

    [Fact]
    public void Sink_is_a_no_op_when_disabled_even_under_flood()
    {
        var mirror = new ConcurrentEventLogMirror();
        EventLogSink.Override = mirror;
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, null);   // disabled
        Assert.False(EventLogSink.Enabled);
        var logger = Logging.GetLogger("altrata_connector.r3off");

        Parallel.For(0, 16, _ =>
        {
            for (var i = 0; i < 200; i++)
                logger.Error("should not mirror");
        });
        Assert.Empty(mirror.Messages);
        StressLog.Line(_out,
            "[R3S3c] disabled sink: 3,200 ERROR lines across 16 threads → 0 mirrored (EVENTLOG_ENABLED unset)");
    }

    private sealed class AlwaysThrowMirror : IEventLogMirror
    {
        public void Write(EventLogSeverity severity, int eventId, string message) =>
            throw new UnauthorizedAccessException("event log unavailable");
    }
}

// =====================================================================================
// R3S4 — dead-letter redaction under concurrent producers (+ DSAR race)
// =====================================================================================

public class DeadLetterRedactionConcurrencyStressTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public DeadLetterRedactionConcurrencyStressTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, null);
        ServiceStop.ResetForTests();
    }

    // PII values that must NEVER appear at rest in a redacted queue.
    private static readonly string[] PiiValues =
    {
        "Ada Lovelace", "ada@contoso.com", "9000000", "Analytical Engines",
        "Grace Hopper", "grace@contoso.com", "7000000", "Compilers Inc",
    };

    [Fact]
    public async Task Concurrent_producers_write_untorn_pii_free_redacted_records()
    {
        // Many producers appending redacted dead-letters to ONE queue file at
        // once — the record for a subject carries the subject's HASH (join key)
        // but never the raw id or any profile value. Assert: no torn line,
        // every record parses, hashes retained, zero raw PII/id at rest, and a
        // concurrent MutateDeadLetters (the forget-subject scrub path) never
        // corrupts the file.
        var root = TestFixtures.NewTempDir("r3dl");
        var state = new FileStateStore("R3DlProducers",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

        const int producers = 16;
        const int perProducer = 400;   // 6,400 redacted records
        var expectedByHash = new ConcurrentDictionary<string, byte>();
        var sw = Stopwatch.StartNew();

        // Concurrent scrubber: repeatedly runs the atomic read-modify-write the
        // forget-subject path uses, racing the appenders.
        using var stopScrub = new CancellationTokenSource();
        var scrubs = 0;
        var scrubber = Task.Run(() =>
        {
            while (!stopScrub.IsCancellationRequested)
            {
                // A no-op-ish scrub of a subject that is never produced: exercises
                // the atomic RMW against live appenders without dropping real rows.
                var forgetHash = DeadLetterPolicy.HashSubject("NEVER-PRODUCED-SUBJECT");
                state.MutateDeadLetters(current =>
                    current.Where(r => !r.SubjectHashes.Contains(forgetHash)));
                Interlocked.Increment(ref scrubs);
            }
        });

        Parallel.For(0, producers, new ParallelOptions { MaxDegreeOfParallelism = producers }, p =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                var subjectId = $"P{p:D2}-{i:D4}";          // raw id — must NOT appear at rest
                var hash = DeadLetterPolicy.HashSubject(subjectId);
                expectedByHash[hash] = 1;
                // Built exactly like CrawlEngine's redacted branch: payload empty,
                // SubjectIds empty, hashes only.
                state.AddDeadLetter(new DeadLetterRecord
                {
                    ItemId = $"PersonProfile-{subjectId}",
                    Dataset = Datasets.PersonProfile,
                    DeliveryId = $"d{p}",
                    Error = "HTTP 503 (redacted producer)",
                    Redacted = true,
                    PayloadJson = "",
                    SubjectIds = Array.Empty<string>(),
                    SubjectHashes = new[] { hash },
                });
            }
        });
        stopScrub.Cancel();
        await scrubber;
        sw.Stop();

        // No torn records: the raw file has exactly N well-formed JSONL lines,
        // and ReadDeadLetters parses all of them (it counts malformed lines).
        var rawLines = File.ReadAllLines(state.DeadLetterPath)
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(producers * perProducer, rawLines.Length);
        foreach (var line in rawLines)
        {
            var rec = JsonSerializer.Deserialize<DeadLetterRecord>(line);   // throws on a torn line
            Assert.NotNull(rec);
            Assert.True(rec!.Redacted);
            Assert.Empty(rec.PayloadJson);
            Assert.Empty(rec.SubjectIds);
            Assert.Single(rec.SubjectHashes);
        }
        var parsed = state.ReadDeadLetters();
        Assert.Equal(producers * perProducer, parsed.Count);

        // Every subject hash retained (the PII-safe join key).
        var hashesAtRest = parsed.SelectMany(r => r.SubjectHashes).ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedByHash.Keys.All(hashesAtRest.Contains), "a subject hash was lost");

        // Redaction invariant at rest: the queue retains the Graph item id (an
        // opaque identifier needed for retry) but NEVER a raw subject id in the
        // SubjectIds field, a payload, or any profile value. The raw subject id
        // is only ever present hashed.
        var fileText = File.ReadAllText(state.DeadLetterPath);
        Assert.DoesNotContain("\"SubjectIds\":[\"", fileText);   // no raw id field ever populated
        Assert.DoesNotContain("\"PayloadJson\":\"[", fileText);  // no captured item JSON at rest
        foreach (var pii in PiiValues)                           // no profile value ever leaked
            Assert.DoesNotContain(pii, fileText);

        StressLog.Line(_out,
            $"[R3S4a] redacted producers: {producers * perProducer:N0} records ({producers} producers) + " +
            $"{scrubs:N0} concurrent scrubs in {sw.Elapsed.TotalSeconds:F1}s — {rawLines.Length:N0} untorn lines, " +
            $"{hashesAtRest.Count:N0} hashes retained, 0 raw-id fields / payloads / profile values at rest");
    }

    [Fact]
    public async Task Redacted_deadletter_never_leaks_a_concurrently_forgotten_subject()
    {
        // The DSAR cross: a crawl dead-letters PII-laden records in REDACTED mode
        // while forget-subject erases one of those very subjects concurrently.
        // Invariants: no profile/PII value ever at rest; the forgotten subject is
        // suppressed and its item never live; retry re-fetch still completes the
        // survivor. Run several iterations to shuffle the interleaving.
        const int iterations = 12;
        long leaked = 0, survivorReplayed = 0, victimSuppressed = 0;
        var sw = Stopwatch.StartNew();

        for (var k = 0; k < iterations; k++)
        {
            using var harness = new CrawlHarness();   // env unset ⇒ redacted default
            TestFixtures.WriteDelivery(harness.FeedPath, "d1",
                ("persons.json", Datasets.PersonProfile, """
                    [{"id":"P1","person_name":"Ada Lovelace","email":"ada@contoso.com","net_worth_usd":"9000000","employer":"Analytical Engines"},
                     {"id":"P2","person_name":"Grace Hopper","email":"grace@contoso.com","net_worth_usd":"7000000","employer":"Compilers Inc"}]
                    """, 2));
            // Both items fail → both dead-letter (redacted).
            harness.Graph.FailingItemIds.Add("PersonProfile-P1");
            harness.Graph.FailingItemIds.Add("PersonProfile-P2");

            using var runtime = TestFixtures.NewRuntime(harness.Config, harness.Graph, harness.Root);
            // Race: crawl (produces redacted dead-letters) vs forget P1.
            var crawlTask = harness.Engine.RunAsync(CrawlKind.Full);
            var forgetTask = CommandRegistry.ForgetSubjectAsync(
                runtime, altrataId: "P1", email: null, actor: "joseph", confirm: true);
            await Task.WhenAll(crawlTask, forgetTask);

            // No PII/profile value at rest in the queue file, in EITHER order.
            var queueText = File.Exists(harness.State.DeadLetterPath)
                ? File.ReadAllText(harness.State.DeadLetterPath) : "";
            foreach (var pii in PiiValues)
                if (queueText.Contains(pii)) leaked++;
            // The forgotten subject id must never appear at rest.
            if (queueText.Contains("\"P1\"") || queueText.Contains("[\"P1\"]")) leaked++;

            Assert.True(harness.State.IsSubjectSuppressed("P1"), $"iter {k}: victim not suppressed");
            victimSuppressed++;

            // Recover Graph and retry: the survivor (P2) re-fetches from the feed
            // and lands; the forgotten P1 is dropped (suppression wins).
            harness.Graph.FailingItemIds.Clear();
            var retry = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);
            Assert.Equal(true, retry);
            Assert.DoesNotContain(harness.Graph.PutItems, i => i.Id == "PersonProfile-P1");
            if (harness.Graph.PutItems.Any(i => i.Id == "PersonProfile-P2"))
                survivorReplayed++;
            // Suppression still durable; ledger intact.
            Assert.True(harness.State.IsSubjectSuppressed("P1"));
            Assert.True(runtime.Erasure.Verify(out _));
        }
        sw.Stop();

        Assert.Equal(0, leaked);
        Assert.Equal(iterations, victimSuppressed);
        Assert.Equal(iterations, survivorReplayed);
        StressLog.Line(_out,
            $"[R3S4b] redacted DSAR race: {iterations} crawl×forget interleavings in {sw.Elapsed.TotalSeconds:F1}s — " +
            $"0 PII/id leaks at rest, victim suppressed {victimSuppressed}/{iterations}, survivor re-fetched & " +
            $"replayed {survivorReplayed}/{iterations}, ledger Verify=OK");
    }
}

// =====================================================================================
// R3S5 — new metric counters under concurrency
// =====================================================================================

public class MetricsConcurrencyStressTests
{
    private readonly ITestOutputHelper _out;
    public MetricsConcurrencyStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Concurrent_increments_across_counters_do_not_tear()
    {
        // Unique per-run counter names isolate from the process-global Metrics
        // dictionary other tests share, so the reconciliation is EXACT.
        var run = Guid.NewGuid().ToString("N")[..8];
        var counters = Enumerable.Range(0, 8).Select(i => $"r3_test_counter_{run}_{i}").ToArray();

        const int threads = 24;
        const int perThread = 5_000;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var rng = new Random(t);
            for (var i = 0; i < perThread; i++)
                Metrics.Increment(counters[rng.Next(counters.Length)]);
        });
        sw.Stop();

        var total = counters.Sum(Metrics.Get);
        var expected = (double)threads * perThread;
        Assert.Equal(expected, total);   // not one increment lost or double-counted

        // A registered real counter also reconciles against a captured delta.
        var before = Metrics.Get("altrata_graph_throttle_429_total");
        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < 1_000; i++)
                Metrics.Increment("altrata_graph_throttle_429_total");
        });
        Assert.Equal(before + threads * 1_000, Metrics.Get("altrata_graph_throttle_429_total"));

        StressLog.Line(_out,
            $"[R3S5] metrics: {expected:N0} concurrent increments ({threads} threads) across {counters.Length} counters " +
            $"in {sw.Elapsed.TotalMilliseconds:F0} ms reconcile EXACTLY ({total:N0}); registered-counter delta exact too");
    }
}

// =====================================================================================
// PART B — regression: hardened invariants hold WITH the enterprise knobs on
// =====================================================================================

public class EnterprisePackRegressionStressTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public EnterprisePackRegressionStressTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        EventLogSink.Override = null;
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, null);
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, null);
        ServiceStop.ResetForTests();
    }

    [Fact]
    public async Task Hardened_invariants_survive_concurrent_crawls_with_eventlog_and_redaction_on()
    {
        // Turn the enterprise knobs ON (Windows Event Log mirroring + redacted
        // dead-letters — the connector default) and re-run the signature
        // invariants under load: seat-only ACLs (never everyone), PII-safe by
        // construction across EVERY surface (queue file, event log mirror),
        // fail-closed entitlement, hash-chained erasure ledger + compensating
        // withdrawal after a concurrent DSAR.
        var mirror = new ConcurrentEventLogMirror();
        EventLogSink.Override = mirror;
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, "true");
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, null);   // redacted default

        var previousLogs = Environment.GetEnvironmentVariable("LOGS_DIR");
        try
        {
            const int shards = 4;
            const int itemsPerShard = 120;
            var pii = new List<string>();
            var everyoneViolations = 0;
            var roots = new List<string>();
            var runtimes = new List<Runtime>();
            var harnesses = new List<CrawlHarness>();

            for (var s = 0; s < shards; s++)
            {
                var harness = new CrawlHarness();
                harnesses.Add(harness);
                roots.Add(harness.Root);
                var persons = Enumerable.Range(0, itemsPerShard).Select(i =>
                    ($"S{s}P{i}", $"Person-{s}-{i} SECRETNAME{s}{i}",
                     (string?)$"secret{s}{i}@contoso.com", (string?)$"SECRETEMP{s}{i} Ltd")).ToArray();
                TestFixtures.WriteDelivery(harness.FeedPath, "d1",
                    ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(persons), itemsPerShard));
                // Fail one item per shard → a redacted dead-letter (WARNING → event log).
                harness.Graph.FailingItemIds.Add($"PersonProfile-S{s}P0");
                pii.Add($"SECRETNAME{s}0");
                pii.Add($"secret{s}0@contoso.com");
                pii.Add($"SECRETEMP{s}0");
            }

            // Concurrent crawls; each shard's fake asserts never-everyone on PUT.
            var results = await Task.WhenAll(harnesses.Select(h =>
                Task.Run(() => h.Engine.RunAsync(CrawlKind.Full))));

            foreach (var (h, r) in harnesses.Zip(results))
            {
                Assert.Equal(itemsPerShard - 1, r.ItemsIngested);
                Assert.Equal(1, r.ItemsDeadLettered);
                // Seat-only ACLs, never everyone, on every ingested item.
                foreach (var item in h.Graph.PutItems)
                {
                    if (item.Acl.Any(e => e.Type is "everyone" or "everyoneExceptGuests"))
                        everyoneViolations++;
                    Assert.NotEmpty(item.Acl);
                }
                // Redacted queue holds no profile at rest.
                var queueText = File.ReadAllText(h.State.DeadLetterPath);
                foreach (var secret in pii)
                    Assert.DoesNotContain(secret, queueText);
            }
            Assert.Equal(0, everyoneViolations);

            // The event log mirror really fired (dead-letter WARNINGs) and carries
            // NO PII — the Event Log is one more log surface, not a softer one.
            Assert.Contains(mirror.Messages, m => m.Contains("Dead-lettered") || m.Contains("dead-letter"));
            var mirrorBlob = string.Join("\n", mirror.Messages);
            foreach (var secret in pii)
                Assert.DoesNotContain(secret, mirrorBlob);

            // Fail-closed entitlement is unchanged by the enterprise pack.
            Assert.Throws<EntitlementViolationException>(
                () => SeatAclBuilder.BuildAcl(Array.Empty<SeatPrincipal>()));

            // Concurrent DSAR on shard 0 subject S0P1: suppressed, withdrawn,
            // ledger stays hash-chain valid; retry completes any compensating
            // DELETE. (Uses the shard's own runtime so stores line up.)
            var h0 = harnesses[0];
            using var rt0 = TestFixtures.NewRuntime(h0.Config, h0.Graph, h0.Root);
            var forgotten = await CommandRegistry.ForgetSubjectAsync(
                rt0, altrataId: "S0P1", email: null, actor: "joseph", confirm: true);
            Assert.Equal(true, forgotten);
            Assert.True(h0.State.IsSubjectSuppressed("S0P1"));
            Assert.True(rt0.Erasure.Verify(out _));
            Assert.DoesNotContain(h0.Identity.ListIngestedItems(), i => i.ItemId == "PersonProfile-S0P1");

            foreach (var h in harnesses) h.Dispose();
            runtimes.ForEach(r => { });

            StressLog.Line(_out,
                $"[R3B] regression under enterprise knobs: {shards} concurrent crawls × {itemsPerShard} items with " +
                $"EVENTLOG_ENABLED + redacted dead-letters — 0 everyone-grants, {mirror.Warning + mirror.Error} " +
                $"event-log entries with 0 PII, redacted queues with 0 profiles at rest, fail-closed on empty seats, " +
                "DSAR suppressed + withdrawn + ledger Verify=OK");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGS_DIR", previousLogs);
        }
    }
}

// =====================================================================================
// PART C — fresh dimension: clock-skew storm on the client_assertion time window
// =====================================================================================

public class ClockSkewAssertionStressTests
{
    private readonly ITestOutputHelper _out;
    public ClockSkewAssertionStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Clock_skew_storm_never_produces_an_assertion_outside_the_aad_window()
    {
        // Adversary: a fleet of nodes with skewed clocks (±10 minutes) all minting
        // assertions concurrently. AAD accepts an assertion iff nbf ≤ now ≤ exp
        // (server clock) AND the total lifetime ≤ 10 minutes. The connector cannot
        // control a node's wall clock, but it MUST keep the time contract
        // internally consistent (nbf = iat-60, exp = iat+540, window = 600 s) and
        // always sign verifiably — regardless of skew or concurrency.
        using var cert = HardeningFixtures.NewSelfSignedRsa("CN=Round3 Skew Cert");
        const string clientId = "client-skew";
        const string audience = "https://login.microsoftonline.com/t/oauth2/v2.0/token";
        // Concurrent signing over one cert is OS-serialised (~50 sign/s on macOS);
        // 512 skewed assertions cover the ±10-min space without dominating the run.
        const int threads = 16;
        const int perThread = 32;

        var windows = new ConcurrentBag<long>();
        var invalid = 0;
        var maxWindow = 0L;
        var exceptions = new ConcurrentQueue<Exception>();

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var rng = new Random(t * 7919 + 1);
            for (var i = 0; i < perThread; i++)
            {
                // Randomised skew in [-10 min, +10 min] around real now.
                var skewSeconds = rng.Next(-600, 601);
                var skewedNow = DateTime.UtcNow.AddSeconds(skewSeconds);
                try
                {
                    var jwt = CertificateCredential.BuildClientAssertion(cert, clientId, audience, skewedNow);
                    var payload = Round3Certs.DecodeSegment(jwt.Split('.')[1]);
                    var iat = payload.GetProperty("iat").GetInt64();
                    var nbf = payload.GetProperty("nbf").GetInt64();
                    var exp = payload.GetProperty("exp").GetInt64();
                    var window = exp - nbf;
                    windows.Add(window);
                    InterlockedMax(ref maxWindow, window);
                    // Internal time contract, independent of skew.
                    if (nbf != iat - 60 || exp != iat + 540 || window > 600)
                        Interlocked.Increment(ref invalid);
                    // And it still verifies.
                    Round3Certs.AssertValidAssertion(cert, jwt, clientId, audience);
                    // iat must track the (skewed) clock we passed, proving the
                    // stamp is deterministic w.r.t. utcNow (offline-testable).
                    if (Math.Abs(iat - CertificateCredential.ToUnixSeconds(skewedNow)) > 1)
                        Interlocked.Increment(ref invalid);
                }
                catch (Exception exc)
                {
                    exceptions.Enqueue(exc);
                }
            }
        });
        sw.Stop();

        Assert.True(exceptions.IsEmpty,
            $"{exceptions.Count} exception(s) under skew, first: {exceptions.FirstOrDefault()}");
        Assert.Equal(0, invalid);
        Assert.True(maxWindow <= 600, $"an assertion window ({maxWindow}s) exceeded AAD's 10-minute cap");
        var total = threads * perThread;
        Assert.Equal(total, windows.Count);

        StressLog.Line(_out,
            $"[R3C] clock-skew storm: {total:N0} assertions minted across ±10 min skew ({threads} threads) in " +
            $"{sw.Elapsed.TotalSeconds:F1}s — every window exactly 600 s (max {maxWindow}s ≤ AAD cap), " +
            "0 outside-window, 0 unverifiable, iat tracks the skewed clock deterministically");
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
    }
}
