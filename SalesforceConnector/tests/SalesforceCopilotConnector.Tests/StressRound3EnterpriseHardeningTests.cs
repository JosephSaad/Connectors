// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// StressRound3EnterpriseHardeningTests.cs
// ---------------------------------------
// Round-3 additions that go BEYOND the first (interrupted-run) round-3 suite in
// StressRound3Tests.cs. Two things live here:
//
//   1. REGRESSION for a real concurrency defect found in round-3 —
//      EventLogSink.AttachIfEnabled attached the sink via a non-atomic
//      `_attached ??= new(...)` check-then-assign, so a concurrent attach storm
//      (parallel shard/object workers each running SetupLogging, or the Windows
//      service host attaching alongside a worker command) could add TWO
//      different sink instances to the logger — every WARNING/ERROR then mirrored
//      to the Windows event log twice, and the loser leaked (detach only tracked
//      the last writer). The existing round-3 attach test asserts "attached
//      exactly once even under a concurrent attach storm", but that assertion is
//      inside an `if (OperatingSystem.IsWindows())` branch, so it can NEVER fail
//      on a Linux/macOS CI. This test drives the extracted, platform-neutral
//      EventLogSink.AttachCore seam so the exactly-once contract is verified on
//      every platform. It fails (>1 sink) on the pre-fix `??=` code and passes on
//      the Interlocked.CompareExchange fix.
//
//   2. A FRESH adversarial dimension not covered by the first round-3 suite:
//      certificate TIME-VALIDITY through the additive-trust TLS callback under
//      concurrency. The existing A2 test covers trusted/untrusted/name-mismatch/
//      null; it never checks that an EXPIRED or NOT-YET-VALID leaf is rejected
//      even when its issuing CA is in the trusted bundle. Accepting a time-
//      invalid cert just because its root is trusted would be a real TLS hole.
//
// Everything is offline: certificates are generated in-test; no logger touches an
// OS event log (a no-op IEventLogWriter is used); no network.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SalesforceCopilotConnector.Infrastructure;
using Xunit.Abstractions;

namespace SalesforceCopilotConnector.Tests;

// ═════════════════════════════════════════════════════════════════════════════
// EventLogSink concurrent-attach: exactly one sink (regression for the ??= race)
// ═════════════════════════════════════════════════════════════════════════════

[Collection("EnvVars")]
public sealed class Round3AttachRaceTests
{
    private readonly ITestOutputHelper _out;
    public Round3AttachRaceTests(ITestOutputHelper output) => _out = output;

    /// <summary>No-op OS-writer stand-in — the attach path never writes during this test.</summary>
    private sealed class NoopWriter : IEventLogWriter
    {
        public void Write(EventLogEntrySeverity severity, string message, int eventId) { }
    }

    [Fact]
    public void AttachCore_ConcurrentAttachStorm_AttachesExactlyOneSink()
    {
        const int threads = 64;
        const int waves = 400;

        var maxSeen = 0;
        var bad = new ConcurrentQueue<string>();
        var sw = Stopwatch.StartNew();
        try
        {
            for (var wave = 0; wave < waves; wave++)
            {
                // Fresh isolated target + forgotten singleton each wave, so every
                // wave exercises the create-and-publish path from scratch.
                EventLogSink.ResetAttachedForTests();
                var target = new LoggerObject($"r3-attach-{wave}");
                using var gate = new Barrier(threads);

                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
                {
                    gate.SignalAndWait();
                    // A small spin widens the check→publish window so a non-atomic
                    // check-then-assign reliably lets multiple builders through.
                    EventLogSink.AttachCore(target, () =>
                    {
                        Thread.SpinWait(400);
                        return NewSink();
                    });
                });

                var count = target.Handlers.OfType<EventLogSink>().Count();
                maxSeen = Math.Max(maxSeen, count);
                if (count != 1)
                    bad.Enqueue($"wave {wave}: {count} sinks attached");
            }
        }
        finally
        {
            EventLogSink.ResetAttachedForTests();
        }
        sw.Stop();

        Assert.True(bad.IsEmpty,
            $"concurrent attach produced duplicate sinks (max {maxSeen}): " + string.Join(" | ", bad.Take(5)));
        Assert.Equal(1, maxSeen);

        var msg = $"{waves} waves × {threads} concurrent attaches in {sw.ElapsedMilliseconds} ms; " +
                  $"max sinks attached per wave = {maxSeen} (exactly one every wave), 0 duplicates, 0 leaks";
        _out.WriteLine("[R3 attach-race] " + msg);
        Round3Support.Report("R3_attach_exactly_once", msg);
    }

    [Fact]
    public void AttachCore_AfterEstablished_DoesNotBuildOrAddASecondSink()
    {
        // Once a sink is established, later AttachCore calls must reuse it (no
        // second build, no second handler) even from many threads at once.
        EventLogSink.ResetAttachedForTests();
        try
        {
            var target = new LoggerObject("r3-attach-established");
            var builds = 0;
            Func<EventLogSink> factory = () => { Interlocked.Increment(ref builds); return NewSink(); };

            EventLogSink.AttachCore(target, factory);          // establish
            var buildsAfterFirst = Volatile.Read(ref builds);

            Parallel.For(0, 64, _ => EventLogSink.AttachCore(target, factory));

            Assert.Single(target.Handlers.OfType<EventLogSink>());
            Assert.Equal(buildsAfterFirst, Volatile.Read(ref builds));  // steady state: factory not re-invoked
            _out.WriteLine($"[R3 attach-steady] 64 post-establish attaches: still 1 sink, factory built {builds}× total");
        }
        finally
        {
            EventLogSink.ResetAttachedForTests();
        }
    }

    private static EventLogSink NewSink() => new EventLogSink(new NoopWriter(), mirrorInfo: false);
}

// ═════════════════════════════════════════════════════════════════════════════
// Fresh dimension — additive-trust TLS callback must reject TIME-INVALID leaves
// even when the issuing CA is trusted, and stay correct under concurrency.
// ═════════════════════════════════════════════════════════════════════════════

public sealed class Round3TlsTimeValidityStressTests
{
    private readonly ITestOutputHelper _out;
    public Round3TlsTimeValidityStressTests(ITestOutputHelper output) => _out = output;

    /// <summary>Wide-window CA so leaves with past/future validity still fall inside the issuer range.</summary>
    private static X509Certificate2 CreateWideCa(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-365), DateTimeOffset.UtcNow.AddDays(365));
    }

    private static X509Certificate2 IssueLeaf(
        X509Certificate2 ca, string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        return request.Create(ca, notBefore, notAfter, serial);
    }

    [Fact]
    public void TimeInvalidLeaf_RejectedEvenWhenRootTrusted_32Threads()
    {
        var now = DateTimeOffset.UtcNow;
        using var ca = CreateWideCa("CN=R3 Time CA");
        using var valid = IssueLeaf(ca, "CN=valid.local", now.AddDays(-1), now.AddDays(7));
        using var expired = IssueLeaf(ca, "CN=expired.local", now.AddDays(-30), now.AddDays(-1));
        using var notYetValid = IssueLeaf(ca, "CN=future.local", now.AddDays(1), now.AddDays(30));
        var extraRoots = new X509Certificate2Collection { ca };

        // Sanity: the ONLY difference between these leaves is time validity — the
        // valid one must be accepted, or the negative cases prove nothing.
        Assert.True(HttpClientFactory.ValidateWithAdditionalRoots(
            valid, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots),
            "baseline valid leaf (trusted CA) should be accepted");

        const int threads = 32;
        const int perThread = 300;
        var wrong = new ConcurrentQueue<string>();
        var threw = new ConcurrentQueue<string>();
        var count = 0;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    if (!HttpClientFactory.ValidateWithAdditionalRoots(
                            valid, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                        wrong.Enqueue("valid leaf rejected");
                    if (HttpClientFactory.ValidateWithAdditionalRoots(
                            expired, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                        wrong.Enqueue("EXPIRED leaf accepted (trusted root must not override time validity)");
                    if (HttpClientFactory.ValidateWithAdditionalRoots(
                            notYetValid, null, SslPolicyErrors.RemoteCertificateChainErrors, extraRoots))
                        wrong.Enqueue("NOT-YET-VALID leaf accepted");
                    Interlocked.Add(ref count, 3);
                }
                catch (Exception exc)
                {
                    threw.Enqueue($"{exc.GetType().Name}: {exc.Message}");
                }
            }
        });
        sw.Stop();

        Assert.True(threw.IsEmpty, "callback threw: " + string.Join(" | ", threw.Take(5)));
        Assert.True(wrong.IsEmpty, "wrong decision(s): " + string.Join(" | ", wrong.Take(5)));
        Assert.Equal(threads * perThread * 3, count);

        var msg = $"{count} time-validity validations / {threads} threads in {sw.ElapsedMilliseconds} ms; " +
                  "expired + not-yet-valid leaves rejected every time despite a trusted CA, valid leaf accepted; 0 throws";
        _out.WriteLine("[R3 tls-time-validity] " + msg);
        Round3Support.Report("R3_tls_time_validity", msg);
    }
}
