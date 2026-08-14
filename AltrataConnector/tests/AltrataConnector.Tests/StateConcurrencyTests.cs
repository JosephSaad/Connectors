// StateConcurrencyTests.cs
// ------------------------
// Cross-process reader/writer invariants for the two state files a crawl keeps
// touching: the checkpoint and the dead-letter queue.
//
// The scenario is not hypothetical — WriteAtomic names it: "an operator running
// ingest-item while a crawl runs under the same CONNECTOR_ID". Two processes,
// two independent sets of handles.
//
// WHY THESE TESTS PUBLISH OUTSIDE StateLock RATHER THAN CALLING SaveCheckpoint.
// Every public FileStateStore method takes StateLock, a process-wide lock keyed
// on the state path, so a reader and a writer in the SAME process can never
// overlap and an in-process race test would prove nothing. The second process is
// simulated by driving AtomicWrite — the real publisher — through its test seam,
// which sits below the lock, while the store's own reader runs against it.
//
// Windows enforces share modes; POSIX treats them as advisory. On POSIX these
// tests still assert the atomicity half (a reader must never observe the file as
// missing or torn); on windows-latest they assert the share modes too, which is
// where every defect behind this file actually failed.
//
// THREE DEFECTS, and the third was found by the first version of these tests
// failing on CI:
//
//   1. GetCheckpoint read with File.ReadAllText (FileShare.Read) while
//      SaveCheckpoint publishes by rename — the rename fails permanently while a
//      reader holds the file.
//   2. The dead-letter reader omitted FileShare.Delete on the strength of a
//      comment claiming the queue is never republished by rename.
//      ReplaceDeadLetters republishes it by rename.
//   3. AtomicWrite published with a bare File.Move. Shared delete access lets
//      the rename succeed, but the replaced file lingers delete-pending until
//      the reader closes, and a publish inside that window gets
//      ERROR_ACCESS_DENIED. The other four connectors have retried this since
//      the state-file work; Altrata never did.
//
// 1 and 2 make the failure permanent, 3 makes it transient, and fixing only the
// first two leaves the test red — which is how 3 surfaced.

using System.Text.Json;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public sealed class StateDirScope : IDisposable
{
    public StateDirScope()
    {
        Dir = Directory.CreateTempSubdirectory("altrata_state_").FullName;
        Store = new FileStateStore("AltrataConcurrency", logsDir: Dir, dataDir: Dir);
    }

    public string Dir { get; }

    public FileStateStore Store { get; }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

public class StateConcurrencyTests
{
    private static CrawlCheckpoint Checkpoint(int recordIndex) => new()
    {
        DeliveryId = "delivery-1",
        Dataset = "people",
        FileName = "people.jsonl",
        RecordIndex = recordIndex,
        UpdatedUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// Publish through the PRODUCTION writer, outside StateLock — i.e. exactly
    /// what a second process does.
    /// </summary>
    /// <remarks>
    /// This deliberately calls AtomicWriteForTests rather than hand-rolling a
    /// temp-and-rename. The first version of this test did hand-roll it, and it
    /// failed on windows-latest inside the test helper — which was the correct
    /// result for the wrong reason: it proved the race was real but told us
    /// nothing about whether production survives it, because the hand-rolled
    /// publisher and the real one had drifted. Driving the real writer is what
    /// makes the assertion mean "Altrata publishes state safely" instead of
    /// "this test file publishes state safely".
    /// </remarks>
    private static void PublishByRename(string path, string content) =>
        FileStateStore.AtomicWriteForTests(path, content);

    /// <summary>
    /// Reading the checkpoint while another process republishes it must neither
    /// throw, nor block that publish, nor report the checkpoint as absent.
    ///
    /// "Absent" is the outcome that matters and the reason this asserts on a
    /// count rather than on an exception. GetCheckpoint catches everything and
    /// returns null, deliberately — an unreadable checkpoint is treated as no
    /// checkpoint because PUTs are idempotent. That is the right call for a
    /// genuinely corrupt file and the wrong one for a sharing violation: the
    /// resume position is discarded and the interrupted delivery re-ingests from
    /// record 0, with a "corrupt file" warning that names the wrong cause.
    /// </summary>
    [Fact]
    public async Task ConcurrentCheckpointPublishAndRead_TheCheckpointNeverVanishes()
    {
        using var scope = new StateDirScope();
        scope.Store.SaveCheckpoint(Checkpoint(0));
        Assert.NotNull(scope.Store.GetCheckpoint());

        var stop = false;
        var vanished = 0;
        var readerErrors = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    if (scope.Store.GetCheckpoint() is null)
                        Interlocked.Increment(ref vanished);
                }
                catch
                {
                    Interlocked.Increment(ref readerErrors);
                }
            }
        });

        var publishErrors = 0;
        for (var i = 1; i <= 300; i++)
        {
            try
            {
                PublishByRename(
                    scope.Store.CheckpointPath,
                    JsonSerializer.Serialize(Checkpoint(i)));
            }
            catch (Exception exc) when (exc is UnauthorizedAccessException or IOException)
            {
                // The other half of the same defect. Without FileShare.Delete on
                // the reader this fails permanently; with it, but without the
                // publisher's retry, it fails transiently inside Windows'
                // delete-pending window. Both land here.
                Interlocked.Increment(ref publishErrors);
            }
        }

        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, readerErrors);
        Assert.Equal(0, publishErrors);
        Assert.Equal(0, vanished);
        Assert.NotNull(scope.Store.GetCheckpoint());
    }

    /// <summary>
    /// The dead-letter queue is republished by rename too — ReplaceDeadLetters
    /// goes through AtomicWrite — so its reader needs shared delete access for
    /// the same reason the checkpoint's does.
    ///
    /// The reader's remark used to assert the opposite: that the queue "is only
    /// ever appended to and never republished by rename". It is a good rule
    /// stated about the wrong file. This test is what makes the claim checkable
    /// rather than a comment nobody can falsify.
    /// </summary>
    [Fact]
    public async Task ConcurrentDeadLetterReplaceAndRead_ReaderNeverThrows()
    {
        using var scope = new StateDirScope();
        scope.Store.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "seed",
            Dataset = "people",
            Error = "seed",
        });

        var stop = false;
        var readerErrors = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    _ = scope.Store.ReadDeadLetters().Count;
                }
                catch
                {
                    Interlocked.Increment(ref readerErrors);
                }
            }
        });

        var publishErrors = 0;
        for (var i = 1; i <= 200; i++)
        {
            try
            {
                PublishByRename(
                    scope.Store.DeadLetterPath,
                    JsonSerializer.Serialize(new DeadLetterRecord
                    {
                        ItemId = $"item-{i}",
                        Dataset = "people",
                        Error = $"error {i}",
                    }) + "\n");
            }
            catch (Exception exc) when (exc is UnauthorizedAccessException or IOException)
            {
                Interlocked.Increment(ref publishErrors);
            }
        }

        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, readerErrors);
        Assert.Equal(0, publishErrors);
    }
}
