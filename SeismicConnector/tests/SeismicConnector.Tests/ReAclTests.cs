// Improvement round 3: permission-change re-ACL detection.
//
// An item's ACL was previously only refreshed when the item's CONTENT changed.
// If a teamsite/content-profile permission changes but the content doesn't,
// the indexed ACL goes stale — a compliance risk. This round tracks a per-item
// ACL fingerprint and re-ACLs (ACL-only Graph PATCH, no content re-send) when
// the resolved principal set drifts: automatically per crawl behind
// PERMISSION_REACL, and on demand via `reacl [--dry-run]`.

using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── ACL fingerprint ──────────────────────────────────────────────────────────

public class AclFingerprintTests
{
    private static AclResult Acl(params AclEntry[] entries) =>
        new(entries, 0, entries.Length == 0);

    [Fact]
    public void SamePrincipals_SameFingerprint()
    {
        var a = Acl(AclEntry.GrantUser("u1"), AclEntry.GrantGroup("g1"));
        var b = Acl(AclEntry.GrantUser("u1"), AclEntry.GrantGroup("g1"));
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void OrderIndependent()
    {
        var a = Acl(AclEntry.GrantUser("u1"), AclEntry.GrantUser("u2"), AclEntry.GrantGroup("g1"));
        var b = Acl(AclEntry.GrantGroup("g1"), AclEntry.GrantUser("u2"), AclEntry.GrantUser("u1"));
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void DuplicateEntries_DoNotChangeFingerprint()
    {
        var a = Acl(AclEntry.GrantUser("u1"));
        var b = Acl(AclEntry.GrantUser("u1"), AclEntry.GrantUser("u1"));
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void CaseInsensitiveOnValue()
    {
        Assert.Equal(Acl(AclEntry.GrantUser("U1")).Fingerprint(), Acl(AclEntry.GrantUser("u1")).Fingerprint());
    }

    [Fact]
    public void AddingPrincipal_ChangesFingerprint()
    {
        var before = Acl(AclEntry.GrantUser("u1")).Fingerprint();
        var after = Acl(AclEntry.GrantUser("u1"), AclEntry.GrantUser("u2")).Fingerprint();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void RemovingPrincipal_ChangesFingerprint()
    {
        var before = Acl(AclEntry.GrantUser("u1"), AclEntry.GrantUser("u2")).Fingerprint();
        var after = Acl(AclEntry.GrantUser("u1")).Fingerprint();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void UserVsGroup_WithSameValue_DiffersInFingerprint()
    {
        Assert.NotEqual(Acl(AclEntry.GrantUser("x")).Fingerprint(), Acl(AclEntry.GrantGroup("x")).Fingerprint());
    }

    [Fact]
    public void EmptyAcl_HasStableSentinelFingerprint()
    {
        Assert.Equal(Acl().Fingerprint(), Acl().Fingerprint());
        Assert.NotEqual(Acl().Fingerprint(), Acl(AclEntry.GrantUser("u1")).Fingerprint());
    }

    [Fact]
    public void Fingerprint_IsShortHex()
    {
        var fp = Acl(AclEntry.GrantUser("u1")).Fingerprint();
        Assert.Equal(32, fp.Length);  // 16 bytes hex
        Assert.All(fp, c => Assert.True(Uri.IsHexDigit(c)));
    }
}

// ── automatic per-crawl re-ACL (PERMISSION_REACL) ────────────────────────────

public class PermissionReaclPipelineTests
{
    private static PipelineHarness TwoPrincipalHarness(bool permissionReacl)
    {
        var harness = new PipelineHarness(permissionReacl: permissionReacl);
        // A second mappable principal so we can widen the resolved set.
        harness.Store.UpsertPrincipal(new PrincipalMapping("seismic-user-2", "user", "bob@contoso.com", "entra-user-2", "Bob"));
        return harness;
    }

    private static List<SeismicPermission> Perms(params string[] principalIds) =>
        principalIds.Select(id => new SeismicPermission { PrincipalId = id, PrincipalType = "user" }).ToList();

    [Fact]
    public async Task PermissionDrift_OnUnchangedContent_ReAclsWithoutReingest()
    {
        using var harness = TwoPrincipalHarness(permissionReacl: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        var fp1 = harness.Store.GetTrackedItem("c1")!.AclFingerprint;
        Assert.NotNull(fp1);

        // Permissions widen; content (version) is unchanged.
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1",
            permissions: Perms("seismic-user-1", "seismic-user-2")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Re-ACLed via PATCH; content NOT re-sent (only one PUT ever, from crawl 1).
        Assert.Contains("c1", harness.AclPatchedItemIds);
        Assert.Equal(1, harness.PutItemIds().Count(id => id == "c1"));
        Assert.Equal(1, harness.Pipeline.Stats.AclDrift);
        Assert.Equal(1, harness.Pipeline.Stats.ReAcled);

        // Stored fingerprint advanced; PATCH body carried both principals, no content.
        Assert.NotEqual(fp1, harness.Store.GetTrackedItem("c1")!.AclFingerprint);
        var patch = harness.AclPatches["c1"]!.AsArray();
        var values = patch.Select(e => e!["value"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("entra-user-1", values);
        Assert.Contains("entra-user-2", values);
    }

    [Fact]
    public async Task NoPermissionChange_IsANoOp()
    {
        using var harness = TwoPrincipalHarness(permissionReacl: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Second crawl, identical permissions and version.
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Empty(harness.AclPatchedItemIds);
        Assert.Equal(0, harness.Pipeline.Stats.AclDrift);
        Assert.Equal(0, harness.Pipeline.Stats.ReAcled);
    }

    [Fact]
    public async Task Disabled_DoesNotReAcl_EvenWhenPermissionsChange()
    {
        using var harness = TwoPrincipalHarness(permissionReacl: false);  // toggle OFF
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1",
            permissions: Perms("seismic-user-1", "seismic-user-2")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Empty(harness.AclPatchedItemIds);  // gated off → no permission resolution / PATCH
        Assert.Equal(0, harness.Pipeline.Stats.ReAcled);
    }

    [Fact]
    public async Task UnresolvablePermissions_NeverWidens_LeavesAclUnchanged()
    {
        using var harness = TwoPrincipalHarness(permissionReacl: true);  // fallback = skip (default)
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        var fp1 = harness.Store.GetTrackedItem("c1")!.AclFingerprint;

        // Permissions now reference only an UNMAPPED principal (unresolvable).
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("ghost-only")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Compliance-safe: no PATCH, ACL (and fingerprint) left exactly as-is.
        Assert.Empty(harness.AclPatchedItemIds);
        Assert.Equal(0, harness.Pipeline.Stats.ReAcled);
        Assert.Equal(fp1, harness.Store.GetTrackedItem("c1")!.AclFingerprint);
        Assert.NotNull(harness.Store.GetTrackedItem("c1"));  // still indexed
    }

    [Fact]
    public async Task NullFingerprint_EstablishesBaseline_WithoutCountingDrift()
    {
        using var harness = TwoPrincipalHarness(permissionReacl: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Simulate an item tracked before re-ACL support: clear its fingerprint.
        var tracked = harness.Store.GetTrackedItem("c1")!;
        harness.Store.UpsertTrackedItem(tracked with { AclFingerprint = null });

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Baseline (re)applies the ACL and sets the fingerprint, but is NOT drift.
        Assert.Contains("c1", harness.AclPatchedItemIds);
        Assert.Equal(1, harness.Pipeline.Stats.ReAcled);
        Assert.Equal(0, harness.Pipeline.Stats.AclDrift);
        Assert.NotNull(harness.Store.GetTrackedItem("c1")!.AclFingerprint);
    }

    [Fact]
    public async Task NewIngest_StoresAclFingerprint()
    {
        using var harness = new PipelineHarness(permissionReacl: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        Assert.False(string.IsNullOrEmpty(harness.Store.GetTrackedItem("c1")!.AclFingerprint));
    }
}

// ── on-demand reacl command (ReAclSweep) ─────────────────────────────────────

public class ReAclSweepTests
{
    private static ReAclSweep Sweep(PipelineHarness harness) =>
        new(harness.Config, harness.Seismic, harness.Store, harness.Pipeline);

    private static List<SeismicPermission> Perms(params string[] principalIds) =>
        principalIds.Select(id => new SeismicPermission { PrincipalId = id, PrincipalType = "user" }).ToList();

    private static PipelineHarness Harness()
    {
        // Manual reacl works regardless of the PERMISSION_REACL flag (off here).
        var harness = new PipelineHarness();
        harness.Store.UpsertPrincipal(new PrincipalMapping("seismic-user-2", "user", "bob@contoso.com", "entra-user-2", "Bob"));
        return harness;
    }

    [Fact]
    public async Task InSync_ReportsNothing()
    {
        using var harness = Harness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        var summary = await Sweep(harness).RunAsync(dryRun: false);
        Assert.Equal(0, summary.Total);
        Assert.Empty(harness.AclPatchedItemIds);
    }

    [Fact]
    public async Task DryRun_ReportsDrift_WithoutMutating()
    {
        using var harness = Harness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        var fp1 = harness.Store.GetTrackedItem("c1")!.AclFingerprint;

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1",
            permissions: Perms("seismic-user-1", "seismic-user-2")));

        var summary = await Sweep(harness).RunAsync(dryRun: true);

        Assert.Equal(1, summary.DriftCount);
        Assert.Equal(0, summary.ReAcledCount);
        Assert.Empty(harness.AclPatchedItemIds);                       // nothing PATCHed
        Assert.Equal(fp1, harness.Store.GetTrackedItem("c1")!.AclFingerprint);  // fingerprint untouched
    }

    [Fact]
    public async Task Repair_ReAclsDriftedItems()
    {
        using var harness = Harness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        var fp1 = harness.Store.GetTrackedItem("c1")!.AclFingerprint;

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1",
            permissions: Perms("seismic-user-1", "seismic-user-2")));

        var summary = await Sweep(harness).RunAsync(dryRun: false);

        Assert.Equal(1, summary.DriftCount);
        Assert.Equal(1, summary.ReAcledCount);
        Assert.Contains("c1", harness.AclPatchedItemIds);
        Assert.Equal(1, harness.PutItemIds().Count(id => id == "c1"));  // content not re-sent
        Assert.NotEqual(fp1, harness.Store.GetTrackedItem("c1")!.AclFingerprint);
    }

    [Fact]
    public async Task Unresolvable_IsReported_NeverWidened()
    {
        using var harness = Harness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("ghost-only")));

        var summary = await Sweep(harness).RunAsync(dryRun: false);

        Assert.Equal(1, summary.UnresolvedCount);
        Assert.Equal(0, summary.DriftCount);
        Assert.Empty(harness.AclPatchedItemIds);
    }

    [Fact]
    public async Task ReportFile_HasFindingsAndSummary()
    {
        using var harness = Harness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1",
            permissions: Perms("seismic-user-1", "seismic-user-2")));

        var reportPath = Path.Combine(harness.State.Dir, "reacl_report_test.jsonl");
        var summary = await Sweep(harness).RunAsync(dryRun: false, reportPath);

        Assert.Equal(1, summary.DriftCount);
        var lines = File.ReadAllLines(reportPath).Where(l => l.Length > 0).ToList();
        Assert.Equal(2, lines.Count);  // 1 finding + summary
        var finding = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!;
        Assert.Equal("acl-drift", finding["kind"]?.GetValue<string>());
        Assert.Equal("c1", finding["item_id"]?.GetValue<string>());
        var trailer = System.Text.Json.Nodes.JsonNode.Parse(lines[1])!;
        Assert.Equal("summary", trailer["kind"]?.GetValue<string>());
        Assert.Equal(1, trailer["drift"]?.GetValue<int>());
        Assert.Equal(1, trailer["reacled"]?.GetValue<int>());
        Assert.False(trailer["dry_run"]!.GetValue<bool>());
    }
}

// ── command wiring + config ──────────────────────────────────────────────────

public class ReAclCommandTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "seismic-reacl-" + Guid.NewGuid().ToString("N"));

    private static readonly (string, string)[] RequiredEnv =
    {
        ("CONNECTOR_ID", "SeismicReacl"),
        ("CONNECTOR_NAME", "n"),
        ("CONNECTOR_DESCRIPTION", "d"),
        ("SEISMIC_TENANT", "contoso"),
        ("SEISMIC_CLIENT_ID", "c"),
        ("SECRET_SEISMIC_CLIENT_SECRET", "s"),
        ("AAD_APP_TENANT_ID", "t"),
        ("AAD_APP_CLIENT_ID", "c"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
    };

    public ReAclCommandTests()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "schema.json"),
            """{"objects":[{"name":"ContentItem","enabled":true},{"name":"Library","enabled":true}]}""");
        foreach (var (key, value) in RequiredEnv)
            Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, _) in RequiredEnv)
            Environment.SetEnvironmentVariable(key, null);
        Environment.SetEnvironmentVariable("PERMISSION_REACL", null);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void PermissionReacl_DefaultsOff()
    {
        Assert.False(AppConfig.Load(_configDir).Seismic.PermissionReacl);
    }

    [Fact]
    public void PermissionReacl_EnabledByEnv()
    {
        Environment.SetEnvironmentVariable("PERMISSION_REACL", "true");
        Assert.True(AppConfig.Load(_configDir).Seismic.PermissionReacl);
    }

    [Fact]
    public void CliParser_ReaclCommand()
    {
        var parser = Commands.CommandRegistry.BuildParser();
        var parsed = parser.ParseArgs(new[] { "reacl", "--dry-run" });
        Assert.Equal("reacl", parsed.Command);
        Assert.True(parsed.GetFlag("dry-run"));
    }

    [Fact]
    public void CliParser_ReaclCommand_RepairMode()
    {
        var parsed = Commands.CommandRegistry.BuildParser().ParseArgs(new[] { "reacl" });
        Assert.Equal("reacl", parsed.Command);
        Assert.False(parsed.GetFlag("dry-run"));
    }
}

// ── metrics ──────────────────────────────────────────────────────────────────

public class ReAclMetricsTests : IDisposable
{
    public ReAclMetricsTests() => Metrics.ResetForTests();

    public void Dispose() => Metrics.ResetForTests();

    [Fact]
    public void ReAclCounters_Render()
    {
        Metrics.IncItemsReAcled(3);
        Metrics.IncAclDriftDetected(2);
        var text = Metrics.RenderPrometheus();
        Assert.Contains("seismic_connector_items_reacled_total 3", text);
        Assert.Contains("seismic_connector_acl_drift_detected_total 2", text);
    }
}
