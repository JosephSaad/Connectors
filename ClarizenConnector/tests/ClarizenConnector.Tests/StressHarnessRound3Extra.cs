// StressHarnessRound3Extra.cs
// ---------------------------
// Round-3 ADDITIONS that fill gaps the primary round-3 harness
// (StressHarnessRound3.cs) and rounds 1/2 left open. Nothing here duplicates an
// existing scenario:
//   • A2 exercised the CA-bundle callback with a valid leaf, an unrelated
//     stranger, and a name-mismatch — but NEVER a certificate that chains to the
//     trusted custom root yet is outside its validity window. An expired (or
//     not-yet-valid) leaf presented by a TLS-inspecting proxy is a real attack
//     surface: the fix must reject it even though it is CA-signed. (PART C —
//     fresh adversarial dimension: certificate time-validity at the trust
//     boundary, under concurrency.)
//   • A4 drove the dead-letter redactor with flat string/number property values
//     only. Real payloads carry array-valued financial fields (cost centres,
//     tag lists) and, on non-externalItem shapes, nested objects/arrays. Two
//     tests here harden the redactor against those structural variations — one
//     against the public RedactRequestBody directly, one end-to-end through the
//     SyncState.AppendFailedRecords choke point under concurrent producers.
//
// All offline / in-process; no network. Every test asserts a hard invariant AND
// emits REAL measured numbers via StressLog.

using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using Xunit.Abstractions;

namespace ClarizenConnector.Tests;

// ═════════════════════════════════════════════════════════════════════════════
// PART C (fresh) — CA-bundle trust path: certificate time-validity boundary
// ═════════════════════════════════════════════════════════════════════════════

public class CaBundleTimeValidityStressTests
{
    private readonly ITestOutputHelper _out;
    public CaBundleTimeValidityStressTests(ITestOutputHelper output) => _out = output;

    /// <summary>Leaf signed by <paramref name="ca"/> with an explicit validity window
    /// (public part only) — lets us mint expired / not-yet-valid certificates.</summary>
    private static X509Certificate2 IssuedBy(
        X509Certificate2 ca, DateTimeOffset notBefore, DateTimeOffset notAfter,
        string subject = "CN=inspected.example.com")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        return request.Create(ca, notBefore, notAfter, serial);
    }

    // A TLS-inspecting proxy could present a leaf that DOES chain to the bundled
    // corporate CA but is expired or not-yet-valid. Custom-root trust must NOT
    // paper over a time-validity failure: the additive path only forgives a
    // chain-TRUST gap, and X509Chain.Build still enforces NotBefore/NotAfter.
    // Drive the real validation function from 32 threads over the full mix and
    // assert every verdict is correct with zero exceptions (fresh X509Chain per
    // call — no shared-chain race even at the time boundary).
    [Fact]
    public void CaBundle_TimeInvalidLeaf_RejectedUnderConcurrency_ValidStillAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        using var ca = TestCertificates.CertificateAuthority();  // valid now-1d .. now+30d
        using var validLeaf = IssuedBy(ca, now.AddHours(-1), now.AddDays(7));
        // Windows fit inside the CA's own validity span but are outside "now":
        using var expiredLeaf = IssuedBy(ca, now.AddHours(-20), now.AddHours(-1), "CN=expired.example.com");
        using var futureLeaf = IssuedBy(ca, now.AddDays(1), now.AddDays(10), "CN=notyet.example.com");
        using var stranger = TestCertificates.SelfSigned("CN=unrelated.example.com");

        // Public-part-only roots collection, exactly as LoadCaBundle produces.
        var roots = new X509Certificate2Collection { X509CertificateLoader.LoadCertificate(ca.RawData) };

        // Sanity: single-threaded, the valid leaf is accepted and each time-invalid
        // / unrelated leaf is rejected. (Pins the expected verdicts before load.)
        Assert.True(Validate(validLeaf, roots));
        Assert.False(Validate(expiredLeaf, roots));
        Assert.False(Validate(futureLeaf, roots));
        Assert.False(Validate(stranger, roots));

        const int threads = 32, perThread = 1500;
        long wrong = 0, exceptions = 0, accepted = 0, rejected = 0;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    bool result, expected;
                    switch ((t + i) % 4)
                    {
                        case 0: result = Validate(validLeaf, roots); expected = true; break;   // in-window, CA-signed
                        case 1: result = Validate(expiredLeaf, roots); expected = false; break; // expired
                        case 2: result = Validate(futureLeaf, roots); expected = false; break;  // not yet valid
                        default: result = Validate(stranger, roots); expected = false; break;   // not CA-signed
                    }
                    if (result != expected) Interlocked.Increment(ref wrong);
                    if (result) Interlocked.Increment(ref accepted);
                    else Interlocked.Increment(ref rejected);
                }
                catch
                {
                    Interlocked.Increment(ref exceptions);
                }
            }
        });
        sw.Stop();

        Assert.Equal(0, Interlocked.Read(ref wrong));         // no verdict flipped at the time boundary
        Assert.Equal(0, Interlocked.Read(ref exceptions));    // no shared-X509Chain crash
        var total = (long)threads * perThread;
        Assert.Equal(total, Interlocked.Read(ref accepted) + Interlocked.Read(ref rejected));
        var rate = total / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        StressLog.Line(_out,
            $"[transport] CA-bundle time-validity boundary: {threads} threads × {perThread} = {total:N0} "
            + $"concurrent validations in {sw.ElapsedMilliseconds} ms ({rate:N0}/s); valid accepted, "
            + $"expired+not-yet-valid+stranger rejected — accepted={Interlocked.Read(ref accepted):N0}, "
            + $"rejected={Interlocked.Read(ref rejected):N0}, wrong verdicts=0, exceptions=0 "
            + "(time validity survives custom-root trust) — PASS");
    }

    private static bool Validate(X509Certificate leaf, X509Certificate2Collection roots) =>
        HttpClientFactory.ValidateWithAdditionalRoots(
            leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, roots);
}

// ═════════════════════════════════════════════════════════════════════════════
// PART A hardening — dead-letter redactor against STRUCTURALLY adversarial input
// ═════════════════════════════════════════════════════════════════════════════

public class RedactorStructuralAdversaryStressTests : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";
    private readonly ITestOutputHelper _out;
    public RedactorStructuralAdversaryStressTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => Metrics.ResetForTests();

    /// <summary>An externalItem-shaped payload whose property VALUES span every
    /// shape a real record can carry — string, number, nested object, array, and
    /// null — each seeded with the run's sentinel so any survivor is detectable.</summary>
    private static JsonObject AdversarialPayload(int n)
    {
        var s = $"FIN-SECRET-{n}";
        return new JsonObject
        {
            ["id"] = $"Project_{n}",
            ["properties"] = new JsonObject
            {
                ["BillingRate"] = $"{s} per hour",                                  // string
                ["PlannedCost"] = 120000.5 + n,                                     // number
                ["Nested"] = new JsonObject { ["iban"] = $"{s}-IBAN", ["swift"] = s },  // nested object
                ["CostCenters"] = new JsonArray($"{s}-cc1", $"{s}-cc2"),            // array
                ["Absent"] = null,                                                  // null value
            },
            ["content"] = new JsonObject
            {
                ["value"] = $"Rate card {s}; contact jane.doe{n}@example.com about billing",
                ["type"] = "text",
            },
            ["acl"] = new JsonArray(
                AclJson("user", $"u-{n}", "grant"),
                AclJson("user", $"d-{n}", "deny"),
                AclJson("group", $"g-{n}", "grant")),
        };
    }

    private static JsonObject AclJson(string type, string value, string access) =>
        new() { ["type"] = type, ["value"] = value, ["accessType"] = access };

    [Fact]
    public void RedactRequestBody_StructurallyAdversarial_Concurrent_NoValueSurvives()
    {
        using var env = new EnvScope((DeadLetterRedactor.ModeEnvVar, "redacted"));
        const int threads = 16, perThread = 4000;
        long violations = 0, leaks = 0, ok = 0;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                var n = (t * perThread) + i;
                var sentinel = $"FIN-SECRET-{n}";
                var redacted = DeadLetterRedactor.RedactRequestBody(AdversarialPayload(n));

                // 1) The whole redacted node — serialized — must not contain the
                //    sentinel or the PII local-part anywhere (nested/array included).
                var text = redacted!.ToJsonString();
                if (text.Contains(sentinel, StringComparison.Ordinal)
                    || text.Contains("jane.doe", StringComparison.Ordinal))
                    Interlocked.Increment(ref leaks);

                // 2) Structure retained for triage: id + hashed values (names kept)
                //    for EVERY property shape, content hash+length, acl_count.
                var obj = redacted as JsonObject;
                var props = obj?["properties"] as JsonObject;
                var good = obj is not null
                    && obj["redacted"]?.GetValue<bool>() == true
                    && obj["id"]!.GetValue<string>() == $"Project_{n}"
                    && props is not null
                    && props["BillingRate"]!.GetValue<string>().StartsWith("sha256:", StringComparison.Ordinal)
                    && props["PlannedCost"]!.GetValue<string>().StartsWith("sha256:", StringComparison.Ordinal)
                    && props["Nested"]!.GetValue<string>().StartsWith("sha256:", StringComparison.Ordinal)
                    && props["CostCenters"]!.GetValue<string>().StartsWith("sha256:", StringComparison.Ordinal)
                    && props.ContainsKey("Absent") && props["Absent"] is null   // null NAME kept, value null
                    && obj["content_sha256"] is not null
                    && obj["content_length"]!.GetValue<int>() > 0
                    && obj["acl_count"]!.GetValue<int>() == 3;
                if (good) Interlocked.Increment(ref ok); else Interlocked.Increment(ref violations);
            }
        });
        sw.Stop();

        // Non-object payloads collapse to a marker — nothing passes through verbatim.
        Assert.Equal("[redacted]",
            DeadLetterRedactor.RedactRequestBody(JsonValue.Create("FIN-SECRET-raw"))!.GetValue<string>());
        Assert.Equal("[redacted]",
            DeadLetterRedactor.RedactRequestBody(new JsonArray("FIN-SECRET-arr"))!.GetValue<string>());

        Assert.Equal(0, Interlocked.Read(ref leaks));
        Assert.Equal(0, Interlocked.Read(ref violations));
        var total = (long)threads * perThread;
        Assert.Equal(total, Interlocked.Read(ref ok));
        var rate = total / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        StressLog.Line(_out,
            $"[deadletter] structural adversary: {threads} threads × {perThread} = {total:N0} redactions of "
            + $"mixed-shape payloads (string/number/nested-object/array/null) in {sw.ElapsedMilliseconds} ms "
            + $"({rate:N0}/s); 0 FIN-SECRET/PII survivors (incl. nested + array), every shape hashed with names "
            + "kept, acl_count=3, non-object payloads collapse to [redacted] — NO LEAK");
    }

    // End-to-end through the real choke point with ARRAY-valued financial props
    // (string[]) — A4 used only flat string/number. Concurrent producers, redacted
    // mode: no torn lines, no array element survives, retry re-fetch intact.
    [Fact]
    public void RedactedChokePoint_ArrayValuedFinancialProps_Concurrent_NoLeakNoTorn()
    {
        using var state = new SyncStateScope();
        using var env = new EnvScope((DeadLetterRedactor.ModeEnvVar, "redacted"));
        // Array-valued financial property, modelled as an operator schema
        // extension so the property name is declared like any other.
        using var schema = new GraphSchemaScope("CostCenters");

        const int producers = 16, perProducer = 500;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, producers, new ParallelOptions { MaxDegreeOfParallelism = producers }, p =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                var n = (p * perProducer) + i;
                var item = new ExternalItem { Id = $"Project_{n}" };
                item.Properties["Title"] = $"Project {n}";
                item.Properties["CostCenters"] = new[] { $"FIN-SECRET-{n}-cc1", $"FIN-SECRET-{n}-cc2" }; // string[]
                item.Properties["PlannedCost"] = 120000.5 + n;
                item.Content = $"Rate card FIN-SECRET-{n}";
                item.Acl.Add(new AclEntry(AclEntryType.User, $"user-{n}", AclAccessType.Grant));
                item.Acl.Add(new AclEntry(AclEntryType.User, $"deny-{n}", AclAccessType.Deny));
                SyncState.AppendFailedRecords(
                    Connector,
                    new List<(string, string)> { ($"Project_{n}", $"HTTP 400 for Project_{n}") },
                    "Project",
                    new Dictionary<string, JsonNode?> { [$"Project_{n}"] = item.ToJson() });
            }
        });
        sw.Stop();

        var expected = producers * perProducer;
        var rawPath = SyncState.FailedRecordsPath(Connector);
        var rawText = File.ReadAllText(rawPath);
        var rawLines = File.ReadAllLines(rawPath).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(expected, rawLines.Count);
        Assert.All(rawLines, l => JsonNode.Parse(l));      // no torn/interleaved write
        Assert.DoesNotContain("FIN-SECRET-", rawText, StringComparison.Ordinal);  // no array element survived

        var records = SyncState.ReadFailedRecords(Connector);
        Assert.Equal(expected, records.Count);
        long retryable = 0, arrayHashed = 0;
        foreach (var r in records)
        {
            var props = (r["request_body"] as JsonObject)?["properties"] as JsonObject;
            if (props?["CostCenters"]?.GetValue<string>().StartsWith("sha256:", StringComparison.Ordinal) == true
                && props.ContainsKey("PlannedCost"))
                Interlocked.Increment(ref arrayHashed);
            if (!r.ContainsKey("response_body")
                && r["item_id"] is not null
                && r["object_type"]!.GetValue<string>() == "Project")
                Interlocked.Increment(ref retryable);
        }
        Assert.Equal(expected, Interlocked.Read(ref arrayHashed));   // array collapsed to one hash, names kept
        Assert.Equal(expected, Interlocked.Read(ref retryable));     // still re-fetchable

        SyncState.ClearFailedRecords(Connector);
        Assert.Empty(SyncState.ReadFailedRecords(Connector));

        var rate = expected / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        StressLog.Line(_out,
            $"[deadletter] array-valued financial props end-to-end: {producers} producers × {perProducer} = "
            + $"{expected:N0} concurrent redacted appends in {sw.ElapsedMilliseconds} ms ({rate:N0}/s); "
            + $"{rawLines.Count:N0} well-formed lines (0 torn), 0 CostCenter element survives, array hashed to one "
            + $"sha256 with names kept, {retryable:N0}/{expected:N0} re-fetchable — NO LEAK");
    }
}
