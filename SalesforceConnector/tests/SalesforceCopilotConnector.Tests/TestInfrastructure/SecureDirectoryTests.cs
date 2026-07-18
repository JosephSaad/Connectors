// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// SecureDirectoryTests.cs
// -----------------------
// #3 — the logs / state / dead-letter directories must be created owner-only.
// On POSIX this is UnixFileMode 0700; the Windows tightening is best-effort and
// is not asserted here (the runner is POSIX). EnsureOwnerOnly must also be a
// no-op-safe idempotent call and must never throw when it cannot set perms.

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

public sealed class SecureDirectoryTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("secure_dir_").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void CreatesDirectory0700OnPosix()
    {
        if (OperatingSystem.IsWindows())
            return;  // POSIX-only assertion; Windows path is best-effort ACL tightening.

        var target = Path.Combine(_tmp, "logs");
        Assert.False(Directory.Exists(target));

        SecureDirectory.EnsureOwnerOnly(target);

        Assert.True(Directory.Exists(target));
        var mode = File.GetUnixFileMode(target);
        Assert.Equal(SecureDirectory.OwnerOnly, mode);
        // Explicitly: no group/other bits at all.
        Assert.Equal((UnixFileMode)0, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute));
        Assert.Equal((UnixFileMode)0, mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
    }

    [Fact]
    public void EnsureOwnerOnlyIsIdempotent()
    {
        var target = Path.Combine(_tmp, "state");
        var first = SecureDirectory.EnsureOwnerOnly(target);
        var second = SecureDirectory.EnsureOwnerOnly(target);  // must not throw on existing dir
        Assert.Equal(first.FullName, second.FullName);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void HardenExistingRestampsAnExistingDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        // A directory left over from an earlier un-hardened run (0755 here).
        var target = Path.Combine(_tmp, "deadletter");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(
            target,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        SecureDirectory.HardenExisting(target);

        Assert.Equal(SecureDirectory.OwnerOnly, File.GetUnixFileMode(target));
    }

    [Fact]
    public void ConcurrentEnsureOwnerOnlyOnSamePathNeverThrowsAndTightens()
    {
        // Startup can hit the same state dir from many threads at once (parallel
        // object/shard workers each opening inventory/identity stores, the
        // dead-letter path, the decision ledger). EnsureOwnerOnly must be safe to
        // call concurrently on one path: no throw, and the result is still owner-only.
        var target = Path.Combine(_tmp, "concurrent");
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 64 }, _ =>
        {
            try
            {
                SecureDirectory.EnsureOwnerOnly(target);
            }
            catch (Exception ex)
            {
                errors.Enqueue($"{ex.GetType().Name}: {ex.Message}");
            }
        });

        Assert.True(errors.IsEmpty, "EnsureOwnerOnly threw under concurrency: " + string.Join(" | ", errors.Take(3)));
        Assert.True(Directory.Exists(target));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(SecureDirectory.OwnerOnly, File.GetUnixFileMode(target));
    }

    [Fact]
    public void EnsureOwnerOnlyOnAnExistingFilePathDoesNotCorruptState()
    {
        // "Never throws for a permission-set failure" is scoped: a genuine
        // cannot-create-the-directory error still propagates from CreateDirectory
        // (documented). Pointing it at an existing FILE is exactly that class of
        // failure — it must surface as an IOException, not be silently swallowed
        // (which would let a caller believe it created an owner-only dir).
        var filePath = Path.Combine(_tmp, "not-a-dir.txt");
        File.WriteAllText(filePath, "x");

        Assert.ThrowsAny<IOException>(() => SecureDirectory.EnsureOwnerOnly(filePath));
    }
}
