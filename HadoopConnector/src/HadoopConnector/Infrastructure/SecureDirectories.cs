// Infrastructure/SecureDirectories.cs
// -----------------------------------
// Create the connector's LOCAL state directories (logs, state/data, dead-letter)
// with owner-only access at startup so a second copy of business data — the
// dead-letter queue payloads, the identity / inventory SQLite files and the run
// logs — is not world-readable on the host.
//
//   POSIX   — the directory is created (and, if it already exists, tightened) to
//             UnixFileMode 0700 (owner rwx; group/other none). Creating at 0700
//             also closes the brief window a default umask would otherwise leave.
//   Windows — best-effort: NTFS inheritance is broken and access restricted to
//             the current user, Administrators and SYSTEM via the documented
//             `icacls` tool (dependency-free — no System.IO.FileSystem.AccessControl
//             reference on this cross-platform TFM). The enterprise deployment
//             guide still documents setting these NTFS ACLs out of band.
//
// Every call is best-effort and NEVER throws: on a locked-down host where the
// mode cannot be changed the connector logs a warning and continues — refusing
// to start would be a worse failure than a directory that stays at its inherited
// permissions (which the operator is separately guided to restrict).

using System.Diagnostics;
using System.Runtime.Versioning;

namespace HadoopConnector.Infrastructure;

public static class SecureDirectories
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    /// <summary>Owner-only POSIX mode (0700).</summary>
    internal const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Create <paramref name="path"/> owner-only (0700 on POSIX; icacls-restricted
    /// on Windows) and tighten it if it already exists. Idempotent; never throws.
    /// </summary>
    public static void CreateHardened(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
                RestrictWindows(path);
            }
            else
            {
                // Create at 0700 so there is no window at the default umask...
                Directory.CreateDirectory(path, OwnerOnly);
                // ...and tighten an ALREADY-existing directory too: CreateDirectory
                // only applies the mode to directories it actually creates.
                File.SetUnixFileMode(path, OwnerOnly);
            }
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"Could not set owner-only permissions on '{path}' "
                + $"({exc.GetType().Name}: {exc.Message}); continuing. Set restrictive "
                + "filesystem ACLs out of band (docs/DEPLOYMENT_ENTERPRISE.md).");
        }
    }

    /// <summary>Harden the standard local state directories at startup (deduped).</summary>
    public static void HardenStartupDirectories(params string[] paths)
    {
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.Ordinal))
        {
            CreateHardened(path);
        }
    }

    /// <summary>Best-effort NTFS lock-down via icacls (Windows only): break
    /// inheritance and grant only the current user, Administrators and SYSTEM.
    /// Uses well-known SIDs to stay locale-independent. Any failure is swallowed
    /// by the caller's try/catch.</summary>
    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var user = Environment.UserName;
        var arguments =
            $"\"{path}\" /inheritance:r "
            + $"/grant:r \"{user}:(OI)(CI)F\" "
            + "/grant:r \"*S-1-5-32-544:(OI)(CI)F\" "  // BUILTIN\Administrators
            + "/grant:r \"*S-1-5-18:(OI)(CI)F\"";      // NT AUTHORITY\SYSTEM
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
            return;
        if (!process.WaitForExit(10_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
            Logger.Warning(
                $"icacls lock-down of '{path}' timed out; set NTFS ACLs out of band "
                + "(docs/DEPLOYMENT_ENTERPRISE.md).");
        }
    }
}
