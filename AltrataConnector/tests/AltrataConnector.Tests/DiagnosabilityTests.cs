// Troubleshooting-diagnosability hardening tests.
//
// An operator must be able to answer "what failed, on which entity/subject/
// item/shard, why, and what happens next" from connector.log alone — WITHOUT
// the log leaking raw subject PII (names/employers/emails appear only as ids
// or hashes). These tests drive real failure paths, capture the run log file,
// and assert both halves: the diagnostic IS there, the PII is NOT.

using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

/// <summary>Opens a run log under the CURRENT LOGS_DIR and reads it back.
/// Defensively closes any run a previous test left open.</summary>
internal sealed class RunLogCapture : IDisposable
{
    private readonly string _logsRoot;

    public RunLogCapture(string prefix)
    {
        Logging.ResetForTests();   // defensive: never inherit another test's file sink
        RunLog.StartRun(prefix);
        _logsRoot = Logging.LogsRoot;
    }

    /// <summary>Close the run and return the whole connector.log content.</summary>
    public string ReadAll()
    {
        Logging.ResetForTests();
        var file = Directory.EnumerateFiles(_logsRoot, "connector.log", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        return File.ReadAllText(file);
    }

    public void Dispose() => Logging.ResetForTests();
}

public class TransformFailureDiagnosabilityTests
{
    [Fact]
    public async Task TransformDeadLetterIsLoggedWithIdsAndWithoutRawPii()
    {
        using var harness = new CrawlHarness();
        // First record carries a name/email/employer but NO id → deterministic
        // transform failure for a fully "named" subject. Second record is fine.
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, """
                [{"person_name":"Ada Lovelace","email":"ada@contoso.com","employer":"Analytical Engines"},
                 {"id":"P2","person_name":"Grace Hopper","email":"grace@contoso.com","employer":"Navy Research"}]
                """, 2));

        using var capture = new RunLogCapture("diag_transform");
        var result = await harness.Engine.RunAsync(CrawlKind.Full);
        var log = capture.ReadAll();

        Assert.Equal(1, result.ItemsDeadLettered);
        Assert.Equal(1, result.ItemsIngested);

        // The operator's questions are answerable: what failed (transform),
        // which record (row0 of PersonProfile), which delivery, why (no id),
        // and what happens next (dead-letter — visible via the record itself).
        Assert.Contains("Transform dead-lettered PersonProfile record 'row0'", log);
        Assert.Contains("delivery 'd1'", log);
        Assert.Contains("TransformException", log);
        Assert.Contains("has no id field", log);

        // ...and NONE of the subject's raw PII reached the log.
        foreach (var pii in new[]
                 {
                     "Ada Lovelace", "ada@contoso.com", "Analytical Engines",
                     "Grace Hopper", "grace@contoso.com", "Navy Research",
                 })
            Assert.DoesNotContain(pii, log);
    }
}

public class ErasureRaceDiagnosabilityTests
{
    [Fact]
    public async Task CompensatingWithdrawalIsLoggedWithItemIdAndWithoutRawPii()
    {
        using var rig = new R2Rig("diagrace", "R2DiagRace", "alice@contoso.com");
        const string victim = "VDIAG";
        var victimItem = ItemTransformer.BuildItemId(Datasets.PersonProfile, victim);
        TestFixtures.WriteDelivery(rig.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(
                (victim, "Victim Person", null, null),
                ("F1", "Filler One", null, null)), 2));

        // Park the PUT batch carrying the victim mid-flight, run the WHOLE DSAR
        // to completion, then release — forcing the compensating-withdrawal path.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new SemaphoreSlim(0);
        var gated = 0;
        rig.Graph.BeforePutBatch = async items =>
        {
            if (items.Any(i => i.Id == victimItem) && Interlocked.Exchange(ref gated, 1) == 0)
            {
                reached.TrySetResult();
                Assert.True(await release.WaitAsync(TimeSpan.FromSeconds(30)), "PUT gate never released");
            }
        };

        using var capture = new RunLogCapture("diag_race");
        var crawl = Task.Run(() => rig.Engine.RunAsync(CrawlKind.Full));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var forgotten = await CommandRegistry.ForgetSubjectAsync(
            rig.Runtime, altrataId: victim, email: null, actor: "joseph", confirm: true);
        Assert.Equal(true, forgotten);
        release.Release();
        var result = await crawl;
        var log = capture.ReadAll();

        // Semantics unchanged: suppression won, nothing lives for the subject.
        Assert.Equal(1, result.ItemsSuppressed);
        Assert.False(rig.Graph.LiveItems().ContainsKey(victimItem));

        // The compensating withdrawal is visible in the log WITH the item id —
        // both the per-item line and the superchunk summary.
        Assert.Contains($"Erasure-race withdrawal completed for {victimItem}", log);
        Assert.Contains("DSAR race: withdrew 1 item(s)", log);

        // No raw subject PII anywhere in the run log.
        Assert.DoesNotContain("Victim Person", log);
        Assert.DoesNotContain("Filler One", log);
    }
}

public class ErasureLedgerDiagnosabilityTests : IDisposable
{
    private readonly string? _previousLogsDir = Environment.GetEnvironmentVariable("LOGS_DIR");

    public void Dispose()
    {
        Logging.ResetForTests();
        Environment.SetEnvironmentVariable("LOGS_DIR", _previousLogsDir);
    }

    [Fact]
    public void CorruptLedgerLogsBrokenSeqAndRefusedAppendIsLoudWithoutEmail()
    {
        var root = TestFixtures.NewTempDir("diag_ledger");
        Environment.SetEnvironmentVariable("LOGS_DIR", Path.Combine(root, "logs"));

        var ledger = new ErasureLedger("DiagLedger", logsDir: root);
        ledger.Append("joseph", ErasureActions.Erase, "P1", "erased-subject@example.com",
            new[] { "PersonProfile-P1" });
        ledger.Append("joseph", ErasureActions.Erase, "P2", null, Array.Empty<string>());

        // External tamper: line 1 no longer parses as a ledger entry.
        var lines = File.ReadAllLines(ledger.Path);
        lines[0] = "{ this is not a ledger entry";
        File.WriteAllLines(ledger.Path, lines);

        using var capture = new RunLogCapture("diag_ledger");
        var fresh = new ErasureLedger("DiagLedger", logsDir: root);   // cold cache → strict reload

        // Verify pinpoints the break (no throw) AND logs brokenAtSeq.
        Assert.False(fresh.Verify(out var broken));
        Assert.Equal(1, broken);

        // Append on a corrupt chain REFUSES (fail-closed, unchanged) and is loud.
        var refusal = Assert.Throws<InvalidDataException>(() =>
            fresh.Append("joseph", ErasureActions.Erase, "P3", "p3-subject@example.com",
                Array.Empty<string>()));
        Assert.Contains("refusing", refusal.Message, StringComparison.OrdinalIgnoreCase);

        var log = capture.ReadAll();
        Assert.Contains("FAILED verification: chain broken at seq 1", log);
        Assert.Contains("REFUSING to append", log);
        Assert.Contains("'P3'", log);                                // subject ID is fine…
        Assert.DoesNotContain("erased-subject@example.com", log);    // …emails never are
        Assert.DoesNotContain("p3-subject@example.com", log);
    }
}

public class StateFileDiagnosabilityTests : IDisposable
{
    private readonly string? _previousLogsDir = Environment.GetEnvironmentVariable("LOGS_DIR");

    public void Dispose()
    {
        Logging.ResetForTests();
        Environment.SetEnvironmentVariable("LOGS_DIR", _previousLogsDir);
    }

    [Fact]
    public void CorruptStateCheckpointAndDeadLetterFilesAreLoggedLoudlyButOnce()
    {
        var root = TestFixtures.NewTempDir("diag_state");
        var logsDir = Path.Combine(root, "logs");
        var dataDir = Path.Combine(root, "data");
        Environment.SetEnvironmentVariable("LOGS_DIR", Path.Combine(root, "runlogs"));
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(dataDir);

        var store = new FileStateStore("DiagState", logsDir: logsDir, dataDir: dataDir);
        File.WriteAllText(store.StatePath, "{ definitely not json");
        File.WriteAllText(store.CheckpointPath, "also not json");
        File.WriteAllText(store.DeadLetterPath, "not-a-dead-letter-line{\n");

        using var capture = new RunLogCapture("diag_state");

        // Behavior unchanged: fall back to empty state / no checkpoint / skip line.
        Assert.Null(store.GetCheckpoint());
        Assert.Empty(store.ListSuppressedSubjects());
        Assert.Empty(store.ReadDeadLetters());
        // Repeat reads must NOT re-warn (these run on hot/polled paths).
        Assert.Null(store.GetCheckpoint());
        Assert.Empty(store.ReadDeadLetters());

        var log = capture.ReadAll();
        Assert.Contains($"State file '{store.StatePath}' is unreadable", log);
        Assert.Contains("EMPTY state document", log);
        Assert.Contains("suppression list", log);       // the operator learns what was lost
        Assert.Contains($"Checkpoint file '{store.CheckpointPath}' is unreadable", log);
        Assert.Contains("restarts from record 0", log);
        Assert.Contains("1 malformed line(s)", log);
        Assert.Contains("(first at line 1)", log);

        // Warn-once per file: exactly one warning per corrupt file despite repeats.
        Assert.Equal(1, CountOf(log, "Checkpoint file '"));
        Assert.Equal(1, CountOf(log, "State file '"));
        Assert.Equal(1, CountOf(log, "malformed line(s)"));

        static int CountOf(string haystack, string needle) =>
            (haystack.Length - haystack.Replace(needle, "").Length) / needle.Length;
    }
}

public class TopLevelHandlerDiagnosabilityTests : IDisposable
{
    private readonly string? _previousLogsDir = Environment.GetEnvironmentVariable("LOGS_DIR");
    private readonly string? _previousConnectorId = Environment.GetEnvironmentVariable("CONNECTOR_ID");

    public void Dispose()
    {
        Logging.ResetForTests();
        Environment.SetEnvironmentVariable("LOGS_DIR", _previousLogsDir);
        Environment.SetEnvironmentVariable("CONNECTOR_ID", _previousConnectorId);
        ServiceStop.ResetForTests();
    }

    [Fact]
    public async Task ConfigurationErrorEscapingACommandExitsNonzeroAndNamesTheSetting()
    {
        // CONNECTOR_ID "ab" is invalid (3-32 chars required) → AppConfig.Load
        // throws ConfigurationError from inside Runtime.Create, BEFORE the
        // handler's own try — i.e. the top-level command handler must catch it.
        var root = TestFixtures.NewTempDir("diag_toplevel");
        Environment.SetEnvironmentVariable("LOGS_DIR", Path.Combine(root, "logs"));
        Environment.SetEnvironmentVariable("CONNECTOR_ID", "ab");
        Logging.ResetForTests();   // the command opens its own run under LOGS_DIR

        var exitCode = await Program.ExecuteAsync(new[] { "seat-sync" });
        Assert.Equal(1, exitCode);

        Logging.ResetForTests();
        var file = Directory.EnumerateFiles(Path.Combine(root, "logs"), "connector.log",
            SearchOption.AllDirectories).Single();
        var log = File.ReadAllText(file);

        // Structured final error: which command, that it was config, which setting.
        Assert.Contains("Command 'seat-sync' aborted by a configuration error", log);
        Assert.Contains("CONNECTOR_ID", log);
    }
}
