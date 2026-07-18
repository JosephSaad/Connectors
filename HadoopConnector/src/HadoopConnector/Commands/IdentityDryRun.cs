// Commands/IdentityDryRun.cs
// --------------------------
// `identity-dry-run [--save]` — run the BDH → Entra identity resolution
// and print mapping statistics without ingesting content. Mappings are only
// persisted with --save.

using HadoopConnector.Infrastructure;

namespace HadoopConnector.Commands;

public static class IdentityDryRun
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var save = args.HasFlag("--save");
        using var context = Runtime.Create(args, "identity_dry_run");
        Dashboard.Banner("Identity dry run" + (save ? " (saving)" : string.Empty));

        try
        {
            var directory = await context.GetUserDirectoryAsync(ServiceStop.Token);
            var result = await context.IdentitySync.SyncAsync(
                directory, context.IdentityStore, persist: save, ServiceStop.Token);

            Dashboard.Line($"Users:               {result.UsersTotal}");
            Dashboard.Line($"Resolved to Entra:   {result.UsersResolved}");
            Dashboard.Line($"Unresolved:          {result.UnresolvedEmails.Count}");
            Dashboard.Line($"Without email:       {result.UsersWithoutEmail}");
            if (result.UnresolvedEmails.Count > 0)
            {
                Dashboard.Line("Unresolved emails (no Entra match by mail/UPN):");
                foreach (var email in result.UnresolvedEmails.Take(25))
                    Dashboard.Line($"  - {email}");
                if (result.UnresolvedEmails.Count > 25)
                    Dashboard.Line($"  ... and {result.UnresolvedEmails.Count - 25} more");
            }
            if (save)
                Dashboard.Line($"Mappings persisted to the identity store ({context.IdentityStore.Count()} rows).");
            else
                Dashboard.Line("Dry run only — nothing persisted (use --save to write the identity store).");
            return true;
        }
        catch (Exception exc)
        {
            Logger.Error("identity-dry-run failed", exc);
            return false;
        }
    }
}
