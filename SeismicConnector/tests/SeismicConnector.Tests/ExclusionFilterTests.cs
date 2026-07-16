// The No-MNE exclusion filter: flag-based, library-based, property rules, and
// the reconciliation report they feed.

using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

public class ExclusionFilterTests
{
    private static ExclusionRules Rules() => new()
    {
        ExcludedFlags = new List<string> { "MNE", "MNPI" },
        FlagProperties = new List<string> { "classification", "complianceFlags" },
        RestrictedLibraries = new List<string> { "M&A Deal Room" },
        RestrictedTeamsiteIds = new List<string> { "ts-restricted" },
        ExcludeRestrictedTeamsites = true,
        PropertyRules = new List<PropertyRule>
        {
            new() { Name = "confidentiality", ExpectedValue = "mnpi" },
        },
    };

    [Fact]
    public void CleanContent_IsAllowed()
    {
        var filter = new ExclusionFilter(Rules());
        var decision = filter.Evaluate(TestContent.Make("c1"));
        Assert.False(decision.Excluded);
    }

    [Theory]
    [InlineData("classification", "MNE")]
    [InlineData("classification", "mne")]           // case-insensitive
    [InlineData("complianceFlags", "MNPI")]
    [InlineData("complianceFlags", " MNE ")]        // whitespace-tolerant
    public void MneFlag_Excludes(string property, string value)
    {
        var filter = new ExclusionFilter(Rules());
        var content = TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = property, Value = value },
        });
        var decision = filter.Evaluate(content);
        Assert.True(decision.Excluded);
        Assert.Equal("mne-flag", decision.Rule);
    }

    [Fact]
    public void MneFlag_InMultiValueProperty_Excludes()
    {
        var filter = new ExclusionFilter(Rules());
        var content = TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = "complianceFlags", Values = new List<string> { "Public", "MNE" } },
        });
        Assert.True(filter.Evaluate(content).Excluded);
    }

    [Fact]
    public void FlagOnUnwatchedProperty_DoesNotExclude()
    {
        var filter = new ExclusionFilter(Rules());
        var content = TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = "notes", Value = "MNE" },  // "notes" not in flagProperties
        });
        Assert.False(filter.Evaluate(content).Excluded);
    }

    [Fact]
    public void RestrictedTeamsiteId_ExcludesContent()
    {
        var filter = new ExclusionFilter(Rules());
        var content = TestContent.Make("c1", teamsiteId: "ts-restricted");
        var decision = filter.Evaluate(content);
        Assert.True(decision.Excluded);
        Assert.Equal("restricted-teamsite-id", decision.Rule);
    }

    [Fact]
    public void RestrictedLibraryName_ExcludesContent()
    {
        var filter = new ExclusionFilter(Rules());
        var teamsite = new SeismicTeamsite { Id = "ts9", Name = "m&a deal room" };  // case-insensitive
        var decision = filter.Evaluate(TestContent.Make("c1", teamsiteId: "ts9"), teamsite);
        Assert.True(decision.Excluded);
        Assert.Equal("restricted-library", decision.Rule);
    }

    [Fact]
    public void SeismicRestrictedTeamsite_ExcludedWhenConfigured()
    {
        var filter = new ExclusionFilter(Rules());
        var teamsite = new SeismicTeamsite { Id = "ts9", Name = "Ordinary", IsRestricted = true };
        Assert.True(filter.Evaluate(TestContent.Make("c1", teamsiteId: "ts9"), teamsite).Excluded);
        Assert.True(filter.IsTeamsiteExcluded(teamsite));
    }

    [Fact]
    public void SeismicRestrictedTeamsite_AllowedWhenDisabled()
    {
        var rules = Rules();
        var filter = new ExclusionFilter(new ExclusionRules
        {
            ExcludedFlags = rules.ExcludedFlags,
            FlagProperties = rules.FlagProperties,
            ExcludeRestrictedTeamsites = false,
        });
        var teamsite = new SeismicTeamsite { Id = "ts9", Name = "Ordinary", IsRestricted = true };
        Assert.False(filter.IsTeamsiteExcluded(teamsite));
        Assert.False(filter.Evaluate(TestContent.Make("c1", teamsiteId: "ts9"), teamsite).Excluded);
    }

    [Fact]
    public void PropertyRule_Excludes()
    {
        var filter = new ExclusionFilter(Rules());
        var content = TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = "confidentiality", Value = "MNPI" },
        });
        var decision = filter.Evaluate(content);
        Assert.True(decision.Excluded);
        Assert.Equal("property-rule", decision.Rule);
    }

    [Fact]
    public void EmptyRules_ExcludeNothing()
    {
        var filter = new ExclusionFilter(new ExclusionRules());
        var teamsite = new SeismicTeamsite { Id = "ts9", Name = "Anything", IsRestricted = false };
        Assert.False(filter.IsTeamsiteExcluded(teamsite));
        Assert.False(filter.Evaluate(TestContent.Make("c1")).Excluded);
    }

    [Fact]
    public void ShippedExclusionsFile_Loads()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "exclusions.json");
        var rules = ExclusionRules.LoadFile(path);
        Assert.Contains("MNE", rules.ExcludedFlags);
        Assert.NotEmpty(rules.FlagProperties);
    }

    [Fact]
    public void MissingRulesFile_MeansNoRules()
    {
        var rules = ExclusionRules.LoadFile(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json"));
        Assert.Empty(rules.ExcludedFlags);
        Assert.Empty(rules.RestrictedLibraries);
    }
}

public class ReconciliationReportTests
{
    [Fact]
    public void Report_WritesJsonlAndSummary()
    {
        var path = Path.Combine(Path.GetTempPath(), "recon-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var report = new ReconciliationReport(path))
            {
                report.RecordExcluded("c1", "ts1", "mne-flag", "flagged MNE");
                report.RecordExcluded("c2", "ts1", "mne-flag", "flagged MNPI");
                report.RecordWithdrawn("c3", "ts2", "restricted-library", "library became restricted");
                report.Finish();
            }

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            Assert.Equal(4, lines.Count);  // 3 events + summary

            var first = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!;
            Assert.Equal("excluded", first["action"]?.GetValue<string>());
            Assert.Equal("c1", first["item_id"]?.GetValue<string>());
            Assert.Equal("mne-flag", first["rule"]?.GetValue<string>());

            var summary = System.Text.Json.Nodes.JsonNode.Parse(lines[^1])!;
            Assert.Equal("summary", summary["action"]?.GetValue<string>());
            Assert.Equal(2, summary["excluded_total"]?.GetValue<int>());
            Assert.Equal(1, summary["withdrawn_total"]?.GetValue<int>());
            Assert.Equal(2, summary["by_rule"]?["mne-flag"]?.GetValue<int>());
            Assert.Equal(1, summary["by_rule"]?["restricted-library"]?.GetValue<int>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Report_CountsWithoutFile()
    {
        var report = new ReconciliationReport();
        report.RecordExcluded("a", null, "mne-flag", "r");
        report.RecordWithdrawn("b", null, "expired", "r");
        Assert.Equal(1, report.ExcludedCount);
        Assert.Equal(1, report.WithdrawnCount);
        Assert.Equal(1, report.CountsByRule["mne-flag"]);
        report.Finish();  // no file — must not throw
    }

    [Fact]
    public void Finish_IsIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), "recon-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var report = new ReconciliationReport(path);
            report.RecordExcluded("a", null, "mne-flag", "r");
            report.Finish();
            report.Finish();
            report.Dispose();
            var summaries = File.ReadAllLines(path).Count(l => l.Contains("\"summary\""));
            Assert.Equal(1, summaries);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
