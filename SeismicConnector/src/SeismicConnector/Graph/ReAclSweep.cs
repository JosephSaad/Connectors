// Graph/ReAclSweep.cs
// -------------------
// On-demand permission-change re-ACL audit/repair (the `reacl` command).
//
// The automatic per-crawl path (PERMISSION_REACL=true) only re-checks items a
// crawl actually visits. This sweep is the on-demand backstop: it walks the
// full inventory, resolves current permissions for every ingested item, and
// detects those whose effective ACL drifted from what's indexed — even when
// no crawl is running and regardless of the PERMISSION_REACL flag.
//
// --dry-run reports counts only (nothing is PATCHed). Without it, drifted
// items are re-ACLed via an ACL-only Graph PATCH (content is never re-sent).
// Compliance-safe: an item whose permissions can't be resolved is left
// exactly as indexed — a re-ACL never widens access on a resolution failure.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SeismicConnector.Config;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Graph;

public sealed record ReAclFinding(string Kind, string ItemId, string? TeamsiteId, string Detail);

public sealed class ReAclSummary
{
    public List<ReAclFinding> Findings { get; } = new();

    /// <summary>Items whose known ACL fingerprint changed (genuine drift).</summary>
    public int DriftCount => Findings.Count(f => f.Kind == "acl-drift");

    /// <summary>Items whose baseline fingerprint was (re)established.</summary>
    public int BaselineCount => Findings.Count(f => f.Kind == "acl-baseline");

    /// <summary>Items whose permissions could not be resolved (left unchanged).</summary>
    public int UnresolvedCount => Findings.Count(f => f.Kind == "acl-unresolved");

    /// <summary>Items actually re-ACLed (drift + baseline, in repair mode).</summary>
    public int ReAcledCount { get; internal set; }

    public int Total => Findings.Count;

    public IReadOnlyDictionary<string, int> CountsByKind =>
        Findings.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
}

public sealed class ReAclSweep
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.reacl");
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

    public ReAclSweep(
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
    /// Run the sweep. <paramref name="dryRun"/> true → audit only (nothing
    /// PATCHed). <paramref name="reportPath"/> null → no file (counts only).
    /// </summary>
    public async Task<ReAclSummary> RunAsync(
        bool dryRun, string? reportPath = null, CancellationToken ct = default)
    {
        using var scope = Tracing.BeginNamedScope("reacl.sweep", _config.Connector.Id, "reacl");
        scope.SetTag("reacl.dry_run", dryRun);
        var summary = new ReAclSummary();
        var nowUtc = DateTime.UtcNow;

        Progress.Info((dryRun
            ? "Re-ACL audit (dry run — no changes will be made)..."
            : "Re-ACL sweep — refreshing ACLs where effective permissions drifted...")
            + $" [correlation {scope.CorrelationId}]");

        // Index the full source inventory by content id (current permissions
        // live on the content; a teamsite/profile change surfaces here).
        var teamsites = await _seismic.GetTeamsitesAsync(ct).ConfigureAwait(false);
        var contentById = new Dictionary<string, SeismicContent>(StringComparer.Ordinal);
        foreach (var teamsite in teamsites)
        {
            ct.ThrowIfCancellationRequested();
            if (_filter.IsTeamsiteExcluded(teamsite))
                continue;
            foreach (var content in await _seismic.GetContentsAsync(teamsite.Id, null, ct).ConfigureAwait(false))
                contentById[content.Id] = content;
        }

        foreach (var tracked in _store.GetAllTrackedItems().Where(t => t.Status == "ingested"))
        {
            ct.ThrowIfCancellationRequested();
            if (!contentById.TryGetValue(tracked.ItemId, out var content))
                continue;  // gone/moved — the reconcile drift sweep owns that case

            var outcome = await _pipeline
                .ReAclIfDriftedAsync(content, tracked, nowUtc, repair: !dryRun, ct)
                .ConfigureAwait(false);

            switch (outcome)
            {
                case ReAclOutcome.Drifted:
                    summary.Findings.Add(new ReAclFinding(
                        "acl-drift", tracked.ItemId, content.TeamsiteId,
                        "effective permissions changed since last index"));
                    if (!dryRun)
                        summary.ReAcledCount++;
                    break;
                case ReAclOutcome.Baseline:
                    summary.Findings.Add(new ReAclFinding(
                        "acl-baseline", tracked.ItemId, content.TeamsiteId,
                        "no stored ACL fingerprint — baseline established"));
                    if (!dryRun)
                        summary.ReAcledCount++;
                    break;
                case ReAclOutcome.Unresolved:
                    summary.Findings.Add(new ReAclFinding(
                        "acl-unresolved", tracked.ItemId, content.TeamsiteId,
                        "permissions could not be resolved — ACL left unchanged (no widening)"));
                    break;
                case ReAclOutcome.NoChange:
                default:
                    break;
            }
        }

        if (reportPath is not null)
            WriteReport(reportPath, summary, dryRun);

        Progress.Info(summary.Total == 0
            ? "Re-ACL sweep: all indexed ACLs are current — no permission drift."
            : $"Re-ACL sweep: {summary.DriftCount} drifted, {summary.BaselineCount} baseline, "
              + $"{summary.UnresolvedCount} unresolved"
              + (dryRun ? " (dry run — nothing changed)." : $"; {summary.ReAcledCount} re-ACLed."));
        return summary;
    }

    private static void WriteReport(string path, ReAclSummary summary, bool dryRun)
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
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            if (correlationId is not null)
                entry["correlation_id"] = correlationId;
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
        }
        writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = "summary",
            ["drift"] = summary.DriftCount,
            ["baseline"] = summary.BaselineCount,
            ["unresolved"] = summary.UnresolvedCount,
            ["reacled"] = summary.ReAcledCount,
            ["dry_run"] = dryRun,
            ["by_kind"] = summary.CountsByKind,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        }, JsonOptions));
        _ = Logger;
    }
}
