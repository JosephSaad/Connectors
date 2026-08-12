// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// StressRound3Tests.cs
// --------------------
// Round-3 stress suite. Rounds 1-2 hardened the ingestion pipeline, the memoized
// ACL resolver and the HA lease layer. Since then an ENTERPRISE PACK landed that
// was only UNIT-tested, never stress-tested:
//
//   * certificate Graph auth (client_assertion RS256 JWT signing) — GraphAuth.cs
//   * proxy + custom-CA TLS transport (RemoteCertificateValidationCallback that
//     rebuilds an X509Chain against additive roots) — HttpClientFactory.cs
//   * Windows Event Log mirroring sink — EventLogSink.cs
//   * dead-letter payload redaction — DeadLetterRedaction.cs / SyncState.cs
//   * process metric counters — Metrics.cs
//
// PART A hammers each of those five under concurrency/load; PART B proves the
// enterprise pack did not regress the round-1/2 hardened resolver invariants by
// running them WHILE the enterprise paths are under load; PART C adds two fresh
// adversarial dimensions — token-expiry clock skew under concurrent varying
// clocks, and a sustained interleaved 3x soak across every enterprise path.
//
// PRIME SUSPECTS explicitly targeted (a shared mutable crypto object reused
// across threads corrupts a signature or a chain build):
//   A1 — RSA private-key signing / SHA hashing during JWT build. The code
//        instantiates the signer per call (using var rsa = cert.GetRSAPrivateKey()),
//        so every one of thousands of concurrently-built assertions must carry a
//        correct x5t#S256, aud, exp and an individually-verifiable RS256 signature.
//   A2 — X509Chain during TLS validation. The callback builds a fresh X509Chain
//        per validation, so concurrent accept/reject decisions must each be
//        correct with zero exceptions leaking into the TLS stack.
//
// Everything is offline: certificates are generated in-test, no token endpoint or
// TLS server is contacted, dead-letter writes go to a temp dir.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace SalesforceCopilotConnector.Tests;

// ═════════════════════════════════════════════════════════════════════════════
// Shared helpers
// ═════════════════════════════════════════════════════════════════════════════

internal static class Round3Support
{
    /// <summary>
    /// Optional machine-readable sink for measured numbers: set ROUND3_METRICS_FILE
    /// to a path and each scenario appends "scenario\tmeasured\n". A no-op when unset,
    /// so the suite is byte-identical in normal CI runs.
    /// </summary>
    private static readonly object ReportGate = new();

    public static void Report(string scenario, string measured)
    {
        var path = Environment.GetEnvironmentVariable("ROUND3_METRICS_FILE");
        if (string.IsNullOrEmpty(path))
            return;
        lock (ReportGate)
            File.AppendAllText(path, $"{scenario}\t{measured}\n");
    }

    /// <summary>Decode a compact JWS into (header, payload, signature, signing-input).</summary>
    public static (JsonObject Header, JsonObject Payload, byte[] Signature, string SigningInput) DecodeJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new FormatException($"malformed JWT: {parts.Length} segments");
        return (
            JsonNode.Parse(FromBase64Url(parts[0]))!.AsObject(),
            JsonNode.Parse(FromBase64Url(parts[1]))!.AsObject(),
            FromBase64UrlBytes(parts[2]),
            parts[0] + "." + parts[1]);
    }

    private static string FromBase64Url(string value) => Encoding.UTF8.GetString(FromBase64UrlBytes(value));

    private static byte[] FromBase64UrlBytes(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    public static async Task AwaitBoundedAsync(Task work, int seconds, string what)
    {
        var done = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.True(done == work, $"{what} did not complete within {seconds}s — possible deadlock/livelock");
        await work;  // propagate failures
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PART A.1 — client_assertion JWT signing under concurrency
// ═════════════════════════════════════════════════════════════════════════════

[Collection("EnvVars")]
public sealed class Round3CertAssertionStressTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tmp = Directory.CreateTempSubdirectory("r3_cert_").FullName;

    private static readonly string[] Touched =
    {
        GraphAuth.CertPathEnvVar, GraphAuth.CertPasswordEnvVar, GraphAuth.CertThumbprintEnvVar,
        "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_AUTHORITY_HOST",
    };
    private readonly Dictionary<string, string?> _saved = new();

    public Round3CertAssertionStressTests(ITestOutputHelper output)
    {
        _out = output;
        foreach (var name in Touched)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved)
            Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private static void VerifyAssertion(
        string jwt, X509Certificate2 cert, string tid, string cid, string host, long? now = null)
    {
        var (header, payload, signature, signingInput) = Round3Support.DecodeJwt(jwt);

        Assert.Equal("RS256", header["alg"]!.GetValue<string>());
        Assert.Equal("JWT", header["typ"]!.GetValue<string>());
        Assert.Equal(
            ClientAssertionJwt.Base64Url(SHA256.HashData(cert.RawData)),
            header["x5t#S256"]!.GetValue<string>());

        Assert.Equal($"{host}/{tid}/oauth2/v2.0/token", payload["aud"]!.GetValue<string>());
        Assert.Equal(cid, payload["iss"]!.GetValue<string>());
        Assert.Equal(cid, payload["sub"]!.GetValue<string>());
        Assert.True(Guid.TryParse(payload["jti"]!.GetValue<string>(), out _));

        var iat = payload["iat"]!.GetValue<long>();
        Assert.Equal(iat, payload["nbf"]!.GetValue<long>());
        Assert.Equal(iat + (long)ClientAssertionJwt.Lifetime.TotalSeconds, payload["exp"]!.GetValue<long>());
        if (now is long expected)
            Assert.Equal(expected, iat);

        // The load-bearing assertion for the concurrency race: the signature must
        // verify against the certificate's public key. A signer corrupted by a
        // shared-instance data race yields a JWT that fails exactly here.
        using var rsa = cert.GetRSAPublicKey()!;
        Assert.True(
            rsa.VerifyData(
                Encoding.ASCII.GetBytes(signingInput), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "RS256 signature failed to verify — signing corrupted under concurrency");
    }

    [Fact]
    public void SignAssertion_32Threads_EveryJwtIndividuallyValid()
    {
        // 32 concurrent builders hammer ONE shared certificate (the exact object
        // ClientAssertionCredential captures) — cert.GetRSAPrivateKey()/SignData
        // and SHA256.HashData are exercised from many threads at once.
        using var cert = TestCertificates.CreateSelfSignedWithKey();
        const string tid = "tid-round3";
        const string cid = "cid-round3";
        const string host = "https://login.microsoftonline.com";
        const int threads = 32;
        const int perThread = 300;

        var jwts = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    jwts.Enqueue(ClientAssertionJwt.Build(cert, tid, cid, host));
                }
                catch (Exception exc)
                {
                    errors.Enqueue($"build threw: {exc.GetType().Name}: {exc.Message}");
                }
            }
        });
        sw.Stop();

        Assert.True(errors.IsEmpty, string.Join(" | ", errors.Take(5)));
        Assert.Equal(threads * perThread, jwts.Count);

        // Verify every produced assertion (signature + claims + x5t) and that all
        // jti values are unique across every concurrently-built token.
        var jti = new HashSet<string>();
        var invalid = new List<string>();
        foreach (var jwt in jwts)
        {
            try
            {
                VerifyAssertion(jwt, cert, tid, cid, host);
                var (_, payload, _, _) = Round3Support.DecodeJwt(jwt);
                Assert.True(jti.Add(payload["jti"]!.GetValue<string>()), "duplicate jti");
            }
            catch (Exception exc)
            {
                invalid.Add(exc.Message);
            }
        }

        Assert.True(invalid.Count == 0, $"{invalid.Count} invalid assertions:\n" + string.Join("\n", invalid.Take(5)));
        Assert.Equal(threads * perThread, jti.Count);

        var msg = $"{jwts.Count} assertions on 1 shared cert / {threads} threads in {sw.ElapsedMilliseconds} ms " +
                  $"({jwts.Count * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0}/s); " +
                  "0 build errors, 0 invalid signatures, 0 duplicate jti";
        _out.WriteLine("[A1 sign-concurrency] " + msg);
        Round3Support.Report("A1_sign_concurrency", msg);
    }

    [Fact]
    public void SignAssertion_PfxLoadedKey_ThroughCredentialClosure_ValidUnderConcurrency()
    {
        // Exercise the REAL production closure GraphAuth.CreateCredential() hands to
        // ClientAssertionCredential, over a PFX-LOADED private key (different key
        // provider semantics than an in-memory generated key), hammered from 24
        // threads. This is the token path Azure.Identity invokes per token request.
        using var generated = TestCertificates.CreateSelfSignedWithKey();
        var pfxPath = Path.Combine(_tmp, "client.pfx");
        File.WriteAllBytes(pfxPath, generated.Export(X509ContentType.Pfx, "pw"));

        Environment.SetEnvironmentVariable(GraphAuth.CertPathEnvVar, pfxPath);
        Environment.SetEnvironmentVariable(GraphAuth.CertPasswordEnvVar, "pw");
        Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "tid-pfx");
        Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "cid-pfx");

        Assert.True(GraphAuth.CertificateConfigured);
        var credential = GraphAuth.CreateCredential();
        Assert.IsType<Azure.Identity.ClientAssertionCredential>(credential);

        // Replicate the exact closure body CreateCredential builds, over the same
        // configured certificate, and hammer it.
        using var cert = GraphAuth.LoadConfiguredCertificate(out _);
        var host = GraphAuth.AuthorityHost();
        Func<string> assertionFactory = () => ClientAssertionJwt.Build(cert, "tid-pfx", "cid-pfx", host);

        const int threads = 24;
        const int perThread = 200;
        var jwts = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try { jwts.Enqueue(assertionFactory()); }
                catch (Exception exc) { errors.Enqueue($"{exc.GetType().Name}: {exc.Message}"); }
            }
        });

        Assert.True(errors.IsEmpty, string.Join(" | ", errors.Take(5)));
        Assert.Equal(threads * perThread, jwts.Count);
        var invalid = 0;
        foreach (var jwt in jwts)
        {
            try { VerifyAssertion(jwt, cert, "tid-pfx", "cid-pfx", host); }
            catch { invalid++; }
        }
        Assert.Equal(0, invalid);

        var msg = $"{jwts.Count} PFX-key assertions via credential closure / {threads} threads; " +
                  "0 errors, 0 invalid signatures";
        _out.WriteLine("[A1 pfx-closure] " + msg);
        Round3Support.Report("A1_pfx_closure", msg);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PART A.2 — proxy + CA-bundle transport (X509Chain validation) under load
// ═════════════════════════════════════════════════════════════════════════════

[Collection("EnvVars")]
public sealed class Round3TlsValidationStressTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tmp = Directory.CreateTempSubdirectory("r3_tls_").FullName;
    private readonly string? _savedProxy = Environment.GetEnvironmentVariable("PROXY_URL");
    private readonly string? _savedBypass = Environment.GetEnvironmentVariable("PROXY_BYPASS");
    private readonly string? _savedCa = Environment.GetEnvironmentVariable("CA_BUNDLE_PATH");

    public Round3TlsValidationStressTests(ITestOutputHelper output)
    {
        _out = output;
        Environment.SetEnvironmentVariable("PROXY_URL", null);
        Environment.SetEnvironmentVariable("PROXY_BYPASS", null);
        Environment.SetEnvironmentVariable("CA_BUNDLE_PATH", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PROXY_URL", _savedProxy);
        Environment.SetEnvironmentVariable("PROXY_BYPASS", _savedBypass);
        Environment.SetEnvironmentVariable("CA_BUNDLE_PATH", _savedCa);
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ValidateWithAdditionalRoots_32Threads_EveryDecisionCorrectNoThrow()
    {
        // PRIME SUSPECT: a callback that shared one X509Chain across concurrent TLS
        // validations would race/corrupt. Drive four fixed cases through the real
        // callback from 32 threads and assert every accept/reject is correct with
        // zero exceptions leaking into the TLS stack.
        using var trustedCa = TestCertificates.CreateCa("CN=R3 Corp Inspection CA");
        using var trustedLeaf = TestCertificates.CreateLeaf(trustedCa, "CN=graph.trusted.local");
        using var unrelatedCa = TestCertificates.CreateCa("CN=R3 Unrelated CA");
        using var untrustedLeaf = TestCertificates.CreateLeaf(unrelatedCa, "CN=graph.untrusted.local");

        var extraRoots = new X509Certificate2Collection { trustedCa };

        const int threads = 32;
        const int perThread = 400;
        var wrong = new ConcurrentQueue<string>();
        var threw = new ConcurrentQueue<string>();
        var count = 0;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    // (1) trusted-by-bundle leaf → accept
                    if (!HttpClientFactory.ValidateWithAdditionalRoots(
                            trustedLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                        wrong.Enqueue("trusted leaf rejected");
                    // (2) unrelated-CA leaf → reject
                    if (HttpClientFactory.ValidateWithAdditionalRoots(
                            untrustedLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                        wrong.Enqueue("untrusted leaf accepted");
                    // (3) name mismatch → always reject, even if the root is trusted
                    if (HttpClientFactory.ValidateWithAdditionalRoots(
                            trustedLeaf, null,
                            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
                            extraRoots))
                        wrong.Enqueue("name mismatch accepted");
                    // (4) errors None → accept without consulting the custom store
                    if (!HttpClientFactory.ValidateWithAdditionalRoots(
                            trustedLeaf, null, SslPolicyErrors.None, extraRoots))
                        wrong.Enqueue("clean chain rejected");
                    // (5) missing cert → reject
                    if (HttpClientFactory.ValidateWithAdditionalRoots(
                            null, null, SslPolicyErrors.RemoteCertificateNotAvailable, extraRoots))
                        wrong.Enqueue("null cert accepted");
                    Interlocked.Add(ref count, 5);
                }
                catch (Exception exc)
                {
                    threw.Enqueue($"{exc.GetType().Name}: {exc.Message}");
                }
            }
        });
        sw.Stop();

        Assert.True(threw.IsEmpty, "callback threw into TLS stack: " + string.Join(" | ", threw.Take(5)));
        Assert.True(wrong.IsEmpty, "wrong validation decision(s): " + string.Join(" | ", wrong.Take(5)));
        Assert.Equal(threads * perThread * 5, count);

        var msg = $"{count} validations / {threads} threads in {sw.ElapsedMilliseconds} ms " +
                  $"({count * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0}/s); " +
                  "0 exceptions, 0 wrong accept/reject decisions";
        _out.WriteLine("[A2 tls-validate] " + msg);
        Round3Support.Report("A2_tls_validate", msg);
    }

    [Fact]
    public void CreateHandler_WithCaBundle_UnderConcurrency_NoThrowNoLeak()
    {
        // Handler construction with a valid CA bundle from 32 threads: each handler
        // must carry the additive-trust callback, construct without throwing, and
        // dispose cleanly (no deadlock, no leaked native chain state).
        using var ca = TestCertificates.CreateCa();
        var pem = Path.Combine(_tmp, "roots.pem");
        File.WriteAllText(pem, ca.ExportCertificatePem() + "\n");
        Environment.SetEnvironmentVariable("CA_BUNDLE_PATH", pem);

        const int threads = 32;
        var errors = new ConcurrentQueue<string>();
        var built = 0;
        Parallel.For(0, threads * 8, i =>
        {
            try
            {
                using var handler = HttpClientFactory.CreateHandler(maxConnectionsPerServer: 20);
                if (handler.SslOptions.RemoteCertificateValidationCallback is null)
                    errors.Enqueue("missing additive-trust callback");
                if (handler.MaxConnectionsPerServer != 20)
                    errors.Enqueue("max connections not applied");
                Interlocked.Increment(ref built);
            }
            catch (Exception exc)
            {
                errors.Enqueue($"{exc.GetType().Name}: {exc.Message}");
            }
        });

        Assert.True(errors.IsEmpty, string.Join(" | ", errors.Take(5)));
        Assert.Equal(threads * 8, built);
        _out.WriteLine($"[A2 handler-build] {built} handlers built concurrently with CA bundle; 0 errors");
        Round3Support.Report("A2_handler_build", $"{built} handlers, 0 errors");
    }

    [Fact]
    public void BrokenCaBundle_FailsFastNamingTheSetting_ConsistentlyUnderConcurrency()
    {
        // A set-but-broken CA_BUNDLE_PATH must fail fast at construction naming the
        // setting — every time, from every thread (no partial/None-trust handler
        // ever escapes construction).
        Environment.SetEnvironmentVariable("CA_BUNDLE_PATH", Path.Combine(_tmp, "does-not-exist.pem"));
        var nonFailFast = 0;
        var wrongMessage = new ConcurrentQueue<string>();
        Parallel.For(0, 64, i =>
        {
            try
            {
                using var h = HttpClientFactory.CreateHandler();
                Interlocked.Increment(ref nonFailFast);  // must not reach here
            }
            catch (InvalidOperationException exc)
            {
                if (!exc.Message.Contains("CA_BUNDLE_PATH"))
                    wrongMessage.Enqueue(exc.Message);
            }
        });
        Assert.Equal(0, nonFailFast);
        Assert.True(wrongMessage.IsEmpty, string.Join(" | ", wrongMessage.Take(3)));
        _out.WriteLine("[A2 fail-fast] broken CA_BUNDLE_PATH aborted construction 64/64 times, naming the setting");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PART A.3 — Event Log sink under a logging flood
// ═════════════════════════════════════════════════════════════════════════════

[Collection("EnvVars")]
public sealed class Round3EventLogFloodTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string? _savedEnabled = Environment.GetEnvironmentVariable("EVENTLOG_ENABLED");
    private readonly string? _savedLevel = Environment.GetEnvironmentVariable("EVENTLOG_LEVEL");

    public Round3EventLogFloodTests(ITestOutputHelper output)
    {
        _out = output;
        Logging.JsonFormat = false;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EVENTLOG_ENABLED", _savedEnabled);
        Environment.SetEnvironmentVariable("EVENTLOG_LEVEL", _savedLevel);
        EventLogSink.DetachForTests();
        Logging.ResetJsonFormatCache();
    }

    private sealed class CountingWriter : IEventLogWriter
    {
        private int _error, _warning, _info, _attempts;
        public Exception? ThrowOnWrite;
        public int Error => Volatile.Read(ref _error);
        public int Warning => Volatile.Read(ref _warning);
        public int Info => Volatile.Read(ref _info);
        public int Attempts => Volatile.Read(ref _attempts);

        public void Write(EventLogEntrySeverity severity, string message, int eventId)
        {
            Interlocked.Increment(ref _attempts);
            if (ThrowOnWrite != null)
                throw ThrowOnWrite;
            switch (severity)
            {
                case EventLogEntrySeverity.Error: Interlocked.Increment(ref _error); break;
                case EventLogEntrySeverity.Warning: Interlocked.Increment(ref _warning); break;
                default: Interlocked.Increment(ref _info); break;
            }
        }
    }

    private static LogRecord Rec(int level, string msg = "flood") =>
        new() { Name = "salesforce_connector", Level = level, Message = msg };

    [Fact]
    public void Flood_32Threads_NeverThrows_LevelMappingHolds()
    {
        var writer = new CountingWriter();
        var sink = new EventLogSink(writer, mirrorInfo: false);

        const int threads = 32;
        const int perThread = 2000;
        // Deterministic level mix per iteration index: Error, Warning, Info, Debug.
        var expectedError = 0;
        var expectedWarning = 0;
        var threw = new ConcurrentQueue<string>();

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                var level = (i % 4) switch
                {
                    0 => LogLevels.Error,
                    1 => LogLevels.Warning,
                    2 => LogLevels.Info,
                    _ => LogLevels.Debug,
                };
                try { sink.Handle(Rec(level)); }
                catch (Exception exc) { threw.Enqueue($"{exc.GetType().Name}: {exc.Message}"); }
            }
        });
        sw.Stop();

        // Per thread: perThread/4 each of Error/Warning/Info/Debug.
        expectedError = threads * (perThread / 4);
        expectedWarning = threads * (perThread / 4);

        Assert.True(threw.IsEmpty, "sink threw under flood: " + string.Join(" | ", threw.Take(5)));
        Assert.Equal(expectedError, writer.Error);
        Assert.Equal(expectedWarning, writer.Warning);
        Assert.Equal(0, writer.Info);   // INFO/DEBUG not mirrored by default
        Assert.Equal(expectedError + expectedWarning, writer.Attempts);

        var msg = $"{threads * perThread} records / {threads} threads in {sw.ElapsedMilliseconds} ms; " +
                  $"mirrored {writer.Error} error + {writer.Warning} warning, dropped info/debug; 0 throws";
        _out.WriteLine("[A3 eventlog-flood] " + msg);
        Round3Support.Report("A3_eventlog_flood", msg);
    }

    [Fact]
    public void Flood_WriterAlwaysThrows_SinkStillNeverThrows()
    {
        // The never-throw contract must hold even when EVERY OS write fails
        // (missing source / ACL denial / full log), from every thread.
        var writer = new CountingWriter { ThrowOnWrite = new UnauthorizedAccessException("source not registered") };
        var sink = new EventLogSink(writer, mirrorInfo: false);
        var threw = new ConcurrentQueue<string>();

        Parallel.For(0, 32, t =>
        {
            for (var i = 0; i < 1000; i++)
            {
                try { sink.Handle(Rec(LogLevels.Error)); }
                catch (Exception exc) { threw.Enqueue($"{exc.GetType().Name}: {exc.Message}"); }
            }
        });

        Assert.True(threw.IsEmpty, "sink surfaced writer failure: " + string.Join(" | ", threw.Take(5)));
        Assert.Equal(32 * 1000, writer.Attempts);   // every record still attempted
        Assert.Equal(0, writer.Error);               // ...and every write swallowed
        _out.WriteLine("[A3 never-throw] 32000 always-failing writes swallowed; 0 throws");
    }

    [Fact]
    public void AttachIfEnabled_OffWindows_IsStrictNoOp_UnderConcurrentAttach()
    {
        Environment.SetEnvironmentVariable("EVENTLOG_ENABLED", "true");
        var before = Logging.Root.Handlers.Count;
        Parallel.For(0, 32, _ => EventLogSink.AttachIfEnabled());
        var after = Logging.Root.Handlers.Count;

        if (OperatingSystem.IsWindows())
            Assert.Equal(before + 1, after);   // attached exactly once even under a concurrent attach storm
        else
            Assert.Equal(before, after);       // strict no-op off Windows
        _out.WriteLine($"[A3 attach] concurrent AttachIfEnabled: handlers {before} → {after} " +
                       $"({(OperatingSystem.IsWindows() ? "windows: +1 once" : "non-windows: no-op")})");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PART A.4 — dead-letter redaction under concurrent producers
// ═════════════════════════════════════════════════════════════════════════════

[Collection("EnvVars")]
public sealed class Round3DeadLetterRedactionStressTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tmp = Directory.CreateTempSubdirectory("r3_dl_").FullName;
    private readonly string _savedLogsDir = SyncState.LogsDir;
    private readonly string? _savedMode = Environment.GetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar);
    private readonly string? _savedUseSql = Environment.GetEnvironmentVariable("USE_SQL_SERVER");

    public Round3DeadLetterRedactionStressTests(ITestOutputHelper output)
    {
        _out = output;
        SyncState.LogsDir = _tmp;
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "redacted");
        Environment.SetEnvironmentVariable("USE_SQL_SERVER", null);
        SyncState.ResetProviderCache();
    }

    public void Dispose()
    {
        SyncState.LogsDir = _savedLogsDir;
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, _savedMode);
        Environment.SetEnvironmentVariable("USE_SQL_SERVER", _savedUseSql);
        SyncState.ResetProviderCache();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private const string PiiName = "ACME_SECRET_ACCOUNT_NAME";
    private const string PiiEmail = "victim.secret@pii.example";
    private const string PiiBody = "SECRET_CASE_NOTES_customer_ssn_123_45_6789";

    private static JsonObject RequestBodyWithPii(string itemId) => new()
    {
        ["id"] = itemId,
        ["acl"] = new JsonArray { new JsonObject { ["type"] = "user", ["value"] = "aad-guid-" + itemId } },
        ["properties"] = new JsonObject
        {
            ["AccountName"] = PiiName,
            ["Email"] = PiiEmail,
            ["Revenue"] = 4_200_000,
        },
        ["content"] = new JsonObject { ["type"] = "text", ["value"] = PiiBody },
    };

    [Fact]
    public void ConcurrentProducers_RedactedMode_NoTornRecords_NoPiiSurvives_RetryStillWorks()
    {
        Assert.True(DeadLetterRedaction.RedactedMode);
        Assert.False(SyncState.UseSqlServer);

        const string connectorId = "round3dl";
        var path = SyncState.FailedRecordsPath(connectorId);
        const int producers = 32;
        const int perProducer = 25;

        var errors = new ConcurrentQueue<string>();
        Parallel.For(0, producers, new ParallelOptions { MaxDegreeOfParallelism = producers }, p =>
        {
            try
            {
                var failures = new List<(string ItemId, string Error)>();
                var requestBodies = new Dictionary<string, JsonNode?>();
                var responseBodies = new Dictionary<string, JsonNode?>();
                for (var i = 0; i < perProducer; i++)
                {
                    var itemId = $"P{p:D2}-I{i:D2}";
                    failures.Add((itemId, "Graph 400: property validation failed"));
                    requestBodies[itemId] = RequestBodyWithPii(itemId);
                    // Response = Graph's error envelope. Redaction intentionally KEEPS
                    // error details for triage, so this must reference the field NAME,
                    // never a raw PII value (those live only in the request payload's
                    // properties/content, which the redactor hashes).
                    responseBodies[itemId] = new JsonObject
                    {
                        ["status"] = 400,
                        ["body"] = new JsonObject { ["error"] = new JsonObject { ["message"] = "property validation failed for AccountName" } },
                    };
                }
                SyncState.AppendFailedRecords(path, failures, "Account", "", requestBodies, responseBodies);
            }
            catch (Exception exc)
            {
                errors.Enqueue($"{exc.GetType().Name}: {exc.Message}");
            }
        });

        Assert.True(errors.IsEmpty, string.Join(" | ", errors.Take(5)));

        // 1. No torn records: EVERY line is well-formed JSON.
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        Assert.Equal(producers * perProducer, lines.Count);
        foreach (var line in lines)
            Assert.NotNull(JsonNode.Parse(line));  // throws on a torn/interleaved line

        // 2. No PII value survives anywhere in the file.
        var whole = File.ReadAllText(path);
        Assert.DoesNotContain(PiiName, whole);
        Assert.DoesNotContain(PiiEmail, whole);
        Assert.DoesNotContain(PiiBody, whole);

        // 3. Retry-critical ids/field names + hashes retained; the @redaction note present.
        var records = SyncState.ReadFailedRecords(connectorId);
        Assert.Equal(producers * perProducer, records.Count);
        var seenIds = new HashSet<string>();
        foreach (var rec in records)
        {
            var itemId = rec["item_id"]!.GetValue<string>();
            Assert.True(seenIds.Add(itemId), "duplicate/torn item_id " + itemId);
            Assert.Equal("Account", rec["object_type"]!.GetValue<string>());  // retry re-fetches by (item_id, object_type)

            var reqBody = rec["request_body"]!.AsObject();
            var props = reqBody["properties"]!.AsObject();
            Assert.StartsWith("sha256:", props["AccountName"]!.GetValue<string>());  // value hashed
            Assert.StartsWith("sha256:", props["Email"]!.GetValue<string>());
            Assert.True(props.ContainsKey("AccountName") && props.ContainsKey("Email"));  // field NAMES kept
            Assert.StartsWith("sha256:", reqBody["content"]!.AsObject()["value"]!.GetValue<string>());
            Assert.Equal("text", reqBody["content"]!.AsObject()["type"]!.GetValue<string>());  // non-value content kept
            Assert.Equal(DeadLetterRedaction.Note, reqBody[DeadLetterRedaction.NoteKey]!.GetValue<string>());
            // ids/acl retained for triage.
            Assert.Equal(itemId, reqBody["id"]!.GetValue<string>());
        }
        Assert.Equal(producers * perProducer, seenIds.Count);

        var msg = $"{records.Count} redacted dead-letter records from {producers} concurrent producers; " +
                  "every line valid JSON, 0 PII values survived, ids+field-names+hashes retained (retry re-fetch intact)";
        _out.WriteLine("[A4 deadletter-redact] " + msg);
        Round3Support.Report("A4_deadletter_redact", msg);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PART A.5 — metric counters under concurrency
// ═════════════════════════════════════════════════════════════════════════════

public sealed class Round3MetricsConcurrencyTests
{
    private readonly ITestOutputHelper _out;
    public Round3MetricsConcurrencyTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ConcurrentIncrements_Reconcile_NoTearing()
    {
        Metrics.ResetForTests();
        try
        {
            const int threads = 32;
            const int iters = 2000;
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                for (var i = 0; i < iters; i++)
                {
                    Metrics.IncItemsIngested();       // +1
                    Metrics.IncItemsFailed(2);        // +2
                    Metrics.IncThrottle429();         // +1
                    Metrics.AddObjectFetched("Account", 3);
                    Metrics.IncHaClaimsHeld();
                    Metrics.DecHaClaimsHeld();        // balanced → net 0
                }
            });

            long n = (long)threads * iters;
            Assert.Equal(n, Metrics.ItemsIngested);
            Assert.Equal(2 * n, Metrics.ItemsFailed);
            Assert.Equal(n, Metrics.Throttle429Total);
            Assert.Equal(0, Metrics.HaClaimsHeld);   // every inc matched by a dec

            // Per-object labeled gauge reconciles exactly (rendered value == 3n).
            var render = Metrics.RenderPrometheus();
            Assert.Contains($"salesforce_connector_object_records_fetched{{object_type=\"Account\"}} {3 * n}", render);

            var msg = $"{threads} threads × {iters} iters: ingested={Metrics.ItemsIngested} " +
                      $"failed={Metrics.ItemsFailed} 429={Metrics.Throttle429Total} " +
                      $"account_fetched={3 * n} ha_claims={Metrics.HaClaimsHeld}; all reconcile, 0 torn";
            _out.WriteLine("[A5 metrics] " + msg);
            Round3Support.Report("A5_metrics", msg);
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }

    [Fact]
    public void HaClaimsGauge_ClampsAtZero_UnderConcurrentOverDecrement()
    {
        Metrics.ResetForTests();
        try
        {
            // More decrements than increments, concurrently: the gauge must never
            // go negative (double-dispose / over-release must clamp at 0).
            Parallel.For(0, 32, t =>
            {
                for (var i = 0; i < 500; i++)
                {
                    Metrics.DecHaClaimsHeld();   // over-decrement (gauge starts at 0)
                    Assert.True(Metrics.HaClaimsHeld >= 0, "gauge went negative");
                }
            });
            Assert.Equal(0, Metrics.HaClaimsHeld);
            _out.WriteLine("[A5 clamp] 16000 concurrent over-decrements; gauge stayed at 0 (never negative)");
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PART B — regression: enterprise pack did NOT regress round-1/2 resolver invariants
// ═════════════════════════════════════════════════════════════════════════════

public sealed class Round3RegressionInterleaveTests
{
    private readonly ITestOutputHelper _out;
    public Round3RegressionInterleaveTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ResolverInvariants_HoldWhileEnterprisePathsUnderLoad()
    {
        // Run the round-1/2 hardened resolver invariants (memoized closure, cycle
        // detection, depth cap, single-flight prewarm snapshot, seat-never-everyone)
        // across mutation epochs WHILE the enterprise-pack paths (cert signing, TLS
        // validation, metrics) are hammered on background threads. A regression that
        // introduced cross-talk (shared static state, a global lock) would surface
        // as a stale grant, a hang, or a corrupted assertion/validation.
        using var cert = TestCertificates.CreateSelfSignedWithKey();
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca);
        using var otherCa = TestCertificates.CreateCa("CN=Other");
        var extraRoots = new X509Certificate2Collection { ca };

        using var stop = new CancellationTokenSource();
        var bgErrors = new ConcurrentQueue<string>();
        long jwtCount = 0, tlsCount = 0;

        // Background load: enterprise paths hammered until the resolver work is done.
        var bg = new List<Task>
        {
            Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        var jwt = ClientAssertionJwt.Build(cert, "tid", "cid", "https://login.microsoftonline.com");
                        var (_, _, sig, input) = Round3Support.DecodeJwt(jwt);
                        using var pub = cert.GetRSAPublicKey()!;
                        if (!pub.VerifyData(Encoding.ASCII.GetBytes(input), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                            bgErrors.Enqueue("bg jwt invalid");
                        Interlocked.Increment(ref jwtCount);
                    }
                    catch (Exception exc) { bgErrors.Enqueue("jwt: " + exc.Message); }
                }
            }),
            Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        if (!HttpClientFactory.ValidateWithAdditionalRoots(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                            bgErrors.Enqueue("bg trusted rejected");
                        if (HttpClientFactory.ValidateWithAdditionalRoots(TestCertificates.CreateLeaf(otherCa), null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                            bgErrors.Enqueue("bg untrusted accepted");
                        Interlocked.Add(ref tlsCount, 2);
                    }
                    catch (Exception exc) { bgErrors.Enqueue("tls: " + exc.Message); }
                }
            }),
            Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Metrics.IncItemsIngested();
                    Metrics.IncThrottle429();
                }
            }),
        };

        // Foreground: the resolver churn invariant (oracle match every epoch).
        var g = Round2Support.BuildRandomGraph(seed: 303, nGroups: 80, nUsers: 60, orgEvery: 17);
        var resolver = Round2Support.MakeResolver(g);
        var totalResolves = 0;
        var sw = Stopwatch.StartNew();
        for (var epoch = 0; epoch < 8; epoch++)
        {
            if (epoch > 0)
            {
                g = Round2Support.Mutate(g, seed: 303_000 + epoch);
                Round2Support.InstallGraph(resolver, g);
                Round2Support.ClearGroupCache(resolver);
            }
            var epochGraph = g;
            var epochNo = epoch;
            var mismatches = new ConcurrentQueue<string>();
            var tasks = new List<Task>();
            foreach (var gid in epochGraph.GroupIds)
            {
                var captured = gid;
                tasks.Add(Task.Run(async () =>
                {
                    var (users, everyone) = await resolver.ResolveGroupAsync(captured);
                    var (oUsers, oEveryone) = epochGraph.Oracle(captured);
                    if (!oUsers.SetEquals(users) || oEveryone != everyone)
                        mismatches.Enqueue($"epoch={epochNo} group={captured}: exp {oUsers.Count}/{oEveryone}, got {users.Count}/{everyone}");
                    Interlocked.Increment(ref totalResolves);
                }));
            }
            await Round3Support.AwaitBoundedAsync(Task.WhenAll(tasks), 60, $"epoch {epoch} resolve wave under load");
            Assert.True(mismatches.IsEmpty, "resolver regressed under enterprise load:\n" + string.Join("\n", mismatches.Take(5)));
        }
        sw.Stop();

        stop.Cancel();
        await Round3Support.AwaitBoundedAsync(Task.WhenAll(bg), 30, "background enterprise load shutdown");

        Assert.True(bgErrors.IsEmpty, "enterprise path failed under interleaved load: " + string.Join(" | ", bgErrors.Take(5)));
        Assert.True(Interlocked.Read(ref jwtCount) > 0 && Interlocked.Read(ref tlsCount) > 0, "background load did not run");

        var msg = $"8 epochs × 80 groups = {totalResolves} resolves matched oracle in {sw.ElapsedMilliseconds} ms " +
                  $"while {Interlocked.Read(ref jwtCount)} JWTs + {Interlocked.Read(ref tlsCount)} TLS validations ran concurrently; " +
                  "0 stale grants, 0 corrupted assertions/validations";
        _out.WriteLine("[B regression-interleave] " + msg);
        Round3Support.Report("B_regression_interleave", msg);
    }

    [Fact]
    public async Task DepthCap_FailClosed_And_NeverEveryone_UnderEnterpriseLoad()
    {
        // Two more round-1/2 invariants under simultaneous enterprise-path load:
        //   * a group chain deeper than the 400-level cap terminates fail-closed
        //     (exactly the cap's worth of grants, never a stack overflow / hang);
        //   * a private (non-Organization) chain never resolves to "everyone".
        using var cert = TestCertificates.CreateSelfSignedWithKey();
        using var stop = new CancellationTokenSource();
        var bg = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                _ = ClientAssertionJwt.Build(cert, "t", "c", "https://login.microsoftonline.com");
        });

        var deep = Round2Support.LinearCycle(5000);   // beyond MaxGroupNestingDepth (400)
        var resolver = Round2Support.MakeResolver(deep);
        var work = Task.Run(async () =>
        {
            var (users, everyone) = await resolver.ResolveGroupAsync(deep.GroupIds[0]);
            Assert.False(everyone, "private cyclic chain must never resolve to everyone (seat-never-everyone)");
            // Fail-closed: expansion stops at the cap — a bounded, deterministic set,
            // never the full 5000 and never a crash.
            Assert.True(users.Count <= 400, $"depth cap breached: {users.Count} grants");
            return users.Count;
        });
        await Round3Support.AwaitBoundedAsync(work, 60, "depth-capped resolve under load");
        stop.Cancel();
        await Round3Support.AwaitBoundedAsync(bg, 30, "bg jwt shutdown");

        var granted = await work;
        _out.WriteLine($"[B depth-cap] 5000-deep chain under JWT load → {granted} grants (≤400 cap), everyone=false, no hang");
        Round3Support.Report("B_depth_cap", $"5000-deep chain → {granted} grants (fail-closed at ≤400), never-everyone held");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PART C — fresh adversarial dimensions
// ═════════════════════════════════════════════════════════════════════════════

public sealed class Round3AdversarialTests
{
    private readonly ITestOutputHelper _out;
    public Round3AdversarialTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ClockSkew_TokenExpiryWindow_StableUnderConcurrentVaryingClocks()
    {
        // Fresh dimension: token-expiry clock skew. Many threads build assertions
        // with WILDLY different `now` values (±10 min skew, simulating unsynced
        // fleet clocks) at once. Each assertion's window must be internally exact
        // — nbf == iat == its own `now`, exp == now + Lifetime — with no torn
        // exp/nbf from interleaving different clocks, and each must remain
        // signature-valid.
        using var cert = TestCertificates.CreateSelfSignedWithKey();
        var lifetime = (long)ClientAssertionJwt.Lifetime.TotalSeconds;
        const string host = "https://login.microsoftonline.com";

        var baseNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var skews = Enumerable.Range(0, 1200).Select(i => (i % 1200) - 600).ToArray();  // -600..+599 s

        var bad = new ConcurrentQueue<string>();
        Parallel.ForEach(skews, new ParallelOptions { MaxDegreeOfParallelism = 32 }, skew =>
        {
            var now = baseNow.AddSeconds(skew);
            var expectedIat = now.ToUnixTimeSeconds();
            for (var rep = 0; rep < 8; rep++)
            {
                var jwt = ClientAssertionJwt.Build(cert, "tid", "cid", host, now);
                var (_, payload, sig, input) = Round3Support.DecodeJwt(jwt);
                var iat = payload["iat"]!.GetValue<long>();
                var nbf = payload["nbf"]!.GetValue<long>();
                var exp = payload["exp"]!.GetValue<long>();
                if (iat != expectedIat) bad.Enqueue($"iat {iat} != {expectedIat} (skew {skew})");
                if (nbf != expectedIat) bad.Enqueue($"nbf {nbf} != iat {expectedIat}");
                if (exp != expectedIat + lifetime) bad.Enqueue($"exp {exp} != iat+{lifetime} (skew {skew})");
                using var pub = cert.GetRSAPublicKey()!;
                if (!pub.VerifyData(Encoding.ASCII.GetBytes(input), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    bad.Enqueue($"sig invalid (skew {skew})");
            }
        });

        Assert.True(bad.IsEmpty, "clock-skew produced torn/invalid windows: " + string.Join(" | ", bad.Take(5)));
        var msg = $"{skews.Length * 8} assertions across ±600s concurrent clock skew; " +
                  $"every window exact (nbf==iat, exp==iat+{lifetime}), every signature valid";
        _out.WriteLine("[C clock-skew] " + msg);
        Round3Support.Report("C_clock_skew", msg);
    }

    [Fact]
    public async Task SustainedInterleavedSoak_AllEnterprisePaths_3x_NoErrors()
    {
        // Fresh dimension: a sustained interleaved soak at 3x concurrency running
        // ALL enterprise paths simultaneously for a fixed wall-clock window — cert
        // signing+verify, TLS accept AND reject, event-log flood, metric
        // increments — proving they coexist with no errors, no deadlock, and no
        // torn metric under continuous mixed pressure. Captures real throughput.
        using var cert = TestCertificates.CreateSelfSignedWithKey();
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca);
        using var otherCa = TestCertificates.CreateCa("CN=Soak Other");
        using var otherLeaf = TestCertificates.CreateLeaf(otherCa);
        var extraRoots = new X509Certificate2Collection { ca };

        var sinkWriter = new SoakWriter();
        var sink = new EventLogSink(sinkWriter, mirrorInfo: false);

        Metrics.ResetForTests();
        var errors = new ConcurrentQueue<string>();
        long jwts = 0, tls = 0, logs = 0;
        var duration = TimeSpan.FromSeconds(3);
        using var stop = new CancellationTokenSource(duration);
        var ct = stop.Token;

        // 3x concurrency: 3 workers per path family (12 workers total), each on a
        // DEDICATED thread rather than the pool. These loops are tight and never
        // await, so pool-scheduled they occupy every worker thread they get and the
        // ones queued behind them wait on thread injection, which is roughly one
        // new thread per 500 ms. On a two-core Windows runner the metrics workers -
        // queued last - had not started before the 3 s window closed, so they ran
        // ZERO iterations: the reconciliation below then found no Soak sample at
        // all (ingested=0, no rendered line) and the test failed every time. It was
        // measuring thread-pool injection, not the paths it claims to soak.
        var workers = new List<Task>();
        for (var k = 0; k < 3; k++)
        {
            workers.Add(Task.Factory.StartNew(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var jwt = ClientAssertionJwt.Build(cert, "tid", "cid", "https://login.microsoftonline.com");
                        var (_, _, sig, input) = Round3Support.DecodeJwt(jwt);
                        using var pub = cert.GetRSAPublicKey()!;
                        if (!pub.VerifyData(Encoding.ASCII.GetBytes(input), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                            errors.Enqueue("soak jwt invalid");
                        Interlocked.Increment(ref jwts);
                    }
                    catch (Exception exc) { errors.Enqueue("jwt: " + exc.Message); }
                }
            }, TaskCreationOptions.LongRunning));
            workers.Add(Task.Factory.StartNew(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (!HttpClientFactory.ValidateWithAdditionalRoots(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                            errors.Enqueue("soak trusted rejected");
                        if (HttpClientFactory.ValidateWithAdditionalRoots(otherLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                            errors.Enqueue("soak untrusted accepted");
                        Interlocked.Add(ref tls, 2);
                    }
                    catch (Exception exc) { errors.Enqueue("tls: " + exc.Message); }
                }
            }, TaskCreationOptions.LongRunning));
            workers.Add(Task.Factory.StartNew(() =>
            {
                var i = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        sink.Handle(new LogRecord
                        {
                            Name = "salesforce_connector",
                            Level = (i++ % 2 == 0) ? LogLevels.Error : LogLevels.Warning,
                            Message = "soak",
                        });
                        Interlocked.Increment(ref logs);
                    }
                    catch (Exception exc) { errors.Enqueue("log: " + exc.Message); }
                }
            }, TaskCreationOptions.LongRunning));
            workers.Add(Task.Factory.StartNew(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    Metrics.IncItemsIngested();
                    Metrics.AddObjectFetched("Soak", 1);
                }
            }, TaskCreationOptions.LongRunning));
        }

        await Round3Support.AwaitBoundedAsync(Task.WhenAll(workers), 30, "3x interleaved soak");

        Assert.True(errors.IsEmpty, "soak produced errors: " + string.Join(" | ", errors.Take(5)));
        // Metric reconciliation: every mirrored log was Error or Warning (no info leak),
        // and the labeled gauge equals the number of AddObjectFetched calls.
        Assert.Equal(0, sinkWriter.Info);
        Assert.Equal(sinkWriter.Error + sinkWriter.Warning, sinkWriter.Attempts);
        var render = Metrics.RenderPrometheus();
        Assert.Contains($"salesforce_connector_object_records_fetched{{object_type=\"Soak\"}} {Metrics.ItemsIngested}", render);
        Metrics.ResetForTests();

        var jwtN = Interlocked.Read(ref jwts);
        var tlsN = Interlocked.Read(ref tls);
        var logN = Interlocked.Read(ref logs);
        var secs = duration.TotalSeconds;
        var msg = $"3s @ 3x/path: {jwtN} JWTs ({jwtN / secs:F0}/s), {tlsN} TLS validations ({tlsN / secs:F0}/s), " +
                  $"{logN} event-log mirrors ({logN / secs:F0}/s), all paths interleaved; 0 errors, metrics reconcile";
        _out.WriteLine("[C soak-3x] " + msg);
        Round3Support.Report("C_soak_3x", msg);
    }

    private sealed class SoakWriter : IEventLogWriter
    {
        private int _error, _warning, _info, _attempts;
        public int Error => Volatile.Read(ref _error);
        public int Warning => Volatile.Read(ref _warning);
        public int Info => Volatile.Read(ref _info);
        public int Attempts => Volatile.Read(ref _attempts);

        public void Write(EventLogEntrySeverity severity, string message, int eventId)
        {
            Interlocked.Increment(ref _attempts);
            switch (severity)
            {
                case EventLogEntrySeverity.Error: Interlocked.Increment(ref _error); break;
                case EventLogEntrySeverity.Warning: Interlocked.Increment(ref _warning); break;
                default: Interlocked.Increment(ref _info); break;
            }
        }
    }
}
