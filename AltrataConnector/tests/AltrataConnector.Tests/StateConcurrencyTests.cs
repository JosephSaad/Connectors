// StateConcurrencyTests.cs
// ------------------------
// Cross-process reader/writer invariants for the two state files a crawl keeps
// touching: the checkpoint and the dead-letter queue.
//
// The scenario is not hypothetical — WriteAtomic names it: "an operator running
// ingest-item while a crawl runs under the same CONNECTOR_ID". Two processes,
// two independent sets of handles.
//
// WHY THESE TESTS DRIVE File.Move DIRECTLY RATHER THAN CALLING SaveCheckpoint.
// Every FileStateStore method takes StateLock, a process-wide lock keyed on the
// state path, so a reader and a writer in the SAME process can never overlap and
// an in-process race test would prove nothing. The second process is therefore
// simulated the only way it can be: by performing the publish — a temp file plus
// File.Move(tmp, path, overwrite: true), exactly what AtomicWrite does — outside
// the lock, while the store's own reader runs against it.
//
// Windows enforces share modes; POSIX treats them as advisory. On POSIX these
// tests still assert the atomicity half (a reader must never observe the file as
// missing or torn); on windows-latest they assert the share modes too, which is
// where both of the defects behind this file actually failed.

using System.Text;
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
    /// Publish a file the way AtomicWrite does — unique temp, then rename over
    /// the target — without taking StateLock, i.e. as a second process would.
    /// </summary>
    private static void PublishByRename(string path, string content)
    {
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

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
            catch (IOException)
            {
                // The other half of the same defect: a reader holding the file
                // without shared delete access makes the rename itself fail.
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
            catch (IOException)
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
