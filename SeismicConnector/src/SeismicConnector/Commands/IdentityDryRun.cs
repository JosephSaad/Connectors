// Commands/IdentityDryRun.cs — preview (or --save) the Seismic → Entra mapping.

using System.Diagnostics;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Commands;

public static class IdentityDryRun
{
    public static async Task<object?> CmdIdentityDryRun(ParsedArgs args)
    {
        var save = args.GetFlag("save");
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("identity-dry-run", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var runtime = Runtime.Create();
            var identity = new IdentitySync(runtime.Seismic, runtime.Graph, runtime.Store);

            progress.Info(save
                ? "Identity crawl (mappings WILL be persisted, --save)"
                : "Identity DRY RUN (nothing persisted — add --save to persist)");
            progress.Info($"{"TYPE",-6} {"SEISMIC ID",-38} {"EMAIL / NAME",-36} ENTRA OBJECT ID");
            progress.Info(new string('-', 110));

            var stats = await identity.RunAsync(
                persist: save,
                onMapping: mapping => progress.Info(
                    $"{mapping.PrincipalType,-6} {mapping.SeismicId,-38} "
                    + $"{(mapping.Email ?? mapping.DisplayName ?? ""),-36} "
                    + (mapping.EntraObjectId ?? "(unmapped)")),
                ct: ServiceStop.Token);

            progress.Info(new string('-', 110));
            progress.Info(
                $"Users: {stats.UsersMapped}/{stats.UsersScanned} mapped; "
                + $"Groups: {stats.GroupsMapped}/{stats.GroupsScanned} mapped."
                + (save ? " Mappings saved to the identity store." : ""));

            CommandRegistry.WriteSummary(
                summaryFile, logFile, new IngestionStats(), null,
                runtime.Config.Connector.Id, stopwatch.Elapsed.TotalSeconds, "identity-dry-run");
            return true;
        }
        catch (Exception ex)
        {
            progress.Error($"identity-dry-run failed: {ex.Message}");
            return false;
        }
    }
}
