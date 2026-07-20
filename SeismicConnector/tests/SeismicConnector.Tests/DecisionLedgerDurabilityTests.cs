// DecisionLedgerDurabilityTests.cs
// --------------------------------
// The decision ledger is only worth having if it (a) actually gets written by
// every command that can make a compliance decision, (b) survives log
// retention, and (c) forms ONE chain an auditor can verify end to end. These
// tests pin all three. Each of the first three FAILED against the pre-fix
// connector, for a different reason:
//
//   * `ingest` never called OpenLedger, so DECISION_LEDGER=true silently
//     produced the in-memory no-op ledger and no file at all — every exclusion
//     and ACL-restriction decision of that run was lost.
//   * the ledger was written inside logs/{run}/, which LOG_RETENTION_DAYS
//     deletes wholesale — retention destroyed the audit records it was meant
//     to preserve.
//   * the path carried a per-run timestamp and the writer truncated, so every
//     run restarted the hash chain from genesis: deleting one run's ledger
//     entirely was undetectable, because nothing linked the runs together.
//
// The ledger-location assertions here glob for decision_ledger_*.jsonl rather
// than hard-coding a directory, so they describe the REQUIRED behaviour (one
// chain, outside the pruner's blast radius) independently of the exact path.

using SeismicConnector.Commands;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

/// <summary>Redirects CommandRegistry.LogsDir (the ledger's root) for a test's lifetime.</summary>
public sealed class TempLogsDir : IDisposable
{
    public string Dir { get; }
    private readonly string _previous;

    public TempLogsDir()
    {
        Dir = Path.Combine(Path.GetTempPath(), "seismic-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        _previous = CommandRegistry.LogsDir;
        CommandRegistry.LogsDir = Dir;
    }

    /// <summary>Create a run directory the way CommandRegistry.SetupLogging would, and return its log file.</summary>
    public string NewRunLogFile(string prefix = "ingest")
    {
        // Distinct per call: real runs never share a run directory.
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var runDir = Path.Combine(Dir, $"{prefix}_{stamp}");
        for (var n = 1; Directory.Exists(runDir); n++)
            runDir = Path.Combine(Dir, $"{prefix}_{stamp[..^1]}{n}");
        Directory.CreateDirectory(runDir);
        return Path.Combine(runDir, $"{Path.GetFileName(runDir)}.log");
    }

    /// <summary>Every decision ledger anywhere under the logs root.</summary>
    public string[] LedgerFiles() =>
        Directory.GetFiles(Dir, "decision_ledger_*.jsonl", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        CommandRegistry.LogsDir = _previous;
        Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, null);
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch
        {
        }
    }
}

public class DecisionLedgerCommandWiringTests
{
    /// <summary>A Runtime assembled from the pipeline harness — no env, no network.</summary>
    private static Runtime RuntimeFor(PipelineHarness harness) => new()
    {
        Config = harness.Config,
        Seismic = harness.Seismic,
        Graph = harness.Graph,
        Store = harness.Store,
        Pipeline = harness.Pipeline,
        Connection = new ConnectionManager(harness.Graph, harness.Config),
    };

    /// <summary>c1 is flagged MNE (so it is excluded and ledgered); c2 is clean.</summary>
    private static PipelineHarness ExcludingHarness()
    {
        var harness = new PipelineHarness(exclusions: new ExclusionRules
        {
            ExcludedFlags = new List<string> { "MNE" },
            FlagProperties = new List<string> { "classification" },
        });
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = "classification", Value = "MNE" },
        }));
        harness.AddContent(TestContent.Make("c2"));
        return harness;
    }

    /// <summary>
    /// DEFECT 1: DECISION_LEDGER=true + the plain `ingest` command must produce a
    /// REAL ledger file carrying that run's decisions. Before the fix `ingest`
    /// opened the report and the manifest but never the ledger, so the flag was
    /// silently a no-op on every command except full-deployment.
    /// </summary>
    [Fact]
    public async Task IngestCommandPath_WithLedgerEnabled_WritesARealLedgerFileWithTheRunsDecisions()
    {
        using var logs = new TempLogsDir();
        using var harness = ExcludingHarness();
        Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, "true");

        var logFile = logs.NewRunLogFile();
        Assert.True(await IngestCommands.RunIngestOnceAsync(
            RuntimeFor(harness), logFile, logFile + ".summary", "connected", 1.0));

        var files = logs.LedgerFiles();
        Assert.True(files.Length == 1, $"expected exactly one decision ledger, found {files.Length}");

        var entries = DecisionLedger.ReadFile(files[0]);
        Assert.Contains(entries, e => e.ItemId == "c1" && e.Decision == DecisionLedger.DecisionExclude);
        Assert.DoesNotContain(entries, e => e.ItemId == "c2");
        Assert.True(DecisionLedger.Verify(entries).Valid);
    }

    /// <summary>The flag stays opt-in: unset means no file anywhere.</summary>
    [Fact]
    public async Task IngestCommandPath_WithLedgerDisabled_WritesNoLedgerFile()
    {
        using var logs = new TempLogsDir();
        using var harness = ExcludingHarness();
        Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, null);

        var logFile = logs.NewRunLogFile();
        Assert.True(await IngestCommands.RunIngestOnceAsync(
            RuntimeFor(harness), logFile, logFile + ".summary", "connected", 1.0));

        Assert.Empty(logs.LedgerFiles());
        // The decisions were still MADE — only their file-backed audit is off.
        Assert.Contains(harness.Pipeline.Ledger.Entries, e => e.ItemId == "c1");
    }

    /// <summary>
    /// DEFECT 2, third problem: the chain must CONTINUE across runs. Two
    /// successive ingest runs against the same logs root produce ONE ledger
    /// whose seqs keep incrementing, whose run-2 entries link back to run 1 via
    /// prev_hash, and which Verify() accepts over the COMBINED chain.
    /// </summary>
    [Fact]
    public async Task LedgerChain_ContinuesAcrossTwoSuccessiveRuns()
    {
        using var logs = new TempLogsDir();
        Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, "true");

        using (var harness = ExcludingHarness())
        {
            var logFile = logs.NewRunLogFile();
            await IngestCommands.RunIngestOnceAsync(
                RuntimeFor(harness), logFile, logFile + ".summary", "connected", 1.0);
        }

        var afterRun1Files = logs.LedgerFiles();
        Assert.Single(afterRun1Files);
        var path = afterRun1Files[0];
        var afterRun1 = DecisionLedger.ReadFile(path);
        Assert.NotEmpty(afterRun1);

        using (var harness = ExcludingHarness())
        {
            var logFile = logs.NewRunLogFile();
            await IngestCommands.RunIngestOnceAsync(
                RuntimeFor(harness), logFile, logFile + ".summary", "connected", 1.0);
        }

        // ONE chain — not one file per run.
        Assert.Equal(new[] { path }, logs.LedgerFiles());

        var combined = DecisionLedger.ReadFile(path);
        Assert.True(combined.Count > afterRun1.Count, "run 2 appended nothing to the chain");

        // Run 1's records are byte-for-byte untouched...
        Assert.Equal(afterRun1, combined.Take(afterRun1.Count).ToList());
        // ...seqs carry on rather than restarting...
        for (var i = 0; i < combined.Count; i++)
            Assert.Equal(i, combined[i].Seq);
        // ...run 2's first entry links to run 1's last, and there is only ONE genesis...
        var firstOfRun2 = combined[afterRun1.Count];
        Assert.Equal(afterRun1[^1].Hash, firstOfRun2.PrevHash);
        Assert.NotEqual(DecisionLedger.GenesisHash, firstOfRun2.PrevHash);
        Assert.Equal(1, combined.Count(e => e.PrevHash == DecisionLedger.GenesisHash));
        // ...so the whole thing verifies as a single continuous chain.
        Assert.True(DecisionLedger.Verify(combined).Valid);
    }

    /// <summary>
    /// The ledger must NOT live inside logs/{run}/ — that directory is exactly
    /// what LOG_RETENTION_DAYS deletes.
    /// </summary>
    [Fact]
    public async Task LedgerIsNotWrittenInsideTheRunDirectory()
    {
        using var logs = new TempLogsDir();
        using var harness = ExcludingHarness();
        Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, "true");

        var logFile = logs.NewRunLogFile();
        var runDir = Path.GetDirectoryName(logFile)!;
        await IngestCommands.RunIngestOnceAsync(
            RuntimeFor(harness), logFile, logFile + ".summary", "connected", 1.0);

        Assert.Empty(Directory.GetFiles(runDir, "decision_ledger_*.jsonl", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(logs.Dir, "decision_ledger_*.jsonl", SearchOption.TopDirectoryOnly));
    }

    /// <summary>
    /// The omission was not unique to `ingest`: ingest-object and ingest-item run
    /// the same decision-making pipeline and were missing the ledger too.
    /// </summary>
    [Fact]
    public async Task IngestObjectCommandPath_AlsoWritesTheLedger()
    {
        using var logs = new TempLogsDir();
        using var harness = ExcludingHarness();
        Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, "true");

        var logFile = logs.NewRunLogFile("ingest-object");
        await IngestCommands.RunIngestObjectOnceAsync(
            RuntimeFor(harness), "ContentItem", logFile, logFile + ".summary", 1.0);

        var files = logs.LedgerFiles();
        Assert.Single(files);
        var entries = DecisionLedger.ReadFile(files[0]);
        Assert.Contains(entries, e => e.ItemId == "c1" && e.Decision == DecisionLedger.DecisionExclude);
        Assert.True(DecisionLedger.Verify(entries).Valid);
    }

    [Fact]
    public async Task IngestItemCommandPath_AlsoWritesTheLedger()
    {
        using var logs = new TempLogsDir();
        using var harness = ExcludingHarness();
        Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, "true");

        var logFile = logs.NewRunLogFile("ingest-item");
        await IngestCommands.RunIngestItemOnceAsync(
            RuntimeFor(harness), "c1", "ts1", logFile, logFile + ".summary", 1.0);

        var files = logs.LedgerFiles();
        Assert.Single(files);
        var entries = DecisionLedger.ReadFile(files[0]);
        Assert.Contains(entries, e => e.ItemId == "c1" && e.Decision == DecisionLedger.DecisionExclude);
        Assert.True(DecisionLedger.Verify(entries).Valid);
    }
}

public class DecisionLedgerRetentionTests : IDisposable
{
    public void Dispose() => Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, null);

    /// <summary>
    /// DEFECT 2: LOG_RETENTION_DAYS deletes whole run directories. It must never
    /// take a decision ledger with it — including a LEGACY per-run ledger left
    /// inside a run directory by a release that predates the stable-path move.
    /// </summary>
    [Fact]
    public void Pruning_NeverDeletesADecisionLedger_EvenInsideAnExpiredRunDirectory()
    {
        using var logs = new TempLogsDir();
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, "7");
        var now = new DateTime(2026, 7, 12, 12, 0, 0);

        var expiredWithLedger = Path.Combine(logs.Dir, "full-deployment_20260601_090000");
        var expiredPlain = Path.Combine(logs.Dir, "ingest_20260601_091500");
        Directory.CreateDirectory(expiredWithLedger);
        Directory.CreateDirectory(expiredPlain);
        var legacyLedger = Path.Combine(
            expiredWithLedger, "decision_ledger_SeismicSales_20260601_090000.jsonl");
        File.WriteAllText(legacyLedger, "{}\n");
        File.WriteAllText(Path.Combine(expiredWithLedger, "run.log"), "log\n");
        File.WriteAllText(Path.Combine(expiredPlain, "run.log"), "log\n");

        // The current (stable-path) ledger sits at the logs root.
        var currentLedger = Path.Combine(logs.Dir, "decision_ledger_SeismicSales.jsonl");
        File.WriteAllText(currentLedger, "{}\n");

        var pruned = LogPruner.Prune(logs.Dir, now);

        // The ledger-free expired directory goes; the one holding audit records stays.
        Assert.Equal(1, pruned);
        Assert.False(Directory.Exists(expiredPlain));
        Assert.True(File.Exists(legacyLedger), "retention deleted a legacy per-run decision ledger");
        Assert.True(File.Exists(currentLedger), "retention deleted the decision ledger at the logs root");
    }

    /// <summary>A ledger nested deeper inside an expired run directory is protected too.</summary>
    [Fact]
    public void Pruning_SkipsAnExpiredRunDirectoryWithANestedLedger()
    {
        using var logs = new TempLogsDir();
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, "7");
        var now = new DateTime(2026, 7, 12, 12, 0, 0);

        var expired = Path.Combine(logs.Dir, "ingest_20260601_090000");
        var nested = Path.Combine(expired, "audit");
        Directory.CreateDirectory(nested);
        var ledger = Path.Combine(nested, "decision_ledger_SeismicSales_20260601_090000.jsonl");
        File.WriteAllText(ledger, "{}\n");

        Assert.Equal(0, LogPruner.Prune(logs.Dir, now));
        Assert.True(File.Exists(ledger));
    }

    /// <summary>Ordinary expired run directories are still pruned — the guard is narrow.</summary>
    [Fact]
    public void Pruning_StillRemovesExpiredRunDirectoriesWithoutLedgers()
    {
        using var logs = new TempLogsDir();
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, "7");
        var now = new DateTime(2026, 7, 12, 12, 0, 0);

        var expired = Path.Combine(logs.Dir, "ingest_20260601_090000");
        Directory.CreateDirectory(expired);
        File.WriteAllText(Path.Combine(expired, "reconciliation_SeismicSales.jsonl"), "{}\n");

        Assert.Equal(1, LogPruner.Prune(logs.Dir, now));
        Assert.False(Directory.Exists(expired));
    }
}

public class DecisionLedgerResumeTests
{
    /// <summary>Re-opening a ledger file resumes its chain instead of truncating it.</summary>
    [Fact]
    public void ReopeningAFileBackedLedger_ResumesTheChain_AndDoesNotTruncate()
    {
        using var logs = new TempLogsDir();
        var path = Path.Combine(logs.Dir, "decision_ledger_SeismicSales.jsonl");

        using (var first = new DecisionLedger(path))
        {
            first.Append("c1", DecisionLedger.DecisionExclude, "r1");
            first.Append("c2", DecisionLedger.DecisionExclude, "r2");
        }

        using (var second = new DecisionLedger(path))
        {
            var e = second.Append("c3", DecisionLedger.DecisionAclRestrict, "r3");
            Assert.Equal(2, e.Seq);
            Assert.NotEqual(DecisionLedger.GenesisHash, e.PrevHash);
            // The instance verifies its own segment against the resumed anchor.
            Assert.True(second.Verify().Valid);
        }

        var all = DecisionLedger.ReadFile(path);
        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { "c1", "c2", "c3" }, all.Select(e => e.ItemId).ToArray());
        Assert.True(DecisionLedger.Verify(all).Valid);
    }

    /// <summary>
    /// A crash mid-append leaves a torn final line. Resuming must drop only that
    /// uncommitted fragment (never a complete record) so the continued chain
    /// stays readable — otherwise a single crash would poison the file forever.
    /// </summary>
    [Fact]
    public void ResumingOverATornFinalLine_DropsOnlyTheFragment_AndTheChainStaysVerifiable()
    {
        using var logs = new TempLogsDir();
        var path = Path.Combine(logs.Dir, "decision_ledger_SeismicSales.jsonl");

        using (var first = new DecisionLedger(path))
        {
            first.Append("c1", DecisionLedger.DecisionExclude, "r1");
            first.Append("c2", DecisionLedger.DecisionExclude, "r2");
        }
        File.AppendAllText(path, """{"Seq":2,"ItemId":"c3","Deci""");   // torn crash-tail

        using (var second = new DecisionLedger(path))
            second.Append("c4", DecisionLedger.DecisionExclude, "r4");

        var all = DecisionLedger.ReadFile(path);
        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { "c1", "c2", "c4" }, all.Select(e => e.ItemId).ToArray());
        Assert.True(DecisionLedger.Verify(all).Valid);
    }

    /// <summary>
    /// DEFECT (a) — NEWLINE-BOUNDARY TEAR. A crash can tear the file at a line
    /// BOUNDARY: the final record's bytes are all on disk (Append already
    /// returned it to the caller and flushed it) but its '\n' terminator never
    /// landed. That record is durable, acknowledged audit evidence.
    /// <para>
    /// Resuming used to treat it as an uncommitted crash-tail and TRUNCATE it,
    /// and because the surviving prefix plus the newly appended entries still
    /// form an internally consistent chain, Verify() returned CLEAN afterwards:
    /// silent evidence loss with a PASSING verification — the one failure a
    /// tamper-evident ledger must never have. The record must survive.
    /// </para>
    /// </summary>
    [Fact]
    public void ResumingOverANewlineBoundaryTear_KeepsTheAcknowledgedFinalRecord()
    {
        using var logs = new TempLogsDir();
        var path = Path.Combine(logs.Dir, "decision_ledger_SeismicSales.jsonl");

        using (var first = new DecisionLedger(path))
        {
            first.Append("c1", DecisionLedger.DecisionExclude, "r1");
            first.Append("c2", DecisionLedger.DecisionExclude, "r2");
            first.Append("c3", DecisionLedger.DecisionAclRestrict, "r3");   // acknowledged + flushed
        }

        // The tear: drop ONLY the trailing newline. Every byte of c3's record is
        // still on disk — nothing about the record itself is partial.
        var onDisk = File.ReadAllText(path);
        Assert.EndsWith("\n", onDisk);
        File.WriteAllText(path, onDisk[..^1]);

        using (var second = new DecisionLedger(path))
            second.Append("c4", DecisionLedger.DecisionExclude, "r4");

        var all = DecisionLedger.ReadFile(path);
        Assert.Equal(new[] { "c1", "c2", "c3", "c4" }, all.Select(e => e.ItemId).ToArray());
        // ...and the repaired file is a single well-formed chain: c4 must not have
        // been concatenated onto c3's unterminated line.
        Assert.Equal(4, all.Count);
        Assert.True(DecisionLedger.Verify(all).Valid);
        Assert.Equal(4, File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l)));
    }

    /// <summary>
    /// DEFECT (b) — BLANK-LINE TEAR. A torn record followed by a blank line (a
    /// partial write whose newline landed, then nothing) used to be MARKED
    /// COMMITTED by the blank line: resuming left the torn record in place, the
    /// next append put a real record after it, and from that moment ReadFile
    /// refused the file forever as interior corruption — a single crash bricked
    /// the audit trail for the life of the file.
    /// </summary>
    [Fact]
    public void ResumingOverATornLineFollowedByABlankLine_DoesNotBrickTheFile()
    {
        using var logs = new TempLogsDir();
        var path = Path.Combine(logs.Dir, "decision_ledger_SeismicSales.jsonl");

        using (var first = new DecisionLedger(path))
        {
            first.Append("c1", DecisionLedger.DecisionExclude, "r1");
            first.Append("c2", DecisionLedger.DecisionExclude, "r2");
        }
        // Torn crash-tail whose newline made it to disk, plus a blank line.
        File.AppendAllText(path, "{\"Seq\":2,\"ItemId\":\"c3\",\"Deci\n\n");

        using (var second = new DecisionLedger(path))
            second.Append("c4", DecisionLedger.DecisionExclude, "r4");

        // Must not throw: the torn tail was never a committed record, so it is
        // discarded on resume rather than turned into interior corruption.
        var all = DecisionLedger.ReadFile(path);
        Assert.Equal(new[] { "c1", "c2", "c4" }, all.Select(e => e.ItemId).ToArray());
        Assert.True(DecisionLedger.Verify(all).Valid);

        // Re-opening the repaired file a second time must stay stable.
        using (var third = new DecisionLedger(path))
            third.Append("c5", DecisionLedger.DecisionExclude, "r5");
        Assert.True(DecisionLedger.Verify(DecisionLedger.ReadFile(path)).Valid);
    }

    /// <summary>
    /// The narrow guard must not swallow GENUINE interior corruption: a
    /// malformed line with real records after it stays in the file and stays
    /// visible to Verify (via ReadFile throwing).
    /// </summary>
    [Fact]
    public void ResumingDoesNotDiscardInteriorCorruptionThatHasRecordsAfterIt()
    {
        using var logs = new TempLogsDir();
        var path = Path.Combine(logs.Dir, "decision_ledger_SeismicSales.jsonl");

        using (var first = new DecisionLedger(path))
        {
            first.Append("c1", DecisionLedger.DecisionExclude, "r1");
            first.Append("c2", DecisionLedger.DecisionExclude, "r2");
        }
        var lines = File.ReadAllLines(path).ToList();
        lines.Insert(1, "{ not valid json");           // interior, records follow it
        File.WriteAllText(path, string.Join('\n', lines) + "\n");

        using (var second = new DecisionLedger(path))
            second.Append("c3", DecisionLedger.DecisionExclude, "r3");

        Assert.Contains("{ not valid json", File.ReadAllText(path));
        Assert.Throws<System.Text.Json.JsonException>(() => DecisionLedger.ReadFile(path));
    }

    /// <summary>Legacy per-run ledgers are discoverable so the operator can be told where they are.</summary>
    [Fact]
    public void LegacyPerRunLedgers_AreFound_NotSilentlyOrphaned()
    {
        using var logs = new TempLogsDir();
        var runDir = Path.Combine(logs.Dir, "full-deployment_20260601_090000");
        Directory.CreateDirectory(runDir);
        var legacy = Path.Combine(runDir, "decision_ledger_SeismicSales_20260601_090000.jsonl");
        File.WriteAllText(legacy, "{}\n");

        // The current stable-path ledger must NOT be mistaken for a legacy one.
        File.WriteAllText(Path.Combine(logs.Dir, "decision_ledger_SeismicSales.jsonl"), "{}\n");

        Assert.Equal(new[] { legacy }, DecisionLedger.FindLegacyRunLedgers(logs.Dir).ToArray());
    }
}
