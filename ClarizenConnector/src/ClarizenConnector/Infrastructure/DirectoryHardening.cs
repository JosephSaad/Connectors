// Infrastructure/DirectoryHardening.cs
// ------------------------------------
// Restrictive filesystem permissions for the directories that hold source data
// at rest: logs (connector.log), state (sync_state / checkpoints), and the
// dead-letter queue all live under the logs/state roots. On a shared host these
// files can carry ids, error strings and — under DEADLETTER_PAYLOAD_MODE=full —
// verbatim payloads, so the directories are created owner-only.
//
//   POSIX   — chmod 0700 (UnixFileMode owner rwx, no group/other) via
//             File.SetUnixFileMode.
//   Windows — best-effort tighten to owner + Administrators + SYSTEM with
//             inheritance disabled, via icacls (present on every supported
//             Windows Server). No extra package dependency.
//
// This is defense-in-depth: it NEVER throws. A platform/permission failure is
// logged as a warning and the connector continues (the env files remain the
// documented trust root — docs/THREAT_MODEL.md).

using System.Runtime.Versioning;

namespace ClarizenConnector.Infrastructure;

public static class DirectoryHardening
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.security");

    /// <summary>Owner-only POSIX mode (rwx------ = 0700).</summary>
    internal const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Create <paramref name="path"/> if needed and restrict it to the owner
    /// (POSIX 0700; Windows owner+admins best-effort). Never throws — a failure
    /// is logged and ignored so hardening can only ever be additive.
    /// </summary>
    public static void EnsureOwnerOnly(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"Could not create directory '{path}' ({exc.GetType().Name}: {exc.Message}); "
                + "skipping permission hardening.");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            TightenWindowsAcl(path);
            return;
        }

        try
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
        catch (Exception exc)
        {
            // A filesystem that does not carry POSIX modes (some network mounts)
            // or a platform that rejects the call: warn and continue.
            Logger.Warning(
                $"Could not set owner-only (0700) permissions on '{path}' "
                + $"({exc.GetType().Name}: {exc.Message}); continuing.");
        }
    }

    /// <summary>Harden the log/state/dead-letter roots at startup. Both roots may
    /// resolve to the same directory (the default single-node layout) — idempotent.</summary>
    public static void HardenStateDirectories()
    {
        EnsureOwnerOnly(Logging.LogsRoot);
        EnsureOwnerOnly(Config.SyncState.LogsDir);
    }

    /// <summary>
    /// Windows best-effort: disable inheritance and grant Full Control to only
    /// SYSTEM, the local Administrators group, and the running account. Uses
    /// icacls so no Windows-only ACL package is pulled into the cross-platform
    /// build. Failures are swallowed (best-effort, documented).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void TightenWindowsAcl(string path)
    {
        try
        {
            var account = Environment.UserName;
            // /inheritance:r removes inherited ACEs; /grant:r replaces the grant.
            var arguments =
                $"\"{path}\" /inheritance:r "
                + "/grant:r \"*S-1-5-18:(OI)(CI)F\" "        // SYSTEM
                + "/grant:r \"*S-1-5-32-544:(OI)(CI)F\" "     // BUILTIN\Administrators
                + (string.IsNullOrWhiteSpace(account)
                    ? string.Empty
                    : $"/grant:r \"{account}:(OI)(CI)F\"");

            var psi = new System.Diagnostics.ProcessStartInfo("icacls", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return;
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                try { process.Kill(); } catch { /* best effort */ }
                Logger.Warning($"icacls hardening of '{path}' timed out; continuing.");
                return;
            }
            if (process.ExitCode != 0)
            {
                Logger.Warning(
                    $"icacls hardening of '{path}' exited {process.ExitCode}; "
                    + "inherited permissions may be broader than owner-only.");
            }
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"Could not tighten Windows ACL on '{path}' "
                + $"({exc.GetType().Name}: {exc.Message}); continuing.");
        }
    }
}
