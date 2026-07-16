// Round-6 data classification & sensitivity labeling — unit coverage for the
// dependency-free content classifier, label derivation (distribution fold-in +
// precedence) and the Purview-aligned manifest. No network, no pipeline harness.

using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

public class ContentClassifierTests
{
    private static ContentClassifier Default() => new(ClassificationRules.Default());

    // ── Detection: PII ─────────────────────────────────────────────────────────

    [Fact]
    public void Detects_Email() =>
        Assert.Contains("PII.Email", Default().Detect("reach jane.doe@vendor.co.uk for signoff"));

    [Fact]
    public void Detects_Phone() =>
        Assert.Contains("PII.Phone", Default().Detect("call 415-555-0142 tomorrow"));

    [Fact]
    public void Detects_NationalId() =>
        Assert.Contains("PII.NationalId", Default().Detect("SSN 123-45-6789 on file"));

    // ── Detection: PCI (Luhn) ──────────────────────────────────────────────────

    [Fact]
    public void Detects_ValidCard_ByLuhn() =>
        Assert.Contains("PCI.CardNumber", Default().Detect("card 4111 1111 1111 1111 exp 12/29"));

    [Fact]
    public void DoesNotDetect_LuhnInvalidCardShapedNumber() =>
        Assert.DoesNotContain("PCI.CardNumber", Default().Detect("ref 4111 1111 1111 1112"));

    [Fact]
    public void DoesNotDetect_OrdinaryLongNumbers_AsPci()
    {
        Assert.DoesNotContain("PCI.CardNumber", Default().Detect("order id 900012345 total 120000"));
    }

    // ── Detection: secrets & MNE-adjacency ─────────────────────────────────────

    [Fact]
    public void Detects_ApiKeySecret() =>
        Assert.Contains("Secret.ApiKey", Default().Detect("key AKIAIOSFODNN7EXAMPLE here"));

    [Fact]
    public void Detects_MneAdjacentKeyword() =>
        Assert.Contains(
            ClassificationRules.MneAdjacentCategory,
            Default().Detect("This deck contains material non-public information."));

    // ── Negatives / robustness ─────────────────────────────────────────────────

    [Fact]
    public void CleanText_DetectsNothing()
    {
        Assert.Empty(Default().Detect("Q3 enablement deck for the treasury product."));
        Assert.Empty(Default().Detect(null));
        Assert.Empty(Default().Detect(""));
    }

    [Fact]
    public void IsLuhnValid_TrueAndFalse()
    {
        Assert.True(ContentClassifier.IsLuhnValid("4111111111111111"));
        Assert.False(ContentClassifier.IsLuhnValid("4111111111111112"));
        Assert.False(ContentClassifier.IsLuhnValid(""));
        Assert.False(ContentClassifier.IsLuhnValid("41a1"));
    }

    [Fact]
    public void ParseLabel_CaseInsensitive_AndFallback()
    {
        Assert.Equal(SensitivityLabel.Restricted, ContentClassifier.ParseLabel("restricted", SensitivityLabel.Public));
        Assert.Equal(SensitivityLabel.Confidential, ContentClassifier.ParseLabel("Confidential", SensitivityLabel.Public));
        Assert.Equal(SensitivityLabel.Internal, ContentClassifier.ParseLabel("bogus", SensitivityLabel.Internal));
        Assert.Equal(SensitivityLabel.Public, ContentClassifier.ParseLabel(null, SensitivityLabel.Public));
    }

    // ── Classify: distribution fold-in + precedence ────────────────────────────

    [Fact]
    public void Classify_CleanText_UsesDefaultLabel()
    {
        var r = Default().Classify("nothing sensitive here", distribution: null);
        Assert.Equal(SensitivityLabel.Internal, r.Label);   // default label
        Assert.Empty(r.Categories);
    }

    [Fact]
    public void Classify_InternalOnlyDistribution_Confidential()
    {
        var r = Default().Classify("clean body", "internal-only");
        Assert.Equal(SensitivityLabel.Confidential, r.Label);
    }

    [Fact]
    public void Classify_ClientApprovedDistribution_Internal()
    {
        var r = Default().Classify("clean body", "client-approved");
        Assert.Equal(SensitivityLabel.Internal, r.Label);
    }

    [Fact]
    public void Classify_DetectedPii_BeatsDistribution_Restricted()
    {
        // Even client-approved content with PII in the body escalates to Restricted.
        var r = Default().Classify("contact jane.doe@vendor.com", "client-approved");
        Assert.Equal(SensitivityLabel.Restricted, r.Label);
        Assert.Contains("PII.Email", r.Categories);
    }

    [Fact]
    public void Classify_MneAdjacent_Restricted()
    {
        var r = Default().Classify("strictly embargoed until earnings", "client-approved");
        Assert.Equal(SensitivityLabel.Restricted, r.Label);
    }

    [Theory]
    [InlineData(null, SensitivityLabel.Internal)]
    [InlineData("internal-only", SensitivityLabel.Confidential)]
    [InlineData("INTERNAL-ONLY", SensitivityLabel.Confidential)]
    [InlineData("client-approved", SensitivityLabel.Internal)]
    [InlineData("something-else", SensitivityLabel.Internal)]
    public void DeriveLabel_NoCategories_FollowsDistribution(string? distribution, SensitivityLabel expected) =>
        Assert.Equal(expected, ContentClassifier.DeriveLabel(distribution, Array.Empty<string>(), SensitivityLabel.Internal));

    [Fact]
    public void DeriveLabel_AnyCategory_IsRestricted() =>
        Assert.Equal(
            SensitivityLabel.Restricted,
            ContentClassifier.DeriveLabel("client-approved", new[] { "PII.Email" }, SensitivityLabel.Internal));

    // ── Manifest (Purview-aligned export) ──────────────────────────────────────

    [Fact]
    public void Manifest_File_WritesItemLinesAndSummary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"classification_{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var m = new ClassificationManifest(path))
            {
                m.RecordItem("c1", "ts1", SensitivityLabel.Restricted, new[] { "PII.Email" });
                m.RecordItem("c2", "ts1", SensitivityLabel.Internal, Array.Empty<string>());
            }   // Dispose → Finish writes the summary and closes

            var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
            Assert.Equal(3, lines.Count);   // 2 items + 1 summary
            Assert.Contains("\"item_id\":\"c1\"", lines[0]);
            Assert.Contains("\"record\":\"summary\"", lines[2]);
            Assert.Contains("\"classified_total\":2", lines[2]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Manifest_InMemory_CountsOnly_NoFile()
    {
        using var m = new ClassificationManifest();   // parameterless = counts only
        m.RecordItem("c1", "ts1", SensitivityLabel.Restricted, new[] { "PII.Email", "PCI.CardNumber" });
        m.RecordItem("c2", "ts1", SensitivityLabel.Restricted, new[] { "PII.Email" });

        Assert.Null(m.FilePath);
        Assert.Equal(2, m.Total);
        Assert.Equal(2, m.CountsByLabel["Restricted"]);
        Assert.Equal(2, m.CountsByCategory["PII.Email"]);
        Assert.Equal(1, m.CountsByCategory["PCI.CardNumber"]);
    }
}
