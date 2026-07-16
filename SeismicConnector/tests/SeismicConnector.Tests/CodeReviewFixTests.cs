// Regression tests for the code-review findings (2026-07 review):
//
//   HIGH-1   — SEISMIC_FALLBACK_ACL=tenant must never widen an ACL on a
//              transient identity gap ("unresolved" vs "genuinely public").
//   HIGH-2   — SqliteIdentityStore must be safe under the pipeline's
//              concurrent $batch workers.
//   HIGH-3   — decompression bombs must be bounded DURING extraction.
//   MED-HIGH — teamsite-permission inheritance is actually wired.
//   MED      — late exclusion flags withdraw on INCREMENTAL crawls too.
//   LOW-1    — a classifier regex timeout fails safe (escalates, not clears).
//   LOW-2    — the single-item/webhook path applies teamsite exclusion rules
//              and fails closed when the teamsite cannot be resolved.

using System.IO.Compression;
using System.Text;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── HIGH-1: never widen on unresolved principals ─────────────────────────────

public class TenantFallbackNeverWidensTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "seismic-widen-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly SqliteIdentityStore _store;

    public TenantFallbackNeverWidensTests()
    {
        _store = new SqliteIdentityStore(_dbPath);
        _store.UpsertPrincipal(new PrincipalMapping("su-1", "user", "amy@contoso.com", "entra-user-1", "Amy"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
        }
    }

    private static List<SeismicPermission> Perms(params string[] ids) =>
        ids.Select(id => new SeismicPermission { PrincipalId = id, PrincipalType = "user" }).ToList();

    [Fact]
    public void Resolve_SourceHadPrincipals_NoneMapped_Tenant_IsMarkedUnresolved()
    {
        var mapper = new AclMapper(_store, "tenant", "tenant-guid");
        var result = mapper.Resolve(Perms("ghost-only"));

        // The everyone-entry is only the fallback — flagged so callers holding
        // a previously applied ACL refuse to replace it.
        Assert.False(result.Skipped);
        Assert.True(result.Unresolved);
        Assert.Equal("everyone", Assert.Single(result.Entries).Type);
    }

    [Fact]
    public void Resolve_SourceHadPrincipals_NoneMapped_Skip_IsMarkedUnresolved()
    {
        var mapper = new AclMapper(_store, "skip", "tenant-guid");
        var result = mapper.Resolve(Perms("ghost-only"));
        Assert.True(result.Skipped);
        Assert.True(result.Unresolved);
    }

    [Fact]
    public void Resolve_GenuinelyNoPrincipals_Tenant_IsNotUnresolved()
    {
        var mapper = new AclMapper(_store, "tenant", "tenant-guid");
        var result = mapper.Resolve(new List<SeismicPermission>());

        // Genuinely public content follows the explicit policy.
        Assert.False(result.Skipped);
        Assert.False(result.Unresolved);
        Assert.Equal("everyone", Assert.Single(result.Entries).Type);
    }

    [Fact]
    public void Resolve_SomePrincipalsMapped_IsNotUnresolved()
    {
        var mapper = new AclMapper(_store, "tenant", "tenant-guid");
        var result = mapper.Resolve(Perms("su-1", "ghost-only"));
        Assert.False(result.Unresolved);
        Assert.Equal("entra-user-1", Assert.Single(result.Entries).Value);
    }

    [Fact]
    public async Task ReAcl_TenantFallback_UnresolvedDrift_LeavesAclUnchanged()
    {
        // The exact HIGH-1 scenario: PERMISSION_REACL + SEISMIC_FALLBACK_ACL=tenant.
        // A restricted item's principals stop mapping (stale identity store);
        // the fingerprint would "drift" to the everyone-fingerprint — the old
        // behaviour PATCHed the ACL tenant-wide.
        using var harness = new PipelineHarness(fallbackAcl: "tenant", permissionReacl: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        var fp1 = harness.Store.GetTrackedItem("c1")!.AclFingerprint;
        Assert.NotNull(fp1);

        // Same version; principals now reference only an UNMAPPED id.
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("ghost-only")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // No PATCH, no drift counted, fingerprint exactly as before — the ACL
        // was never widened to everyone.
        Assert.Empty(harness.AclPatchedItemIds);
        Assert.Equal(0, harness.Pipeline.Stats.ReAcled);
        Assert.Equal(0, harness.Pipeline.Stats.AclDrift);
        Assert.Equal(fp1, harness.Store.GetTrackedItem("c1")!.AclFingerprint);
    }

    [Fact]
    public async Task VersionChange_TenantFallback_UnresolvedPrincipals_DoesNotReingestWithEveryone()
    {
        // Same identity gap, but the content version ALSO changed: the full
        // PUT path must not re-ingest the item with the everyone fallback.
        using var harness = new PipelineHarness(fallbackAcl: "tenant");
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v2", permissions: Perms("ghost-only")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Only the original PUT; the tracked item still carries v1 (retried
        // once the identity store recovers) and was never deleted.
        Assert.Equal(1, harness.PutItemIds().Count(id => id == "c1"));
        Assert.Equal("v1", harness.Store.GetTrackedItem("c1")!.VersionId);
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);
        Assert.Empty(harness.DeletedItemIds);
        var acl = harness.LastPutBody("c1")!["acl"]!.AsArray();
        Assert.DoesNotContain(acl, e => e!["type"]!.GetValue<string>() == "everyone");
    }

    [Fact]
    public async Task GenuinelyEmptyPermissions_TenantFallback_StillFollowsPolicy()
    {
        // Content that truly carries no principals (item AND teamsite) is
        // "public per policy" — the tenant fallback still applies on ingest.
        using var harness = new PipelineHarness(fallbackAcl: "tenant");
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", permissions: new List<SeismicPermission>()));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Contains("c1", harness.PutItemIds());
        var acl = harness.LastPutBody("c1")!["acl"]!.AsArray();
        Assert.Equal("everyone", acl[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReAcl_GenuinelyEmptiedPermissions_TenantPolicy_IsRealDrift()
    {
        // Permissions genuinely emptied (not an identity gap): policy applies,
        // so the drift IS repaired — proving the unresolved guard does not
        // block legitimate re-ACLs.
        using var harness = new PipelineHarness(fallbackAcl: "tenant", permissionReacl: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: new List<SeismicPermission>()));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Contains("c1", harness.AclPatchedItemIds);
        Assert.Equal(1, harness.Pipeline.Stats.AclDrift);
    }
}

// ── HIGH-2: identity store thread safety ─────────────────────────────────────

public class IdentityStoreConcurrencyTests
{
    [Fact]
    public async Task ParallelReadersAndWriters_NoExceptions_ConsistentFinalState()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "seismic-conc-" + Guid.NewGuid().ToString("N") + ".db");
        const int workers = 8;
        const int opsPerWorker = 100;
        try
        {
            using (var store = new SqliteIdentityStore(dbPath))
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
                {
                    await start.Task;
                    for (var i = 0; i < opsPerWorker; i++)
                    {
                        var itemId = $"item-{w}-{i}";
                        store.UpsertTrackedItem(new TrackedItem(
                            itemId, "v1", $"ts{w}", null, DateTime.UtcNow, "ingested")
                        {
                            AclFingerprint = $"fp-{w}-{i}",
                        });
                        var roundTripped = store.GetTrackedItem(itemId);
                        Assert.NotNull(roundTripped);
                        Assert.Equal($"fp-{w}-{i}", roundTripped!.AclFingerprint);

                        store.UpsertPrincipal(new PrincipalMapping(
                            $"p-{w}-{i}", "user", $"u{w}-{i}@contoso.com", $"entra-{w}-{i}", null));
                        Assert.Equal($"entra-{w}-{i}", store.GetEntraObjectId($"p-{w}-{i}"));

                        // Mixed full-table reads race the writers on other threads.
                        _ = store.GetAllTrackedItems();
                        _ = store.GetItemsNotSeenSince(DateTime.UtcNow.AddMinutes(-5));
                    }
                })).ToList();
                start.SetResult();
                await Task.WhenAll(tasks);  // any "database is locked"/torn read throws here

                Assert.Equal(workers * opsPerWorker, store.GetAllTrackedItems().Count);
                Assert.Equal(workers * opsPerWorker, store.CountMappedPrincipals());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(dbPath);
            }
            catch
            {
            }
        }
    }
}

// ── HIGH-3: decompression bombs bounded during extraction ────────────────────

public class DecompressionBombTests
{
    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    [Fact]
    public void PdfTryInflate_HighRatioBomb_IsTruncatedAtCeiling()
    {
        // ~48 MB of zeros compress to a few KB — a classic bomb well past the
        // 32 MB inflation ceiling.
        var bomb = ZlibCompress(new byte[ExtractionLimits.MaxInflatedStreamBytes + 16 * 1024 * 1024]);
        Assert.True(bomb.Length < 1024 * 1024);  // sanity: high compression ratio

        var inflated = PdfTextExtractor.TryInflate(bomb);

        Assert.NotNull(inflated);
        Assert.Equal(ExtractionLimits.MaxInflatedStreamBytes, inflated!.Length);
    }

    [Fact]
    public void PdfExtract_BombStream_CompletesAndStillExtractsRealText()
    {
        var bombBody = ZlibCompress(new byte[ExtractionLimits.MaxInflatedStreamBytes + 8 * 1024 * 1024]);
        var realBody = Encoding.ASCII.GetBytes("BT (Hello bomb world) Tj ET");
        using var payload = new MemoryStream();
        void Append(string s) => payload.Write(Encoding.ASCII.GetBytes(s));
        Append("%PDF-1.4\nstream\n");
        payload.Write(bombBody);
        Append("\nendstream\nstream\n");
        payload.Write(realBody);
        Append("\nendstream\n%%EOF");

        var text = new PdfTextExtractor().Extract(payload.ToArray());

        Assert.Contains("Hello bomb world", text);
    }

    [Fact]
    public void OpenXml_ManyTextNodes_StopAtAccumulationCeiling()
    {
        // 3,000 nodes x 1,000 chars = 3M chars of text in a small zip — the
        // extractor must stop at the 1M accumulation ceiling, not build it all.
        var chunk = new string('a', 1000);
        var sb = new StringBuilder("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        for (var i = 0; i < 3000; i++)
            sb.Append("<w:p><w:r><w:t>").Append(chunk).Append("</w:t></w:r></w:p>");
        sb.Append("</w:body></w:document>");
        var payload = MakeZip("word/document.xml", sb.ToString());

        var text = new OpenXmlTextExtractor().Extract(payload);

        Assert.NotEmpty(text);
        Assert.True(text.Length <= ExtractionLimits.MaxAccumulatedChars + 2000,
            $"extracted {text.Length} chars — accumulation ceiling not applied");
    }

    [Fact]
    public void OpenXml_SingleGiantTextNode_IsAbortedByXmlCharacterCeiling()
    {
        // One text node past MaxXmlCharsPerPart: the XmlReader aborts the part
        // instead of materialising the inflated giant — no throw, bounded output.
        var giant = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + "<w:body><w:p><w:r><w:t>"
            + new string('a', (int)ExtractionLimits.MaxXmlCharsPerPart + 1_000_000)
            + "</w:t></w:r></w:p></w:body></w:document>";
        var payload = MakeZip("word/document.xml", giant);

        var text = new OpenXmlTextExtractor().Extract(payload);

        Assert.True(text.Length <= ExtractionLimits.MaxAccumulatedChars + 2000,
            $"extracted {text.Length} chars — XML character ceiling not applied");
    }

    [Fact]
    public void OpenXml_OversizedPart_DoesNotPoisonOtherParts()
    {
        // A bomb slide must not wipe the text extracted from healthy slides.
        var bomb = "<sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:t>"
            + new string('x', (int)ExtractionLimits.MaxXmlCharsPerPart + 500_000)
            + "</a:t></sld>";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "ppt/slides/slide1.xml",
                "<sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:t>healthy slide text</a:t></sld>");
            WriteEntry(zip, "ppt/slides/slide2.xml", bomb);
        }

        var text = new OpenXmlTextExtractor().Extract(buffer.ToArray());

        Assert.Contains("healthy slide text", text);
    }

    private static byte[] MakeZip(string entryName, string xml)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, entryName, xml);
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}

// ── MED-HIGH: teamsite-permission inheritance is wired ───────────────────────

public class TeamsitePermissionInheritanceTests
{
    private static List<SeismicPermission> Perms(params string[] ids) =>
        ids.Select(id => new SeismicPermission { PrincipalId = id, PrincipalType = "user" }).ToList();

    [Fact]
    public async Task ItemWithNoPermissions_InheritsTeamsiteAcl()
    {
        // fallback=skip: before the fix an empty item-level permission set was
        // resolved against a NEVER-SUPPLIED teamsite list and dropped.
        using var harness = new PipelineHarness();  // fallback skip
        harness.AddTeamsite("ts1", permissions: Perms("seismic-user-1"));
        harness.AddContent(TestContent.Make("c1", permissions: new List<SeismicPermission>()));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Contains("c1", harness.PutItemIds());
        Assert.Equal(0, harness.Pipeline.Stats.AclSkipped);
        var acl = harness.LastPutBody("c1")!["acl"]!.AsArray();
        var entry = Assert.Single(acl);
        Assert.Equal("user", entry!["type"]!.GetValue<string>());
        Assert.Equal("entra-user-1", entry["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task ItemPermissions_StillWinOverTeamsitePermissions()
    {
        using var harness = new PipelineHarness();
        harness.Store.UpsertPrincipal(new PrincipalMapping("seismic-user-2", "user", "bob@contoso.com", "entra-user-2", "Bob"));
        harness.AddTeamsite("ts1", permissions: Perms("seismic-user-2"));
        harness.AddContent(TestContent.Make("c1", permissions: Perms("seismic-user-1")));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var acl = harness.LastPutBody("c1")!["acl"]!.AsArray();
        var entry = Assert.Single(acl);
        Assert.Equal("entra-user-1", entry!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task LibraryItem_CarriesTeamsitePermissions()
    {
        // Library metadata items have no item-level permissions by definition;
        // with mapped teamsite permissions they now index under the library ACL
        // even with fallback=skip (previously always acl-skipped).
        using var harness = new PipelineHarness();  // fallback skip
        harness.AddTeamsite("ts1", permissions: Perms("seismic-user-1"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "Library"));

        Assert.Contains("lib-ts1", harness.PutItemIds());
        var acl = harness.LastPutBody("lib-ts1")!["acl"]!.AsArray();
        Assert.Equal("entra-user-1", Assert.Single(acl)!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReAcl_TeamsitePermissionChange_OnItemWithoutOwnPermissions_IsDetected()
    {
        // The whole point of PERMISSION_REACL: a LIBRARY permission change must
        // reach items that inherit it.
        using var harness = new PipelineHarness(permissionReacl: true);
        harness.Store.UpsertPrincipal(new PrincipalMapping("seismic-user-2", "user", "bob@contoso.com", "entra-user-2", "Bob"));
        harness.AddTeamsite("ts1", permissions: Perms("seismic-user-1"));
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: new List<SeismicPermission>()));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // The teamsite ACL widens; the item (and its version) are untouched.
        harness.Seismic.Teamsites.Clear();
        harness.AddTeamsite("ts1", permissions: Perms("seismic-user-1", "seismic-user-2"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Contains("c1", harness.AclPatchedItemIds);
        Assert.Equal(1, harness.Pipeline.Stats.AclDrift);
        var values = harness.AclPatches["c1"]!.AsArray()
            .Select(e => e!["value"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("entra-user-2", values);
    }
}

// ── MED: late exclusion flags withdraw on incremental crawls ─────────────────

public class IncrementalLateExclusionTests
{
    private static ExclusionRules MneRules() => new()
    {
        ExcludedFlags = new List<string> { "MNE" },
        FlagProperties = new List<string> { "classification" },
    };

    [Fact]
    public async Task LateFlag_WithoutModifiedAtBump_IsWithdrawnByIncremental()
    {
        using var harness = new PipelineHarness(MneRules());
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", modifiedAt: DateTime.UtcNow.AddDays(-10)));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);

        // Compliance flags the item WITHOUT touching modifiedAt — the
        // incremental listing (modifiedAt >= lastSync) never re-lists it.
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1",
            modifiedAt: DateTime.UtcNow.AddDays(-10),
            properties: new List<SeismicProperty> { new() { Name = "classification", Value = "MNE" } }));

        var report = new ReconciliationReport();
        harness.Pipeline.Report = report;
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: false));

        // Withdrawn on the INCREMENTAL — no full crawl needed.
        Assert.Contains("c1", harness.DeletedItemIds);
        Assert.Equal("excluded", harness.Store.GetTrackedItem("c1")!.Status);
        Assert.Equal(1, report.WithdrawnCount);
        Assert.True(harness.Pipeline.Stats.Excluded >= 1);
    }

    [Fact]
    public async Task Incremental_UnflaggedUnmodifiedItems_AreNotTouched()
    {
        using var harness = new PipelineHarness(MneRules());
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", modifiedAt: DateTime.UtcNow.AddDays(-10)));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: false));

        Assert.Empty(harness.DeletedItemIds);
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);
        // Not re-PUT either: the item was neither re-listed nor re-ingested.
        Assert.Equal(1, harness.PutItemIds().Count(id => id == "c1"));
    }

    [Fact]
    public async Task Incremental_ItemMissingFromSource_IsLeftToTheFullCrawlReaper()
    {
        // An incremental sees a partial world: a tracked item absent from the
        // re-listing must NOT be withdrawn by the late-exclusion pass.
        using var harness = new PipelineHarness(MneRules());
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", modifiedAt: DateTime.UtcNow.AddDays(-10)));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();  // gone from source
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: false));

        Assert.Empty(harness.DeletedItemIds);
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);
    }
}

// ── LOW-1: classifier match-timeout fails safe ───────────────────────────────

public class ClassifierTimeoutFailSafeTests
{
    /// <summary>A catastrophically backtracking pattern + input that trips any timeout.</summary>
    private static ContentClassifier PathologicalClassifier() => new(
        new ClassificationRules
        {
            DefaultLabel = "Internal",
            Patterns = new List<ClassificationPattern>
            {
                new() { Category = "PII.Test", Regex = "(a+)+$" },
            },
            Luhn = null,
        },
        matchTimeout: TimeSpan.FromMilliseconds(1));

    private static readonly string TimeoutTrippingText = new string('a', 5000) + "!";

    [Fact]
    public void RegexTimeout_AttachesClassificationIncompleteCategory()
    {
        var categories = PathologicalClassifier().Detect(TimeoutTrippingText);
        Assert.Contains(ClassificationRules.ClassificationIncompleteCategory, categories);
    }

    [Fact]
    public void RegexTimeout_EscalatesLabelToRestricted_NotDefault()
    {
        // Before the fix a timeout was silently "no match" → default label →
        // a crafted document could evade PII/secret detection.
        var result = PathologicalClassifier().Classify(TimeoutTrippingText, distribution: null);
        Assert.Equal(SensitivityLabel.Restricted, result.Label);
        Assert.Contains(ClassificationRules.ClassificationIncompleteCategory, result.Categories);
    }

    [Fact]
    public void LuhnTimeout_AlsoFailsSafe()
    {
        var classifier = new ContentClassifier(
            new ClassificationRules
            {
                Patterns = new List<ClassificationPattern>(),
                Luhn = new LuhnRule { CandidateRegex = "(\\d+)+$" },
            },
            matchTimeout: TimeSpan.FromMilliseconds(1));

        var categories = classifier.Detect(new string('1', 5000) + "!");
        Assert.Contains(ClassificationRules.ClassificationIncompleteCategory, categories);
    }

    [Fact]
    public void CleanText_WithHealthyPatterns_HasNoIncompleteCategory()
    {
        var classifier = new ContentClassifier(ClassificationRules.Default());
        var categories = classifier.Detect("perfectly ordinary sales collateral text");
        Assert.DoesNotContain(ClassificationRules.ClassificationIncompleteCategory, categories);
        Assert.Empty(categories);
    }
}

// ── LOW-2: single-item path teamsite exclusion + fail-closed ─────────────────

public class SingleItemTeamsiteGateTests
{
    [Fact]
    public async Task RestrictedByNameTeamsite_BlocksSingleItemIngest()
    {
        var rules = new ExclusionRules { RestrictedLibraries = new List<string> { "Deal Room" } };
        using var harness = new PipelineHarness(rules);
        harness.AddTeamsite("ts1", name: "Deal Room");
        harness.AddContent(TestContent.Make("c1"));

        // Name-based rules only fire off the RESOLVED teamsite — before the
        // fix the single-item path skipped IsTeamsiteExcluded entirely.
        Assert.True(await harness.Pipeline.IngestSingleAsync("c1", "ts1"));

        Assert.DoesNotContain("c1", harness.PutItemIds());
        Assert.Equal("excluded", harness.Store.GetTrackedItem("c1")!.Status);
        Assert.True(harness.Pipeline.Stats.Excluded >= 1);
    }

    [Fact]
    public async Task SeismicRestrictedTeamsite_BlocksSingleItemIngest()
    {
        using var harness = new PipelineHarness(new ExclusionRules());  // excludeRestrictedTeamsites default true
        harness.AddTeamsite("ts1", restricted: true);
        harness.AddContent(TestContent.Make("c1"));

        Assert.True(await harness.Pipeline.IngestSingleAsync("c1", "ts1"));

        Assert.DoesNotContain("c1", harness.PutItemIds());
        Assert.Equal("excluded", harness.Store.GetTrackedItem("c1")!.Status);
    }

    [Fact]
    public async Task RestrictedTeamsite_SingleItemEvent_WithdrawsPreviouslyIngestedItem()
    {
        // Ingested while open; the library becomes restricted; a webhook event
        // for the item must withdraw it, not refresh it.
        using var harness = new PipelineHarness(new ExclusionRules());
        harness.AddTeamsite("ts1", name: "Deal Room");
        harness.AddContent(TestContent.Make("c1"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);

        var restricted = new ExclusionFilter(new ExclusionRules
        {
            RestrictedLibraries = new List<string> { "Deal Room" },
        });
        var pipeline = new IngestPipeline(
            harness.Config, harness.Seismic, harness.Graph, harness.Store, restricted);

        Assert.True(await pipeline.IngestSingleAsync("c1", "ts1"));

        Assert.Contains("c1", harness.DeletedItemIds);
        Assert.Equal("excluded", harness.Store.GetTrackedItem("c1")!.Status);
    }

    [Fact]
    public async Task UnresolvableTeamsite_FailsClosed()
    {
        using var harness = new PipelineHarness(new ExclusionRules());
        // Content exists in the teamsite's content list, but the teamsite is
        // NOT in the teamsites listing → name/isRestricted rules can't be
        // evaluated → refuse to ingest.
        harness.AddContent(TestContent.Make("c1", teamsiteId: "ghost-ts"));

        Assert.False(await harness.Pipeline.IngestSingleAsync("c1", "ghost-ts"));

        Assert.DoesNotContain("c1", harness.PutItemIds());
        Assert.Null(harness.Store.GetTrackedItem("c1"));
    }
}
