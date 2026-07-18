// HardeningReviewTests.cs
// -----------------------
// try/catch review + stress round over the NEW bank-grade hardening code
// (2026-07-18). Covers the four Altrata sharp edges called out for this pass:
//   1. purpose-of-use authz — fail-closed BEFORE any billable/token/HTTP call,
//      under CONCURRENT lookups; deny audit PII-safe.
//   2. the HashChainedLedger<T> refactor — the erasure/DSAR ledger hash is still
//      byte-for-byte the documented canonical form (characterisation guard so a
//      future refactor cannot silently change the compliance record's format).
//   3. dead-letter redaction stays Altrata's default (already covered by
//      DeadLetterProtectionTests; here we lock the CONFIG-load contract for it).
//   4. directory hardening never throws on real failure modes + decision-ledger
//      concurrent-append integrity / tamper / tolerant torn-line reader.
//
// The DEADLETTER_PAYLOAD_MODE config-load test is the red→green regression for a
// genuine defect: a garbage value passed validate-config / AppConfig.Load()
// (reported VALID) and only threw mid-crawl.

using System.Security.Cryptography;
using System.Text;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ============================================================================
// #1 — purpose-of-use authorization under concurrency (fail-closed, PII-safe)
// ============================================================================

public class PurposeConcurrencyTests
{
    private static (AltrataApiClient Client, FileStateStore State, AuditLog Audit, ScriptedHandler Handler)
        Setup(IPurposePolicy purpose)
    {
        var root = TestFixtures.NewTempDir("purpose_conc");
        var config = new AppConfig
        {
            ConnectorId = "AltrataTest", ConnectorName = "t", ConnectorDescription = "t",
            AadClientId = "c", AadTenantId = "t", AadClientSecret = "s",
            AltrataApiBaseUrl = "https://api.altrata.test/v1",
            AltrataTokenUrl = "https://auth.altrata.test/oauth/token",
            AltrataClientId = "api-client", AltrataClientSecret = "api-secret",
        };
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var audit = new AuditLog("AltrataTest", logsDir: Path.Combine(root, "logs"));
        var handler = new ScriptedHandler();   // NOTHING enqueued: any HTTP call throws
        var client = new AltrataApiClient(config, state, audit, handler,
            (_, _) => Task.CompletedTask, purpose: purpose);
        return (client, state, audit, handler);
    }

    [Fact]
    public async Task ConcurrentDisallowedLookupsAreAllVetoedBeforeAnyHttpOrBilling()
    {
        // One shared client, 64 concurrent lookups with a disallowed purpose.
        // The handler has NO scripted responses, so a single leaked token/HTTP
        // call would surface as an InvalidOperationException ("Unexpected
        // request") rather than PurposeDeniedException — proving the veto is
        // strictly before GetTokenAsync / the rate limiter / the billable PUT.
        var (client, state, audit, handler) = Setup(new PurposePolicy(new[] { "RFP", "KYC" }));

        const int n = 64;
        var tasks = Enumerable.Range(0, n).Select(i =>
            Assert.ThrowsAsync<PurposeDeniedException>(
                () => client.LookupPersonAsync($"P{i}", $"actor{i}", "scrape everyone")));
        await Task.WhenAll(tasks);

        Assert.Empty(handler.Requests);                       // no token, no lookup, ever
        Assert.Equal(0, state.GetBillableLookupCount());      // nothing billed
        Metrics.Get("altrata_purpose_denied_total");          // (touch — value is process-global)

        // Every denial was audited durably, and each is PII-safe: id / actor /
        // purpose / decision only, never a profile value (none was fetched).
        var entries = audit.ReadAll();
        Assert.Equal(n, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal("deny", e.Decision);
            Assert.False(e.Billable);
            Assert.Equal("api_lookup", e.Action);
        });
    }

    [Fact]
    public async Task EmptyPurposeIsDeniedFailClosedUnderConcurrency()
    {
        var (client, state, audit, handler) = Setup(new PurposePolicy(new[] { "RFP" }));

        var tasks = Enumerable.Range(0, 32).Select(_ =>
            Assert.ThrowsAsync<PurposeDeniedException>(
                () => client.LookupPersonAsync("P1", "actor", "   ")));   // whitespace purpose
        await Task.WhenAll(tasks);

        Assert.Empty(handler.Requests);
        Assert.Equal(0, state.GetBillableLookupCount());
        Assert.All(audit.ReadAll(), e => Assert.Equal("deny", e.Decision));
    }
}

// ============================================================================
// #2 — HashChainedLedger refactor: hash is byte-for-byte the documented form
// ============================================================================

public class LedgerCanonicalFormTests
{
    private static string Sha256HexLower(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    [Fact]
    public void ErasureLedgerHashIsTheDocumentedCanonicalSha256()
    {
        // Independently re-derive the canonical string (prevHash, seq, O-format
        // timestamp, actor, action, subjectId, email, correlationId, joined
        // items) and confirm ComputeHash matches it byte-for-byte. Locks the
        // erasure/DSAR compliance record's on-disk hash format across refactors.
        var ts = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
        var items = new[] { "PersonProfile-P1", "WealthIndicator-W1" };
        var expected = Sha256HexLower(string.Concat(
            HashChainedLedger<ErasureLedgerEntry>.GenesisHash,
            "1",
            ts.ToString("O"),
            "joseph",
            ErasureActions.Erase,
            "P1",
            "ada@x.com",
            "corr-1",
            string.Join(",", items)));

        var actual = ErasureLedger.ComputeHash(1, ts, "joseph", ErasureActions.Erase,
            "P1", "ada@x.com", "corr-1", items,
            HashChainedLedger<ErasureLedgerEntry>.GenesisHash);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AppendedErasureEntryHashMatchesIndependentRecompute()
    {
        var dir = Path.Combine(TestFixtures.NewTempDir("erasure_canon"), "logs");
        var ledger = new ErasureLedger("AltrataTest", dir);
        var items = new[] { "PersonProfile-P1" };
        var entry = ledger.Append("joseph", ErasureActions.Erase, "P1", "ada@x.com", items);

        var expected = Sha256HexLower(string.Concat(
            HashChainedLedger<ErasureLedgerEntry>.GenesisHash,
            entry.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.TimestampUtc.ToString("O"),
            "joseph",
            ErasureActions.Erase,
            "P1",
            "ada@x.com",
            entry.CorrelationId ?? "",
            string.Join(",", items)));

        Assert.Equal(HashChainedLedger<ErasureLedgerEntry>.GenesisHash, entry.PrevHash);
        Assert.Equal(expected, entry.Hash);
        Assert.True(ledger.Verify(out _));
    }

    [Fact]
    public void DecisionLedgerHashIsTheDocumentedCanonicalSha256()
    {
        var ts = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
        var expected = Sha256HexLower(string.Concat(
            HashChainedLedger<DecisionLedgerEntry>.GenesisHash,
            "1",
            ts.ToString("O"),
            "d1",
            DecisionActions.Exclude,
            "checksum mismatch",
            "",          // actor null → ""
            ""));        // correlationId null → ""

        var actual = DecisionLedger.ComputeHash(1, ts, "d1", DecisionActions.Exclude,
            "checksum mismatch", actor: null, correlationId: null,
            prevHash: HashChainedLedger<DecisionLedgerEntry>.GenesisHash);

        Assert.Equal(expected, actual);
    }
}

// ============================================================================
// #4 — decision-ledger concurrent-append integrity + tamper + tolerant reader
// ============================================================================

public class DecisionLedgerStressTests
{
    private static DecisionLedger NewLedger(out string path)
    {
        var dir = Path.Combine(TestFixtures.NewTempDir("decision_stress"), "logs");
        var ledger = new DecisionLedger("AltrataTest", dir);
        path = ledger.Path;
        return ledger;
    }

    [Fact]
    public void ConcurrentAppendsProduceAValidContiguousUntornChain()
    {
        var ledger = NewLedger(out var path);
        const int n = 400;

        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            i => ledger.RecordExclusion($"d{i}", $"reason {i}"));

        // Every append landed; the chain verifies; sequence numbers are exactly
        // 1..n with no gaps or duplicates (no interleaved/torn writes).
        var entries = ledger.ReadAll();
        Assert.Equal(n, entries.Count);
        Assert.True(ledger.Verify(out var broken), $"chain broken at {broken}");
        Assert.Equal(0, broken);
        Assert.Equal(Enumerable.Range(1, n), entries.Select(e => (int)e.Seq).OrderBy(s => s));

        // The physical file has exactly n non-blank lines and every one parses
        // (a torn/interleaved concurrent write would leave a half line).
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(n, lines.Length);
    }

    [Fact]
    public void ReorderingTwoEntriesBreaksTheChain()
    {
        var ledger = NewLedger(out var path);
        ledger.RecordExclusion("d1", "r1");
        ledger.RecordExclusion("d2", "r2");
        ledger.RecordExclusion("d3", "r3");

        var lines = File.ReadAllLines(path);
        (lines[0], lines[2]) = (lines[2], lines[0]);   // swap first and last
        File.WriteAllLines(path, lines);

        // A fresh instance (cold cache) must still detect the reorder without
        // throwing. The entry now sitting at position 1 is the one with Seq=3, so
        // Verify reports the break at that out-of-place entry's own seq.
        var reopened = new DecisionLedger("AltrataTest", Path.GetDirectoryName(path));
        Assert.False(reopened.Verify(out var broken));
        Assert.Equal(3, broken);
    }

    [Fact]
    public void TolerantReaderSurvivesATornFinalLine()
    {
        var ledger = NewLedger(out var path);
        ledger.RecordExclusion("d1", "r1");
        ledger.RecordExclusion("d2", "r2");

        // Simulate a crash mid-append: a partial (unparseable) trailing line.
        File.AppendAllText(path, "{\"Seq\":3,\"ItemId\":\"d3\",\"Decisi");

        // Verify pinpoints the torn line and NEVER throws on the tampered bytes.
        Assert.False(ledger.Verify(out var broken));
        Assert.Equal(3, broken);

        // The strict reader refuses (it is the compliance read path)...
        Assert.Throws<InvalidDataException>(() => ledger.ReadAll());

        // ...and a corrupt chain is never appended to (fail-closed / fail-durable).
        Assert.Throws<InvalidDataException>(() => ledger.RecordExclusion("d4", "r4"));
    }

    [Fact]
    public void ChainContinuesAcrossInstanceRestarts()
    {
        var ledger = NewLedger(out var path);
        var dir = Path.GetDirectoryName(path);
        ledger.RecordExclusion("d1", "r1");

        // A second process (new instance, cold cache) appends: seq advances and
        // the new entry chains off the first's stored hash.
        var restarted = new DecisionLedger("AltrataTest", dir);
        var e2 = restarted.RecordAclRestriction("PersonProfile-P1", "Restricted -> group G");
        Assert.Equal(2, e2.Seq);

        var first = restarted.ReadAll()[0];
        Assert.Equal(first.Hash, e2.PrevHash);
        Assert.True(restarted.Verify(out _));

        // A THIRD instance still verifies the whole persisted chain.
        Assert.True(new DecisionLedger("AltrataTest", dir).Verify(out _));
    }
}

// ============================================================================
// #4 — directory hardening: "never throws" holds for real failure modes
// ============================================================================

public class DirectoryHardeningStressTests
{
    [Fact]
    public void EnsureSecureDirectoryNeverThrowsWhenAFileOccupiesThePath()
    {
        // Directory.CreateDirectory throws IOException when a FILE already exists
        // at the target path — the classic "never throws" failure mode. The
        // helper must swallow-with-warning and leave the file intact.
        var root = TestFixtures.NewTempDir("harden_file");
        var clash = Path.Combine(root, "state");
        File.WriteAllText(clash, "i am a file, not a directory");

        DirectoryHardening.EnsureSecureDirectory(clash);   // must not throw

        Assert.True(File.Exists(clash));
        Assert.False(Directory.Exists(clash));
    }

    [Fact]
    public void ConcurrentEnsureSecureDirectoryOnTheSamePathIsSafe()
    {
        var root = TestFixtures.NewTempDir("harden_conc");
        var target = Path.Combine(root, "logs");

        // Many startups racing to harden the same directory: no throw, idempotent.
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            _ => DirectoryHardening.EnsureSecureDirectory(target));

        Assert.True(Directory.Exists(target));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(DirectoryHardening.OwnerOnly, File.GetUnixFileMode(target));
    }
}

// ============================================================================
// #3 — DEADLETTER_PAYLOAD_MODE is validated at CONFIG LOAD (red→green defect)
// ============================================================================

public class DeadLetterModeConfigLoadTests : IDisposable
{
    public DeadLetterModeConfigLoadTests()
    {
        foreach (var (k, v) in new[]
                 {
                     ("CONNECTOR_ID", "AltrataDlmTest"), ("CONNECTOR_NAME", "t"),
                     ("CONNECTOR_DESCRIPTION", "t"), ("AAD_APP_CLIENT_ID", "c"),
                     ("AAD_APP_TENANT_ID", "t"), ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
                 })
            Environment.SetEnvironmentVariable(k, v);
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, null);
    }

    public void Dispose()
    {
        foreach (var k in new[]
                 {
                     "CONNECTOR_ID", "CONNECTOR_NAME", "CONNECTOR_DESCRIPTION",
                     "AAD_APP_CLIENT_ID", "AAD_APP_TENANT_ID", "SECRET_AAD_APP_CLIENT_SECRET",
                     DeadLetterPolicy.EnvVar,
                 })
            Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void GarbageDeadLetterModeFailsConfigLoadNamingTheSetting()
    {
        // Regression: previously this passed AppConfig.Load()/validate-config
        // (reported VALID) and only threw mid-crawl at DeadLetterPolicy.Mode.
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, "redacetd");   // typo
        var exc = Assert.Throws<ConfigurationError>(() => AppConfig.Load());
        Assert.Contains("DEADLETTER_PAYLOAD_MODE", exc.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("redacted")]
    [InlineData("FULL")]        // case-insensitive
    [InlineData(" full ")]      // trimmed
    public void ValidDeadLetterModesLoadCleanly(string? mode)
    {
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, mode);
        var config = AppConfig.Load();
        Assert.Equal("AltrataDlmTest", config.ConnectorId);   // load succeeded
    }
}
