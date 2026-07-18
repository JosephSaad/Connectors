// Graph/DriftSweep.cs
// -------------------
// Full-inventory deletion/drift reconciliation (the `reconcile` command).
//
// Event-driven withdrawal (webhooks, incremental crawls, the full-crawl
// not-seen reaper) can miss edge cases: events dropped while the connector
// was down, state restored from backup, exclusion rules edited between
// crawls, or manual index surgery. This sweep diffs the ENTIRE Seismic
// inventory against the tracked-item store and reports — or, with --repair,
// fixes — every divergence:
//
//   orphaned-in-index   tracked as ingested but gone from Seismic → withdraw
//   excluded-drift      tracked as ingested but now No-MNE-excluded → withdraw
//   expired-drift       tracked as ingested but past its expiry → withdraw
//   version-drift       indexed version != current published version → re-ingest
//   missing-from-index  eligible in Seismic but absent from the index → ingest
//
// Every finding is one JSONL line in drift_report_*.jsonl (plus a trailing
// summary object), complementing the No-MNE reconciliation report. The last
// sweep's finding count is exported as a gauge on /metrics.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SeismicConnector.Config;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Graph;

public sealed record DriftFinding(string Kind, string ItemId, string? TeamsiteId, string Detail, bool Repaired);

public sealed class DriftSummary
{
    public List<DriftFinding> Findings { get; } = new();

    public int Total => Findings.Count;

    public int RepairedCount => Findings.Count(f => f.Repaired);

    public int UnrepairedCount => Total - RepairedCount;

    public IReadOnlyDictionary<string, int> CountsByKind =>
        Findings.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>True when the index diverges from the source and was not fully repaired.</summary>
    public bool HasUnrepairedDrift => UnrepairedCount > 0;
}

public sealed class DriftSweep
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.drift");
    private static readonly IAppLogger Progress = Logging.GetLogger("progress");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly AppConfig _config;
    private readonly SeismicClient _seismic;
    private readonly IIdentityStore _store;
    private readonly ExclusionFilter _filter;
    private readonly IngestPipeline _pipeline;

    public DriftSweep(
        AppConfig config,
        SeismicClient seismic,
        IIdentityStore store,
        IngestPipeline pipeline,
        ExclusionFilter? filter = null)
    {
        _config = config;
        _seismic = seismic;
        _store = store;
        _pipeline = pipeline;
        _filter = filter ?? new ExclusionFilter(config.Exclusions);
    }

    /// <summary>
    /// Run the sweep. <paramref name="repair"/> false → report only;
    /// true → withdraw/re-ingest to converge the index on the source.
    /// <paramref name="reportPath"/> null → no file (counts only).
    /// </summary>
    public async Task<DriftSummary> RunAsync(
        bool repair, string? reportPath = null, CancellationToken ct = default)
    {
        using var scope = Tracing.BeginNamedScope("reconcile.sweep", _config.Connector.Id, "reconcile");
        scope.SetTag("reconcile.repair", repair);
        var summary = new DriftSummary();

        // ── 1. Full source inventory ─────────────────────────────────────────
        Progress.Info($"Drift sweep: listing the full Seismic inventory... [correlation {scope.CorrelationId}]");
        var teamsites = await _seismic.GetTeamsitesAsync(ct).ConfigureAwait(false);
        var teamsitesById = teamsites.ToDictionary(t => t.Id, StringComparer.Ordinal);

        // Everything visible in the source (any status), and the subset that
        // is currently ELIGIBLE for the index (published + not excluded + not
        // expired). Items in wholly-excluded teamsites are never listed —
        // their tracked rows are handled by the excluded-drift rule below.
        var seenInSource = new Dictionary<string, SeismicContent>(StringComparer.Ordinal);
        var eligible = new Dictionary<string, SeismicContent>(StringComparer.Ordinal);
        var excludedNow = new HashSet<string>(StringComparer.Ordinal);
        var excludedTeamsiteIds = new HashSet<string>(StringComparer.Ordinal);
        var nowUtc = DateTime.UtcNow;

        foreach (var teamsite in teamsites)
        {
            ct.ThrowIfCancellationRequested();
            if (_filter.IsTeamsiteExcluded(teamsite))
            {
                excludedTeamsiteIds.Add(teamsite.Id);
                continue;
            }
            foreach (var content in await _seismic.GetContentsAsync(teamsite.Id, null, ct).ConfigureAwait(false))
            {
                seenInSource[content.Id] = content;
                if (_filter.Evaluate(content, teamsite).Excluded)
                {
                    excludedNow.Add(content.Id);
                    continue;
                }
                if (!content.Status.Equals("published", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(content.CurrentVersionId))
                {
                    continue;
                }
                if (content.ExpiresAt is not null && content.ExpiresAt.Value.ToUniversalTime() <= nowUtc)
                    continue;
                eligible[content.Id] = content;
            }
        }

        // ── 2. Diff against the tracked-item store ──────────────────────────
        var tracked = _store.GetAllTrackedItems();
        var trackedById = tracked.ToDictionary(t => t.ItemId, StringComparer.Ordinal);

        foreach (var item in tracked.Where(t => t.Status == "ingested"))
        {
            ct.ThrowIfCancellationRequested();
            if (excludedTeamsiteIds.Contains(item.TeamsiteId))
            {
                await RecordAsync(summary, repair, "excluded-drift", item.ItemId, item.TeamsiteId,
                    "teamsite is now excluded by the No-MNE rules",
                    () => WithdrawAsync(item.ItemId, markExcluded: true, ct)).ConfigureAwait(false);
            }
            else if (excludedNow.Contains(item.ItemId))
            {
                await RecordAsync(summary, repair, "excluded-drift", item.ItemId, item.TeamsiteId,
                    "content is now excluded by the No-MNE rules",
                    () => WithdrawAsync(item.ItemId, markExcluded: true, ct)).ConfigureAwait(false);
            }
            else if (!seenInSource.ContainsKey(item.ItemId))
            {
                await RecordAsync(summary, repair, "orphaned-in-index", item.ItemId, item.TeamsiteId,
                    "content no longer exists in Seismic",
                    () => WithdrawAsync(item.ItemId, markExcluded: false, ct)).ConfigureAwait(false);
            }
            else if (!eligible.ContainsKey(item.ItemId))
            {
                var content = seenInSource[item.ItemId];
                var reason = content.ExpiresAt is not null
                    && content.ExpiresAt.Value.ToUniversalTime() <= nowUtc
                        ? "expired-drift"
                        : "orphaned-in-index";
                var detail = reason == "expired-drift"
                    ? $"content expired at {content.ExpiresAt:o}"
                    : $"content status is '{content.Status}' (not published)";
                await RecordAsync(summary, repair, reason, item.ItemId, item.TeamsiteId, detail,
                    () => WithdrawAsync(item.ItemId, markExcluded: false, ct)).ConfigureAwait(false);
            }
            else if (eligible[item.ItemId].CurrentVersionId != item.VersionId)
            {
                var content = eligible[item.ItemId];
                await RecordAsync(summary, repair, "version-drift", item.ItemId, item.TeamsiteId,
                    $"index has version '{item.VersionId}', current published is '{content.CurrentVersionId}'",
                    () => _pipeline.IngestSingleAsync(content.Id, content.TeamsiteId, ct)).ConfigureAwait(false);
            }
        }

        foreach (var (contentId, content) in eligible)
        {
            ct.ThrowIfCancellationRequested();
            trackedById.TryGetValue(contentId, out var row);
            if (row is { Status: "ingested" })
                continue;
            var detail = row is { Status: "excluded" }
                ? "previously excluded but no longer matches any exclusion rule"
                : "eligible in Seismic but absent from the index";
            await RecordAsync(summary, repair, "missing-from-index", contentId, content.TeamsiteId, detail,
                () => _pipeline.IngestSingleAsync(contentId, content.TeamsiteId, ct)).ConfigureAwait(false);
        }

        // ── 3. Report ────────────────────────────────────────────────────────
        if (reportPath is not null)
            WriteReport(reportPath, summary, repair);
        Metrics.SetLastDriftFindings(summary.Total);

        Progress.Info(summary.Total == 0
            ? "Drift sweep: index and source are in sync — no drift."
            : $"Drift sweep: {summary.Total} finding(s) "
              + $"[{string.Join(", ", summary.CountsByKind.Select(kv => $"{kv.Key}={kv.Value}"))}]"
              + (repair ? $", {summary.RepairedCount} repaired" : " (report-only; rerun with --repair)"));
        return summary;
    }

    private async Task<bool> WithdrawAsync(string itemId, bool markExcluded, CancellationToken ct)
    {
        await _pipeline.WithdrawItemAsync(itemId, "drift-sweep", "reconcile sweep repair", ct)
            .ConfigureAwait(false);
        var tracked = _store.GetTrackedItem(itemId);
        if (markExcluded && tracked is not null)
            _store.UpsertTrackedItem(tracked with { Status = "excluded", LastSeenUtc = DateTime.UtcNow });
        else
            _store.RemoveTrackedItem(itemId);
        return true;
    }

    private static async Task RecordAsync(
        DriftSummary summary,
        bool repair,
        string kind,
        string itemId,
        string? teamsiteId,
        string detail,
        Func<Task<bool>> repairAction)
    {
        var repaired = false;
        if (repair)
        {
            try
            {
                repaired = await repairAction().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Repair boundary: one item's failed repair must not abort the
                // sweep. The finding is recorded as UNREPAIRED (exit 1 /
                // index_drift alert), so the miss is never silent. Full
                // exception attached — this broad catch also absorbs unexpected
                // types whose stack matters.
                Logger.Error($"Drift repair failed for {itemId} ({kind}) — finding left unrepaired.", ex);
            }
        }
        summary.Findings.Add(new DriftFinding(kind, itemId, teamsiteId, detail, repaired));
        Logger.Info($"DRIFT {kind} {itemId}: {detail}{(repaired ? " [repaired]" : "")}");
    }

    private static void WriteReport(string path, DriftSummary summary, bool repair)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        var correlationId = Tracing.CurrentCorrelationId;
        foreach (var finding in summary.Findings)
        {
            var entry = new Dictionary<string, object?>
            {
                ["kind"] = finding.Kind,
                ["item_id"] = finding.ItemId,
                ["teamsite_id"] = finding.TeamsiteId,
                ["detail"] = finding.Detail,
                ["repaired"] = finding.Repaired,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            if (correlationId is not null)
                entry["correlation_id"] = correlationId;
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
        }
        writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = "summary",
            ["total"] = summary.Total,
            ["repaired"] = summary.RepairedCount,
            ["unrepaired"] = summary.UnrepairedCount,
            ["repair_mode"] = repair,
            ["by_kind"] = summary.CountsByKind,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        }, JsonOptions));
    }
}
