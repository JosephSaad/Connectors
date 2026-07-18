// Restrictive filesystem permissions at startup (#3, Infrastructure/
// SecureDirectories.cs): the local state directories (logs / state / dead-letter)
// are created owner-only — POSIX 0700 — and an already-existing looser directory
// is tightened. The call is best-effort and never throws.

using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class SecureDirectoriesTests
{
    private const UnixFileMode Loose0755 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [Fact]
    public void CreateHardened_NewDirectory_IsOwnerOnly_OnPosix()
    {
        if (OperatingSystem.IsWindows())
            return;  // POSIX-mode assertion only; Windows uses a best-effort icacls path.

        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "state");

        SecureDirectories.CreateHardened(target);

        Assert.True(Directory.Exists(target));
        Assert.Equal(SecureDirectories.OwnerOnly, File.GetUnixFileMode(target));
    }

    [Fact]
    public void CreateHardened_TightensExistingLooseDirectory_OnPosix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "logs");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, Loose0755);

        SecureDirectories.CreateHardened(target);

        Assert.Equal(SecureDirectories.OwnerOnly, File.GetUnixFileMode(target));
    }

    [Fact]
    public void HardenStartupDirectories_IsBestEffort_NeverThrows()
    {
        // Empty/blank paths are skipped; a valid one is hardened. Must never throw.
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "data");

        var ex = Record.Exception(() =>
            SecureDirectories.HardenStartupDirectories(string.Empty, "   ", target, target));

        Assert.Null(ex);
        Assert.True(Directory.Exists(target));
    }
}
