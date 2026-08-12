// Enterprise hardening package: Windows Event Log mirroring (EVENTLOG_ENABLED),
// proxy + custom-CA connectivity (PROXY_URL / PROXY_BYPASS / CA_BUNDLE_PATH),
// certificate-based Graph auth (GRAPH_CLIENT_CERT_*: RS256 client assertion),
// the new operational metrics, and the MSI authoring source (WiX v5) drift
// guards. All offline; certs are generated in-test.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ---- shared helpers --------------------------------------------------------------

internal static class HardeningFixtures
{
    /// <summary>Self-signed RSA cert (CA-capable so it can act as its own
    /// trust root in chain tests). Valid yesterday → +2 years.</summary>
    public static X509Certificate2 NewSelfSignedRsa(string subject = "CN=AltrataHardeningTest")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("AltrataConnector.sln").Length == 0)
            dir = dir.Parent;
        Assert.True(dir != null,
            "could not locate repo root (AltrataConnector.sln) above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}

/// <summary>Capturing Event Log mirror (installed via EventLogSink.Override).</summary>
internal sealed class FakeEventLogMirror : IEventLogMirror
{
    public List<(EventLogSeverity Severity, int EventId, string Message)> Entries { get; } = new();

    public void Write(EventLogSeverity severity, int eventId, string message)
    {
        lock (Entries)
            Entries.Add((severity, eventId, message));
    }
}

// ---- Windows Event Log sink ------------------------------------------------------

public class EventLogSinkTests : IDisposable
{
    private readonly FakeEventLogMirror _mirror = new();

    public EventLogSinkTests()
    {
        EventLogSink.Override = _mirror;
    }

    public void Dispose()
    {
        EventLogSink.Override = null;
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, null);
        Environment.SetEnvironmentVariable("LOGS_DIR", null);
        Logging.ResetForTests();
    }

    [Fact]
    public void DisabledByDefaultEvenWithAMirrorInstalled()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, null);
        Logging.GetLogger("test").Error("an error line");
        Assert.Empty(_mirror.Entries);
        Assert.False(EventLogSink.Enabled);
    }

    [Fact]
    public void MirrorsWarningAndErrorWithStableEventIdsButNeverInfo()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, "true");
        var logger = Logging.GetLogger("altrata_connector.test");
        logger.Info("info line");
        logger.Warning("warn line");
        logger.Error("error line", new InvalidOperationException("boom"));

        Assert.Equal(2, _mirror.Entries.Count);
        var warn = _mirror.Entries[0];
        Assert.Equal(EventLogSeverity.Warning, warn.Severity);
        Assert.Equal(EventLogSink.EventIdWarning, warn.EventId);
        Assert.Contains("warn line", warn.Message);
        Assert.Contains("altrata_connector.test", warn.Message);

        var error = _mirror.Entries[1];
        Assert.Equal(EventLogSeverity.Error, error.Severity);
        Assert.Equal(EventLogSink.EventIdError, error.EventId);
        Assert.Contains("error line", error.Message);
        Assert.Contains("InvalidOperationException", error.Message);   // exception summary rides along
    }

    [Fact]
    public void StartRunEmitsALifecycleMarker()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, "true");
        Environment.SetEnvironmentVariable("LOGS_DIR", TestFixtures.NewTempDir("evtlog"));
        RunLog.StartRun("hardening");
        Assert.Contains(_mirror.Entries, e =>
            e.EventId == EventLogSink.EventIdLifecycle
            && e.Severity == EventLogSeverity.Information
            && e.Message.Contains("Run started: hardening"));
    }

    [Fact]
    public void OversizedMessagesAreTruncatedNotDropped()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, "true");
        Logging.GetLogger("test").Warning(new string('x', EventLogSink.MaxMessageLength + 5_000));
        var entry = Assert.Single(_mirror.Entries);
        Assert.True(entry.Message.Length <= EventLogSink.MaxMessageLength + 20);
        Assert.EndsWith("[truncated]", entry.Message);
    }

    [Fact]
    public void AThrowingMirrorNeverBreaksLogging()
    {
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, "true");
        EventLogSink.Override = new ThrowingMirror();
        var exception = Record.Exception(() => Logging.GetLogger("test").Error("still fine"));
        Assert.Null(exception);
    }

    private sealed class ThrowingMirror : IEventLogMirror
    {
        public void Write(EventLogSeverity severity, int eventId, string message) =>
            throw new UnauthorizedAccessException("no event log for you");
    }

    [Fact]
    public async Task NoPersonalDataReachesTheMirroredEventLogMessages()
    {
        // Same discipline as the span/no-PII proof: run a crawl loaded with PII
        // that produces WARNING (dead-letter) and ERROR (checksum reject) lines,
        // then assert none of the sensitive literals appear in ANY mirrored
        // message — the Event Log is one more log surface, not a softer one.
        Environment.SetEnvironmentVariable(EventLogSink.EnvVar, "true");
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, """
                [{"id":"P1","person_name":"Ada Lovelace","email":"ada@contoso.com","net_worth_usd":"9000000","employer":"Analytical Engines"},
                 {"id":"P2","person_name":"Grace Hopper","email":"grace@contoso.com","net_worth_usd":"7000000","employer":"Compilers Inc"}]
                """, 2));
        var tampered = TestFixtures.WriteDelivery(harness.FeedPath, "d2",
            ("persons.json", Datasets.PersonProfile,
                """[{"id":"P9","person_name":"Alan Turing","email":"alan@contoso.com"}]""", 1));
        File.AppendAllText(Path.Combine(tampered.Directory, "persons.json"), "tampered");
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");   // → WARNING dead-letter line

        await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.NotEmpty(_mirror.Entries);                              // dispatch really happened
        Assert.Contains(_mirror.Entries, e => e.Severity == EventLogSeverity.Warning);
        Assert.Contains(_mirror.Entries, e => e.Severity == EventLogSeverity.Error);
        var blob = string.Join("\n", _mirror.Entries.Select(e => e.Message));
        foreach (var secret in new[]
                 {
                     "Ada Lovelace", "ada@contoso.com", "9000000", "Analytical Engines",
                     "Grace Hopper", "grace@contoso.com", "7000000", "Compilers Inc",
                     "Alan Turing", "alan@contoso.com",
                 })
            Assert.DoesNotContain(secret, blob);
    }
}

// ---- proxy + custom CA -----------------------------------------------------------

public class HttpConnectivityTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyUrlEnvVar, null);
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyBypassEnvVar, null);
        Environment.SetEnvironmentVariable(HttpConnectivity.CaBundleEnvVar, null);
    }

    [Fact]
    public void UnsetEnvironmentMeansDirectHandler()
    {
        Assert.Null(HttpConnectivity.BuildProxy());
        Assert.Null(HttpConnectivity.LoadCaBundle());
        using var handler = HttpConnectivity.CreateHandler();
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Null(sockets.Proxy);
        Assert.Null(sockets.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void ProxyUrlAndBypassAreApplied()
    {
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyUrlEnvVar, "http://proxy.corp.local:8080");
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyBypassEnvVar,
            @"^https://otel\.corp\.local; ^https://sql\.corp\.local");
        var proxy = Assert.IsType<WebProxy>(HttpConnectivity.BuildProxy());
        Assert.Equal(new Uri("http://proxy.corp.local:8080"), proxy.Address);
        Assert.Equal(2, proxy.BypassList.Length);

        using var handler = HttpConnectivity.CreateHandler();
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.NotNull(sockets.Proxy);
        Assert.True(sockets.UseProxy);
    }

    [Fact]
    public void BadProxyUrlFailsFastNamingTheSetting()
    {
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyUrlEnvVar, "not a proxy url");
        var exc = Assert.Throws<ConfigurationError>(() => HttpConnectivity.BuildProxy());
        Assert.Contains("PROXY_URL", exc.Message);
    }

    [Fact]
    public void GraphClientConstructionFailsFastOnBadConnectivityConfig()
    {
        // The wiring proof: a bad proxy is rejected at client construction
        // (Runtime.Create → every command), naming the setting.
        Environment.SetEnvironmentVariable(HttpConnectivity.ProxyUrlEnvVar, "::: garbage :::");
        var exc = Assert.Throws<ConfigurationError>(() => new GraphClient(TestFixtures.NewConfig()));
        Assert.Contains("PROXY_URL", exc.Message);
    }

    [Fact]
    public void MissingCaBundleFailsFastNamingTheSetting()
    {
        Environment.SetEnvironmentVariable(HttpConnectivity.CaBundleEnvVar,
            Path.Combine(TestFixtures.NewTempDir("ca"), "nope.pem"));
        var exc = Assert.Throws<ConfigurationError>(() => HttpConnectivity.LoadCaBundle());
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }

    [Fact]
    public void GarbageCaBundleFailsFastNamingTheSetting()
    {
        var path = Path.Combine(TestFixtures.NewTempDir("ca"), "garbage.pem");
        File.WriteAllText(path, "this is not a pem bundle");
        Environment.SetEnvironmentVariable(HttpConnectivity.CaBundleEnvVar, path);
        var exc = Assert.Throws<ConfigurationError>(() => HttpConnectivity.LoadCaBundle());
        Assert.Contains("CA_BUNDLE_PATH", exc.Message);
    }

    [Fact]
    public void SelfSignedServerCertIsTrustedViaTheBundleAdditively()
    {
        using var cert = HardeningFixtures.NewSelfSignedRsa("CN=tls-inspect.corp.local");
        var path = Path.Combine(TestFixtures.NewTempDir("ca"), "corp-roots.pem");
        File.WriteAllText(path, cert.ExportCertificatePem());
        Environment.SetEnvironmentVariable(HttpConnectivity.CaBundleEnvVar, path);

        var bundle = HttpConnectivity.LoadCaBundle();
        Assert.NotNull(bundle);
        Assert.Single(bundle!);

        // Chain errors + the cert's root in the bundle → accepted.
        Assert.True(HttpConnectivity.ValidateWithAdditiveTrust(
            cert, null, SslPolicyErrors.RemoteCertificateChainErrors, bundle));
        // Additive means the OS verdict stands when it already passed.
        Assert.True(HttpConnectivity.ValidateWithAdditiveTrust(
            cert, null, SslPolicyErrors.None, bundle));
    }

    [Fact]
    public void ACertNotRootedInTheBundleStaysRejected()
    {
        using var serverCert = HardeningFixtures.NewSelfSignedRsa("CN=unknown.example");
        using var unrelatedRoot = HardeningFixtures.NewSelfSignedRsa("CN=some-other-root");
        var bundle = new X509Certificate2Collection { unrelatedRoot };
        Assert.False(HttpConnectivity.ValidateWithAdditiveTrust(
            serverCert, null, SslPolicyErrors.RemoteCertificateChainErrors, bundle));
    }

    [Fact]
    public void HostnameMismatchIsNeverForgiven()
    {
        using var cert = HardeningFixtures.NewSelfSignedRsa("CN=tls-inspect.corp.local");
        var bundle = new X509Certificate2Collection { cert };
        Assert.False(HttpConnectivity.ValidateWithAdditiveTrust(
            cert, null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors,
            bundle));
    }
}

// ---- certificate credential (Graph client assertion) -----------------------------

public class CertificateCredentialTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar, null);
        Environment.SetEnvironmentVariable(CertificateCredential.CertPasswordEnvVar, null);
        Environment.SetEnvironmentVariable(CertificateCredential.ThumbprintEnvVar, null);
    }

    private static string WritePfx(X509Certificate2 cert, string password)
    {
        var path = Path.Combine(TestFixtures.NewTempDir("cert"), "graph-client.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, password));
        return path;
    }

    private static JsonElement DecodeSegment(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
    }

    [Fact]
    public void NotConfiguredByDefault()
    {
        Assert.False(CertificateCredential.Configured);
        Assert.Equal("client secret", CertificateCredential.ModeDescription);
    }

    [Fact]
    public void ClientAssertionCarriesTheAadContract()
    {
        using var cert = HardeningFixtures.NewSelfSignedRsa();
        var now = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
        const string audience = "https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token";

        var jwt = CertificateCredential.BuildClientAssertion(cert, "client-1", audience, now, "jti-42");
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = DecodeSegment(parts[0]);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal(CertificateCredential.Base64Url(SHA256.HashData(cert.RawData)),
            header.GetProperty("x5t#S256").GetString());

        var payload = DecodeSegment(parts[1]);
        Assert.Equal(audience, payload.GetProperty("aud").GetString());
        Assert.Equal("client-1", payload.GetProperty("iss").GetString());
        Assert.Equal("client-1", payload.GetProperty("sub").GetString());
        Assert.Equal("jti-42", payload.GetProperty("jti").GetString());
        var iat = payload.GetProperty("iat").GetInt64();
        Assert.Equal(iat - 60, payload.GetProperty("nbf").GetInt64());
        Assert.Equal(iat + (long)CertificateCredential.AssertionLifetime.TotalSeconds,
            payload.GetProperty("exp").GetInt64());
        Assert.Equal(CertificateCredential.ToUnixSeconds(now), iat);
    }

    [Fact]
    public void AssertionSignatureVerifiesWithTheCertificatePublicKey()
    {
        using var cert = HardeningFixtures.NewSelfSignedRsa();
        var jwt = CertificateCredential.BuildClientAssertion(
            cert, "client-1", "https://login.microsoftonline.com/t/oauth2/v2.0/token");
        var parts = jwt.Split('.');
        var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        var padded = parts[2].Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        var signature = Convert.FromBase64String(padded);

        using var rsa = cert.GetRSAPublicKey()!;
        Assert.True(rsa.VerifyData(signingInput, signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void LoadsPfxFromPathWithPassword()
    {
        using var cert = HardeningFixtures.NewSelfSignedRsa();
        var path = WritePfx(cert, "pfx-pass");
        Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar, path);
        Environment.SetEnvironmentVariable(CertificateCredential.CertPasswordEnvVar, "pfx-pass");

        Assert.True(CertificateCredential.Configured);
        using var loaded = CertificateCredential.Load();
        Assert.True(loaded.HasPrivateKey);
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
    }

    [Fact]
    public void MissingCertFileFailsFastNamingTheSetting()
    {
        Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar,
            Path.Combine(TestFixtures.NewTempDir("cert"), "missing.pfx"));
        var exc = Assert.Throws<ConfigurationError>(() => CertificateCredential.Load());
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", exc.Message);
    }

    [Fact]
    public void WrongPfxPasswordFailsFastNamingTheSetting()
    {
        using var cert = HardeningFixtures.NewSelfSignedRsa();
        Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar, WritePfx(cert, "right"));
        Environment.SetEnvironmentVariable(CertificateCredential.CertPasswordEnvVar, "wrong");
        var exc = Assert.Throws<ConfigurationError>(() => CertificateCredential.Load());
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", exc.Message);
    }

    [Fact]
    public void UnknownThumbprintFailsFastNamingTheSetting()
    {
        Environment.SetEnvironmentVariable(CertificateCredential.ThumbprintEnvVar,
            "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF");
        var exc = Assert.Throws<ConfigurationError>(() => CertificateCredential.Load());
        Assert.Contains("GRAPH_CLIENT_CERT_THUMBPRINT", exc.Message);
    }

    [Fact]
    public async Task TokenRequestUsesClientAssertionAndCertificateWinsOverSecret()
    {
        using var cert = HardeningFixtures.NewSelfSignedRsa();
        Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar, WritePfx(cert, "p"));
        Environment.SetEnvironmentVariable(CertificateCredential.CertPasswordEnvVar, "p");

        string? tokenBody = null;
        var handler = new ScriptedHandler();
        handler.Enqueue(request =>
        {
            tokenBody = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"tok","expires_in":3600}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        handler.EnqueueJson(200, "{}");

        // Config still carries a secret — the certificate must WIN.
        var client = new GraphClient(TestFixtures.NewConfig(), handler, (_, _) => Task.CompletedTask);
        await client.DeleteItemAsync("x1");

        Assert.NotNull(tokenBody);
        Assert.Contains("client_assertion_type=", tokenBody);
        Assert.Contains(Uri.EscapeDataString(CertificateCredential.ClientAssertionType), tokenBody!);
        Assert.Contains("client_assertion=", tokenBody);
        Assert.DoesNotContain("client_secret=", tokenBody);
    }

    [Fact]
    public async Task SecretFlowIsByteIdenticalWhenNoCertIsConfigured()
    {
        string? tokenBody = null;
        var handler = new ScriptedHandler();
        handler.Enqueue(request =>
        {
            tokenBody = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"tok","expires_in":3600}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        handler.EnqueueJson(200, "{}");
        var client = new GraphClient(TestFixtures.NewConfig(), handler, (_, _) => Task.CompletedTask);
        await client.DeleteItemAsync("x1");

        Assert.Contains("client_secret=secret", tokenBody);
        Assert.DoesNotContain("client_assertion", tokenBody);
    }
}

public class CertificateConfigTests : IDisposable
{
    public CertificateConfigTests()
    {
        foreach (var (k, v) in new[]
                 {
                     ("CONNECTOR_ID", "AltrataCertTest"), ("CONNECTOR_NAME", "t"),
                     ("CONNECTOR_DESCRIPTION", "t"), ("AAD_APP_CLIENT_ID", "c"),
                     ("AAD_APP_TENANT_ID", "t"),
                 })
            Environment.SetEnvironmentVariable(k, v);
    }

    public void Dispose()
    {
        foreach (var k in new[]
                 {
                     "CONNECTOR_ID", "CONNECTOR_NAME", "CONNECTOR_DESCRIPTION", "AAD_APP_CLIENT_ID",
                     "AAD_APP_TENANT_ID", "SECRET_AAD_APP_CLIENT_SECRET",
                     CertificateCredential.CertPathEnvVar, CertificateCredential.CertPasswordEnvVar,
                 })
            Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void SecretBecomesOptionalWhenACertificateIsConfigured()
    {
        using var cert = HardeningFixtures.NewSelfSignedRsa();
        var path = Path.Combine(TestFixtures.NewTempDir("cert"), "c.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "p"));
        Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar, path);
        Environment.SetEnvironmentVariable("SECRET_AAD_APP_CLIENT_SECRET", null);

        var config = AppConfig.Load();   // must not demand the secret
        Assert.Equal(string.Empty, config.AadClientSecret);
    }

    [Fact]
    public void MissingSecretStillFailsWithoutACertificate()
    {
        Environment.SetEnvironmentVariable("SECRET_AAD_APP_CLIENT_SECRET", null);
        var exc = Assert.Throws<ConfigurationError>(() => AppConfig.Load());
        Assert.Contains("SECRET_AAD_APP_CLIENT_SECRET", exc.Message);
    }

    [Fact]
    public void NonexistentCertPathIsAConfigError()
    {
        Environment.SetEnvironmentVariable("SECRET_AAD_APP_CLIENT_SECRET", "s");
        Environment.SetEnvironmentVariable(CertificateCredential.CertPathEnvVar,
            Path.Combine(TestFixtures.NewTempDir("cert"), "gone.pfx"));
        var exc = Assert.Throws<ConfigurationError>(() => AppConfig.Load());
        Assert.Contains("GRAPH_CLIENT_CERT_PATH", exc.Message);
    }
}

// ---- new operational metrics ------------------------------------------------------

public class HardeningMetricsTests
{
    [Fact]
    public void NewMetricsAreRegisteredInThePrometheusExposition()
    {
        var text = Metrics.RenderPrometheus();
        foreach (var name in new[]
                 {
                     "altrata_graph_throttle_429_total",
                     "altrata_entitlement_refusals_total",
                     "altrata_erasure_ledger_broken",
                     "altrata_match_review_depth",
                     "altrata_ha_leases_held",
                 })
        {
            Assert.Contains($"# TYPE {name}", text);
        }
    }

    [Fact]
    public void EntitlementRefusalsIncrementTheCounter()
    {
        var before = Metrics.Get("altrata_entitlement_refusals_total");
        Assert.Throws<EntitlementViolationException>(
            () => SeatAclBuilder.BuildAcl(Array.Empty<SeatPrincipal>()));
        Assert.Throws<EntitlementViolationException>(
            () => SeatAclBuilder.AssertNeverEveryone(new[]
            {
                new AclEntry { Type = "everyone", Value = "everyone" },
            }));
        Assert.Equal(before + 2, Metrics.Get("altrata_entitlement_refusals_total"));
    }

    [Fact]
    public void LedgerVerifySetsAndClearsTheBrokenGauge()
    {
        var logs = TestFixtures.NewTempDir("ledger_gauge");
        var ledger = new ErasureLedger("GaugeTest", logs);
        ledger.Append("joseph", ErasureActions.Erase, "P1", null, new[] { "PersonProfile-P1" });

        Assert.True(ledger.Verify(out _));
        Assert.Equal(0, Metrics.Get("altrata_erasure_ledger_broken"));

        // Tamper: flip a byte in the recorded line (entries serialize PascalCase).
        var text = File.ReadAllText(ledger.Path).Replace("\"SubjectId\":\"P1\"", "\"SubjectId\":\"P2\"");
        File.WriteAllText(ledger.Path, text);
        var fresh = new ErasureLedger("GaugeTest", logs);
        Assert.False(fresh.Verify(out var brokenAt));
        Assert.True(brokenAt > 0);
        Assert.Equal(1, Metrics.Get("altrata_erasure_ledger_broken"));
    }

    [Fact]
    public void HaLeaseGaugeTracksAcquireAndRelease()
    {
        var before = Metrics.Get("altrata_ha_leases_held");
        var ha = new HaCoordinator(new InMemoryLeaseStore(), "node-test");
        Assert.True(ha.TryAcquire("delivery:d1"));
        Assert.Equal(before + 1, Metrics.Get("altrata_ha_leases_held"));
        ha.Release("delivery:d1");
        Assert.Equal(before, Metrics.Get("altrata_ha_leases_held"));
    }

    [Fact]
    public void ReviewQueueDepthGaugeFollowsTheQueue()
    {
        var logs = TestFixtures.NewTempDir("review_gauge");
        var queue = new MatchReviewQueue("GaugeTest", logs);
        queue.Add(new MatchReviewEntry
        {
            AltrataId = "P1",
            CandidateContactId = "C1",
            Score = 0.7,
            NameHash = MatchReviewEntry.HashValue("ada lovelace"),
            EmployerHash = MatchReviewEntry.HashValue("acme"),
        });
        Assert.Equal(1, Metrics.Get("altrata_match_review_depth"));
    }
}

// ---- MSI authoring source (WiX v5) drift guards -----------------------------------

public class MsiPackagingTests
{
    private static string WxsPath =>
        Path.Combine(HardeningFixtures.RepoRoot(), "packaging", "msi", "Package.wxs");

    [Fact]
    public void WxsIsWellFormedWixV5WithServiceInstall()
    {
        Assert.True(File.Exists(WxsPath), $"missing {WxsPath}");
        var doc = XDocument.Load(WxsPath);   // throws on malformed XML
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";  // v4 schema namespace, used by WiX v5

        Assert.Equal(wix + "Wix", doc.Root!.Name);
        var package = doc.Root.Element(wix + "Package");
        Assert.NotNull(package);
        Assert.NotNull(package!.Attribute("UpgradeCode"));       // stable upgrade identity
        Assert.NotNull(package.Element(wix + "MajorUpgrade"));   // upgrades replace, never side-by-side

        var serviceInstall = doc.Descendants(wix + "ServiceInstall").SingleOrDefault();
        Assert.NotNull(serviceInstall);
        Assert.Equal("AltrataConnector", serviceInstall!.Attribute("Name")?.Value);
        Assert.NotNull(doc.Descendants(wix + "ServiceControl").SingleOrDefault());
    }

    [Fact]
    public void WxsVersionMatchesTheProjectVersion()
    {
        var csproj = XDocument.Load(Path.Combine(HardeningFixtures.RepoRoot(),
            "src", "AltrataConnector", "AltrataConnector.csproj"));
        var projectVersion = csproj.Descendants("Version").Single().Value;

        var wxs = XDocument.Load(WxsPath);
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var msiVersion = wxs.Root!.Element(wix + "Package")!.Attribute("Version")!.Value;
        Assert.Equal(projectVersion, msiVersion);
    }
}
