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
}
