// Infrastructure/DirectoryHardening.cs
// ------------------------------------
// Owner-only hardening for the connector's on-disk directories (logs / state /
// dead-letter). These hold the most sensitive artifacts OUTSIDE the seat-gated
// index — run logs, the dead-letter queue, the identity DB, the erasure and
// decision ledgers, the audit trail — so at startup they are created with the
// tightest permissions the platform can express:
//
//   * POSIX   : chmod 0700 (owner rwx, no group/other) via UnixFileMode.
//   * Windows : best-effort — break ACL inheritance and grant Full Control to
//               the OWNER RIGHTS SID (S-1-3-4) and the local Administrators
//               group (S-1-5-32-544) only, via `icacls` (documented in
//               SECURITY.md). No Windows-only assemblies are referenced, so the
//               connector still builds/runs cross-platform.
//
// BEST-EFFORT BY DESIGN: a filesystem that cannot express the mode (network
// share, ACL-only volume, locked-down host) logs a WARNING and continues —
// hardening must never take the process down.

using System.Runtime.Versioning;

namespace AltrataConnector.Infrastructure;

public static class DirectoryHardening
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.security");

    /// <summary>POSIX owner-only: rwx------ (0700).</summary>
    public const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Create <paramref name="path"/> if absent and restrict it to the owner.
    /// Never throws — a platform/filesystem that cannot apply the mode logs a
    /// warning and the caller proceeds.
    /// </summary>
    public static void EnsureSecureDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exc)
        {
            Logger.Warning($"Could not create directory '{path}' " +
                           $"({exc.GetType().Name}: {exc.Message}) — continuing.");
            return;
        }

        if (OperatingSystem.IsWindows())
            TightenWindows(path);
        else
            TightenPosix(path);
    }

    /// <summary>Harden the connector's startup directories (logs + state). Called
    /// once per command run; idempotent and best-effort.</summary>
    public static void HardenStartupDirectories()
    {
        EnsureSecureDirectory(Logging.LogsRoot);
        EnsureSecureDirectory(StateDir);
    }

    /// <summary>Effective state directory (DATA_DIR override, else ./data) —
    /// matches FileStateStore's resolution.</summary>
    public static string StateDir =>
        Environment.GetEnvironmentVariable("DATA_DIR")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

    [UnsupportedOSPlatform("windows")]
    private static void TightenPosix(string path)
    {
        try
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
        catch (Exception exc)
        {
            Logger.Warning($"Could not set owner-only (0700) permissions on '{path}' " +
                           $"({exc.GetType().Name}: {exc.Message}) — continuing; secure the directory out of band.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TightenWindows(string path)
    {
        try
        {
            // Best-effort: break inherited ACEs (/inheritance:r) and grant Full
            // Control to the OWNER RIGHTS SID and local Administrators only.
            // Using icacls avoids a hard dependency on the Windows-only
            // System.Security.AccessControl assembly (keeps the cross-platform
            // build clean); failures are non-fatal (locked-down hosts, ACL-less
            // volumes).
            var psi = new System.Diagnostics.ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/inheritance:r");
            psi.ArgumentList.Add("/grant:r");
            psi.ArgumentList.Add("*S-1-3-4:(OI)(CI)F");     // OWNER RIGHTS
            psi.ArgumentList.Add("*S-1-5-32-544:(OI)(CI)F"); // BUILTIN\Administrators
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null && !proc.WaitForExit(5000))
                Logger.Warning($"icacls hardening of '{path}' timed out — continuing.");
        }
        catch (Exception exc)
        {
            Logger.Warning($"Could not tighten Windows ACLs on '{path}' " +
                           $"({exc.GetType().Name}: {exc.Message}) — continuing; secure the directory out of band.");
        }
    }
}
