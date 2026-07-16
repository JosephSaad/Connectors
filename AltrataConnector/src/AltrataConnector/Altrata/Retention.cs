// Altrata/Retention.cs
// --------------------
// Feed-file retention. After a delivery has been SUCCESSFULLY processed
// (reconciled + marked in the ledger) and RETENTION_DAYS have elapsed, the
// delivery directory is archived (moved to FEED_PATH/archive/) or deleted,
// per RETENTION_MODE. Connector state (checkpoints, ledger, identity DB,
// audit log) is never touched — retention only ever acts on feed files.

using AltrataConnector.Config;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Altrata;

public static class Retention
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.retention");

    /// <summary>
    /// Apply retention to processed deliveries. Returns the delivery ids acted on.
    /// </summary>
    public static IReadOnlyList<string> Apply(AppConfig config, IStateStore state, DateTime? utcNow = null)
    {
        if (config.RetentionDays <= 0 || string.IsNullOrEmpty(config.FeedPath))
            return Array.Empty<string>();

        var now = utcNow ?? DateTime.UtcNow;
        var cutoff = now.AddDays(-config.RetentionDays);
        var acted = new List<string>();

        foreach (var delivery in FeedReader.DiscoverDeliveries(config.FeedPath))
        {
            if (!state.IsDeliveryProcessed(delivery.Id))
                continue;  // never touch an unprocessed delivery

            DateTime processedUtc;
            try
            {
                processedUtc = Directory.GetLastWriteTimeUtc(delivery.Directory);
                // Ledger timestamp preferred when the directory mtime is newer than processing.
                var ledgerRaw = state.GetValue($"delivery_processed_{delivery.Id}");
                if (ledgerRaw != null && DateTime.TryParse(ledgerRaw, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var ledgerUtc))
                    processedUtc = ledgerUtc;
            }
            catch
            {
                continue;
            }

            if (processedUtc >= cutoff)
                continue;

            try
            {
                if (config.RetentionMode == "delete")
                {
                    Directory.Delete(delivery.Directory, recursive: true);
                    Logger.Info($"Retention: deleted processed delivery '{delivery.Id}' " +
                                $"(processed {processedUtc:u}, retention {config.RetentionDays}d)");
                }
                else
                {
                    var archiveRoot = Path.Combine(config.FeedPath, FeedReader.ArchiveDirectoryName);
                    Directory.CreateDirectory(archiveRoot);
                    var target = Path.Combine(archiveRoot, Path.GetFileName(delivery.Directory));
                    if (Directory.Exists(target))
                        target += "_" + now.ToString("yyyyMMddHHmmss");
                    Directory.Move(delivery.Directory, target);
                    Logger.Info($"Retention: archived processed delivery '{delivery.Id}' → {target}");
                }
                acted.Add(delivery.Id);
            }
            catch (Exception exc)
            {
                Logger.Warning($"Retention: could not {config.RetentionMode} delivery '{delivery.Id}': {exc.Message}");
            }
        }
        return acted;
    }
}
