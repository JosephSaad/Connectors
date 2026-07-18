// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/SecureDirectory.cs
// ---------------------------------
// Owner-only directory creation for the connector's at-rest state (#3).
//
// The logs / state / dead-letter directories hold sensitive material — run logs
// that can reference CRM ids, the sync-state / checkpoint files, and the
// dead-letter queue (which, in full mode, contains raw request/response
// payloads). On a shared host these must not be world- or group-readable.
//
//   * POSIX  — the directory is created (or, if it already exists, re-stamped on
//              first touch) with UnixFileMode 0700 (owner rwx, no group/other).
//   * Windows — a best-effort call disables ACL inheritance and grants only the
//              current owner + BUILTIN\Administrators. This is documented as
//              best-effort: some environments (roaming profiles, redirected
//              folders, restricted service accounts) reject the change, so it
//              NEVER throws — it logs a warning and leaves the inherited ACL.
//
// EnsureOwnerOnly NEVER throws for a permission-set failure: hardening at-rest
// storage must not take down a crawl. A genuine "cannot create the directory at
// all" IOException still propagates from Directory.CreateDirectory, exactly as
// the previous plain CreateDirectory calls did.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>Creates connector state directories with owner-only access.</summary>
public static class SecureDirectory
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>Owner rwx only — POSIX mode 0700.</summary>
    internal const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Create <paramref name="path"/> (idempotent) and, when it is newly created,
    /// tighten it to owner-only access. Re-stamping an already-existing directory
    /// is skipped so the hot dead-letter / checkpoint append path stays a plain
    /// idempotent CreateDirectory — the permissions were set when the directory
    /// was first created at startup. Returns the created/existing directory info.
    /// </summary>
    public static DirectoryInfo EnsureOwnerOnly(string path)
    {
        var existedBefore = Directory.Exists(path);
        var info = Directory.CreateDirectory(path);
        if (!existedBefore)
            TryRestrict(path);
        return info;
    }

    /// <summary>
    /// Force owner-only access on <paramref name="path"/> even if it already
    /// exists (creating it first if needed). Used at process startup so a logs
    /// directory left over from an earlier, un-hardened run is tightened too.
    /// </summary>
    public static DirectoryInfo HardenExisting(string path)
    {
        var info = Directory.CreateDirectory(path);
        TryRestrict(path);
        return info;
    }

    /// <summary>Best-effort permission tightening; never throws.</summary>
    private static void TryRestrict(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                TightenWindows(path);
            else
                File.SetUnixFileMode(path, OwnerOnly);
        }
        catch (Exception ex)
        {
            // Never fail a crawl because we could not lock down a directory —
            // log a warning so an operator can tighten it out of band.
            Logger.Warning(
                $"Could not set owner-only permissions on '{path}': {ex.GetType().Name}: {ex.Message}. " +
                "Directory is usable; review its ACL manually if it holds sensitive state.");
        }
    }

    /// <summary>
    /// Windows best-effort: remove inherited ACEs and grant only the current user
    /// and the local Administrators group. Uses icacls so the connector needs no
    /// Windows-only ACL NuGet package on its cross-platform build. Any failure is
    /// swallowed by the caller (logged as a warning) — this is defense in depth on
    /// top of the parent directory's inherited ACL, not a hard requirement.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void TightenWindows(string path)
    {
        var user = Environment.UserName;
        var psi = new ProcessStartInfo("icacls")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // /inheritance:r removes inherited ACEs; /grant:r replaces (not adds) the
        // named grants: current user + Administrators, full control, applied to
        // the folder and inherited by children ((OI)(CI)).
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("/inheritance:r");
        psi.ArgumentList.Add("/grant:r");
        psi.ArgumentList.Add($"{user}:(OI)(CI)F");
        psi.ArgumentList.Add("/grant:r");
        psi.ArgumentList.Add("*S-1-5-32-544:(OI)(CI)F");   // BUILTIN\Administrators (well-known SID)
        using var proc = Process.Start(psi);
        if (proc is null)
            return;
        proc.WaitForExit(10_000);
        if (!proc.HasExited || proc.ExitCode != 0)
        {
            Logger.Warning(
                $"icacls tightening of '{path}' did not complete cleanly (exit " +
                $"{(proc.HasExited ? proc.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "timeout")}); " +
                "the directory falls back to its inherited ACL.");
        }
    }
}
