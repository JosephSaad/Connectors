// StateConcurrencyTests.cs
// ------------------------
// The concurrency harness this connector did not have.
//
// Two Windows-only defect classes reached CI from this suite rather than being
// caught locally, and both for the same reason: nothing here exercised a READER
// running while a writer holds the file.
//
//   * the dead-letter queue (fixed fleet-wide in #22/#23) — this connector took
//     the source fix with no test of its own, because there was no fixture to
//     hang one on;
//   * the checkpoint (fixed in #34) — found only when Round2HaPipelineTests
//     tripped over it on windows-latest, three PRs after the same bug had
//     already been fixed twenty lines away in the same file.
//
// Windows enforces share modes and POSIX treats them as advisory, so both are
// invisible on the macOS and Linux machines this is developed on and only ever
// fail on Windows Server, the deployment target. These tests are the thing that
// makes a green local run mean something.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;

namespace SalesforceCopilotConnector.Tests;

/// <summary>
/// Redirects <see cref="SyncState.LogsDir"/> at a private temp directory for the
/// life of a test and restores it after, so state-file tests neither collide
/// with each other nor leave anything behind.
/// </summary>
public sealed class SyncStateScope : IDisposable
{
    private readonly string _previousLogsDir;

    public SyncStateScope()
    {
        Dir = Directory.CreateTempSubdirectory("sf_state_").FullName;
        _previousLogsDir = SyncState.LogsDir;
        SyncState.LogsDir = Dir;
        SyncState.ResetProviderCache();  // ensure the file backend is active
    }

    public string Dir { get; }

    public void Dispose()
    {
        SyncState.LogsDir = _previousLogsDir;
        SyncState.ResetProviderCache();
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

/// <summary>
/// Reader-during-write invariants for the two files a crawl keeps open: the
/// dead-letter queue and the checkpoint.
/// </summary>
[Collection("EnvVars")]
public class StateConcurrencyTests
{
    private const string Connector = "StateConcurrencyConnector";

    /// <summary>
    /// Reading the dead-letter queue WHILE a crawl appends to it must not throw.
    ///
    /// The distinction from a concurrent-WRITER test is the whole point, and it
    /// is why the existing suites missed this class for so long: writers are
    /// serialised by the in-process dead-letter lock, so only ever one write
    /// handle exists and no sharing conflict is possible. A reader takes its own
    /// handle. On Windows a reader whose share mode excludes Write is refused
    /// while an appending writer holds the file, so end-of-run dead-letter
    /// accounting and retry-failed die mid-crawl.
    /// </summary>
    [Fact]
    public async Task ConcurrentAppendAndRead_ReaderNeverThrows()
    {
        using var scope = new SyncStateScope();
        var path = SyncState.FailedRecordsPath(Connector);
        var stop = false;
        var readerErrors = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    _ = SyncState.ReadFailedRecords(Connector);
                }
                catch
                {
                    Interlocked.Increment(ref readerErrors);
                }
            }
        });

        for (var i = 0; i < 200; i++)
        {
            SyncState.AppendFailedRecords(
                path, new[] { $"item-{i}" }, "Account", $"error {i}");
        }

        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, readerErrors);
        Assert.Equal(200, SyncState.ReadFailedRecords(Connector).Count);
    }

    /// <summary>
    /// Reading the checkpoint WHILE another node writes it must neither throw nor
    /// lose the checkpoint.
    ///
    /// Two defects sit behind this and the second is the dangerous one. The share
    /// mode is the loud half: an overlapping read throws on Windows. The quiet
    /// half is atomicity — a truncate-in-place write lets a reader observe a
    /// half-written file, and torn JSON is swallowed and reported as "no
    /// checkpoint", so a resume silently restarts the crawl from the beginning
    /// and re-ingests everything with nothing in the log to say why.
    /// </summary>
    [Fact]
    public async Task ConcurrentCheckpointWriteAndRead_ReaderNeverThrowsAndNeverLosesTheCheckpoint()
    {
        using var scope = new SyncStateScope();
        const string Since = "2026-01-01T00:00:00Z";

        // Establish one first: from here a null read can only mean the reader
        // caught the file mid-truncate.
        SyncState.WriteCheckpoint(Connector, Since, "Account", 0);
        Assert.NotNull(SyncState.ReadCheckpoint(Connector));

        var stop = false;
        var readerErrors = 0;
        var vanished = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    if (SyncState.ReadCheckpoint(Connector) is null)
                    {
                        Interlocked.Increment(ref vanished);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref readerErrors);
                }
            }
        });

        for (var i = 1; i <= 300; i++)
        {
            SyncState.WriteCheckpoint(Connector, Since, "Account", i);
        }

        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, readerErrors);
        Assert.Equal(0, vanished);

        var final = SyncState.ReadCheckpoint(Connector);
        Assert.NotNull(final);
        Assert.Equal(300, final!["completed"]!["Account"]!.GetValue<int>());
    }
}
