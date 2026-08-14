// IdentityStoreOpenContentionTests.cs
// -----------------------------------
// Opening the identity store must survive other processes opening it at the same
// moment.
//
// Two steps of the open need a brief EXCLUSIVE lock and neither is covered by
// busy_timeout:
//
//   * switching journal_mode to WAL — the pragma that SETS busy_timeout has not
//     run yet, and the transition takes an exclusive lock the busy handler is
//     deliberately not consulted for;
//   * the CREATE TABLE / ALTER TABLE that InitSchema runs on EVERY open.
//
// So two openers colliding get SQLITE_BUSY ("database is locked") thrown straight
// out of the constructor. The collision is ordinary operations, not a stress
// scenario: a service restart overlapping the outgoing process, an HA pair coming
// up together, or a CLI command run while a crawl is going.
//
// This connector's own constructor comment already named the collision exactly —
// "a restart or failover overlapping the outgoing process, and every reopen
// re-running the CREATE TABLE statements below, which need a write lock" — and
// then did nothing about it. A documented risk with no handling and no test is
// indistinguishable from an undocumented one.

using SeismicConnector.Graph;

namespace SeismicConnector.Tests;

public class IdentityStoreOpenContentionTests
{
    /// <summary>
    /// Many processes opening the same NEW database at once must all succeed.
    ///
    /// A fresh file is the hard case, not the easy one: the very first open is
    /// the one that performs the journal_mode transition and creates every table,
    /// so every opener is contending for the exclusive lock at the same instant.
    /// Against an established database the pragma is a no-op and the DDL is
    /// IF NOT EXISTS, and the window mostly closes on its own.
    /// </summary>
    [Fact]
    public void ManySimultaneousOpensOfANewDatabaseAllSucceed()
    {
        var dir = Directory.CreateTempSubdirectory("sms_idstore_open_").FullName;
        try
        {
            var db = Path.Combine(dir, "identity.db");
            const int Openers = 12;
            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var gate = new Barrier(Openers);

            var threads = Enumerable.Range(0, Openers).Select(_ => new Thread(() =>
            {
                try
                {
                    gate.SignalAndWait();   // maximise the overlap
                    using var store = new SqliteIdentityStore(db);
                }
                catch (Exception exc)
                {
                    failures.Add(exc);
                }
            })).ToList();

            foreach (var thread in threads)
                thread.Start();
            foreach (var thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "an opener hung");

            Assert.True(
                failures.IsEmpty,
                "opening the identity store threw while another opener held the file: "
                + string.Join(" | ", failures.Select(e => $"{e.GetType().Name}: {e.Message}")));
            Assert.True(File.Exists(db));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Reopening an established database concurrently must also succeed — this is
    /// the restart-overlapping-shutdown case, where the schema already exists and
    /// only the pragmas re-run.
    /// </summary>
    [Fact]
    public void SimultaneousReopensOfAnExistingDatabaseAllSucceed()
    {
        var dir = Directory.CreateTempSubdirectory("sms_idstore_reopen_").FullName;
        try
        {
            var db = Path.Combine(dir, "identity.db");
            using (var seed = new SqliteIdentityStore(db))
            {
                Assert.True(File.Exists(db));
            }

            const int Openers = 12;
            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var gate = new Barrier(Openers);

            var threads = Enumerable.Range(0, Openers).Select(_ => new Thread(() =>
            {
                try
                {
                    gate.SignalAndWait();
                    using var store = new SqliteIdentityStore(db);
                }
                catch (Exception exc)
                {
                    failures.Add(exc);
                }
            })).ToList();

            foreach (var thread in threads)
                thread.Start();
            foreach (var thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "an opener hung");

            Assert.True(
                failures.IsEmpty,
                "reopening an established identity store threw: "
                + string.Join(" | ", failures.Select(e => $"{e.GetType().Name}: {e.Message}")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
