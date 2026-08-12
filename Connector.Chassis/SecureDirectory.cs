// Infrastructure/SecureDirectory.cs
// ---------------------------------
// Creates the connector's on-disk state directories (logs/, data/, dead-letter)
// with OWNER-ONLY access, so run logs, checkpoints, the identity store and
// dead-letter records — which can carry item ids, principal object ids and (in
// full payload mode) indexed content — are not world-readable on a shared host.
//
//   * POSIX  : the directory is created/tightened to UnixFileMode 0700
//              (rwx------ for the owner, nothing for group/other).
//   * Windows: a best-effort ACL tighten — disable inheritance and grant only
//              the current owner and the local Administrators group. Documented
//              best-effort: locked-down hosts may deny SetAccessControl.
//
// NEVER throws: a directory the connector cannot lock down is logged at WARNING
// and used as-is (the alternative — refusing to run — would be worse than a
// slightly-too-open directory the operator can still fix). Idempotent.

using System.Runtime.InteropServices;

namespace Connector.Chassis;

public static class SecureDirectory
{
    private static readonly IAppLogger Logger = Chassis.GetLogger($"{Chassis.Identity.BaseLoggerName}.fs");

    /// <summary>Owner-only POSIX permission bits (rwx------).</summary>
    internal const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Ensure <paramref name="path"/> exists and is owner-only. Best-effort:
    /// creation failures propagate (the caller needs the directory), but a
    /// failure to TIGHTEN permissions is only logged — never fatal.
    /// </summary>
    public static void EnsureHardened(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Directory.CreateDirectory(path);
            HardenWindows(path);
            return;
        }

        // POSIX: create with the mode directly (so it is never briefly 0777),
        // then tighten in case the directory already existed with looser bits.
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path, OwnerOnly);
            else
                File.SetUnixFileMode(path, OwnerOnly);
        }
        catch (Exception exc) when (exc is not (OutOfMemoryException or StackOverflowException))
        {
            // Fall back to a plain create so the connector can still run; log the
            // reason the lock-down did not take.
            try
            {
                Directory.CreateDirectory(path);
            }
            catch
            {
                // If even the plain create fails the caller's own CreateDirectory
                // will surface it; nothing more to do here.
            }
            Logger.Warning(
                $"Could not set owner-only (0700) permissions on '{path}': {exc.Message} "
                + "— the directory is used as-is; restrict it manually if it holds sensitive state.");
        }
    }

    private static void HardenWindows(string path)
    {
        // Best-effort NTFS lock-down. Kept behind an OS guard and a broad catch:
        // SetAccessControl throws on hosts where the caller lacks the right, and
        // that must never stop the connector from running.
        try
        {
            if (!OperatingSystem.IsWindows())
                return;
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (owner is not null)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    owner,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit
                        | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            var admins = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                admins,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit
                    | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception exc) when (exc is not (OutOfMemoryException or StackOverflowException))
        {
            Logger.Warning(
                $"Could not tighten NTFS permissions on '{path}': {exc.Message} "
                + "— the directory is used as-is; restrict it manually if it holds sensitive state.");
        }
    }
}
