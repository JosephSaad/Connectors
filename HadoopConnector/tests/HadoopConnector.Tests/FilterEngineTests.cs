// FilterEngineTests.cs
// --------------------
// The full record-predicate operator matrix, AND/OR group semantics,
// case-insensitivity, and partition-key evaluation.

using System.Text.Json.Nodes;
using HadoopConnector.Filters;
using HadoopConnector.Hdfs;

namespace HadoopConnector.Tests;

public class FilterEngineTests
{
    private static readonly Func<DateTime> FixedNow =
        () => new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);

    private static FilterPredicate P(
        string field, FilterOp op, string? value = null, params string[] values) => new()
    {
        Field = field,
        Op = op,
        Value = value,
        Values = values.ToList(),
    };

    private static BdhRecord Rec(params (string Key, string? Value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields)
            obj[key] = value is null ? null : JsonValue.Create(value);
        return new BdhRecord("Contact", obj);
    }

    private static bool Match(FilterPredicate predicate, BdhRecord record) =>
        new FilterEngine(FixedNow).Evaluate(record.Get(predicate.Field), predicate);

    // ── Operator matrix ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Active", "Active", true)]
    [InlineData("Active", "ACTIVE", true)]       // case-insensitive
    [InlineData("Active", "Inactive", false)]
    public void Equals_Operator(string fieldValue, string operand, bool expected) =>
        Assert.Equal(expected, Match(P("Status", FilterOp.Equals, operand), Rec(("Status", fieldValue))));

    [Theory]
    [InlineData("Active", "Inactive", true)]
    [InlineData("Active", "active", false)]
    public void NotEquals_Operator(string fieldValue, string operand, bool expected) =>
        Assert.Equal(expected, Match(P("Status", FilterOp.NotEquals, operand), Rec(("Status", fieldValue))));

    [Fact]
    public void In_And_NotIn()
    {
        var record = Rec(("Region", "emea"));
        Assert.True(Match(P("Region", FilterOp.In, null, "EMEA", "NA"), record));
        Assert.False(Match(P("Region", FilterOp.In, null, "APAC"), record));
        Assert.False(Match(P("Region", FilterOp.NotIn, null, "EMEA"), record));
        Assert.True(Match(P("Region", FilterOp.NotIn, null, "APAC", "LATAM"), record));
    }

    [Fact]
    public void Prefix_And_Contains()
    {
        var record = Rec(("Name", "Acme Corporation"));
        Assert.True(Match(P("Name", FilterOp.Prefix, "acme"), record));
        Assert.False(Match(P("Name", FilterOp.Prefix, "Corp"), record));
        Assert.True(Match(P("Name", FilterOp.Contains, "CORPORAT"), record));
        Assert.False(Match(P("Name", FilterOp.Contains, "Ltd"), record));
    }

    [Theory]
    [InlineData("100000", FilterOp.Gte, "100000", true)]
    [InlineData("99999.5", FilterOp.Gte, "100000", false)]
    [InlineData("100000", FilterOp.Lte, "100000", true)]
    [InlineData("100000.01", FilterOp.Lte, "100000", false)]
    public void Numeric_Gte_Lte(string fieldValue, FilterOp op, string operand, bool expected) =>
        Assert.Equal(expected, Match(P("AnnualRevenue", op, operand), Rec(("AnnualRevenue", fieldValue))));

    [Fact]
    public void Between_IsInclusive()
    {
        Assert.True(Match(P("Amount", FilterOp.Between, null, "10", "20"), Rec(("Amount", "10"))));
        Assert.True(Match(P("Amount", FilterOp.Between, null, "10", "20"), Rec(("Amount", "20"))));
        Assert.True(Match(P("Amount", FilterOp.Between, null, "10", "20"), Rec(("Amount", "15.5"))));
        Assert.False(Match(P("Amount", FilterOp.Between, null, "10", "20"), Rec(("Amount", "9.99"))));
        Assert.False(Match(P("Amount", FilterOp.Between, null, "10", "20"), Rec(("Amount", "20.01"))));
    }

    [Fact]
    public void Numeric_Operators_NeverMatchNonNumericValues()
    {
        Assert.False(Match(P("Amount", FilterOp.Gte, "10"), Rec(("Amount", "not-a-number"))));
        Assert.False(Match(P("Amount", FilterOp.Between, null, "1", "2"), Rec(("Amount", "abc"))));
    }

    [Fact]
    public void WithinLastDays_UsesInjectedClock()
    {
        // Fixed now = 2026-07-17T00:00Z. 30 days back = 2026-06-17T00:00Z (inclusive).
        Assert.True(Match(P("CloseDate", FilterOp.WithinLastDays, "30"), Rec(("CloseDate", "2026-07-01"))));
        Assert.True(Match(P("CloseDate", FilterOp.WithinLastDays, "30"), Rec(("CloseDate", "2026-06-17"))));
        Assert.False(Match(P("CloseDate", FilterOp.WithinLastDays, "30"), Rec(("CloseDate", "2026-06-01"))));
        // ISO timestamps parse too.
        Assert.True(Match(
            P("Modstamp", FilterOp.WithinLastDays, "2"), Rec(("Modstamp", "2026-07-16T08:00:00Z"))));
    }

    [Fact]
    public void After_And_Before()
    {
        var record = Rec(("CloseDate", "2026-03-15"));
        Assert.True(Match(P("CloseDate", FilterOp.After, "2026-03-14"), record));
        Assert.False(Match(P("CloseDate", FilterOp.After, "2026-03-15"), record));  // strict
        Assert.True(Match(P("CloseDate", FilterOp.Before, "2026-03-16"), record));
        Assert.False(Match(P("CloseDate", FilterOp.Before, "2026-03-15"), record)); // strict
    }

    [Fact]
    public void Date_Operators_NeverMatchUnparseableDates()
    {
        Assert.False(Match(P("CloseDate", FilterOp.After, "2026-01-01"), Rec(("CloseDate", "yesterday"))));
        Assert.False(Match(P("CloseDate", FilterOp.WithinLastDays, "30"), Rec(("CloseDate", "n/a"))));
    }

    [Fact]
    public void IsNull_And_IsNotNull()
    {
        Assert.True(Match(P("Email", FilterOp.IsNull), Rec(("Email", null))));
        Assert.True(Match(P("Email", FilterOp.IsNull), Rec(("Other", "x"))));   // absent field
        Assert.False(Match(P("Email", FilterOp.IsNull), Rec(("Email", "a@b.c"))));
        Assert.True(Match(P("Email", FilterOp.IsNotNull), Rec(("Email", "a@b.c"))));
        Assert.False(Match(P("Email", FilterOp.IsNotNull), Rec(("Email", null))));
    }

    [Fact]
    public void ValueComparingOperators_FailOnMissingValue()
    {
        var record = Rec(("Other", "x"));
        Assert.False(Match(P("Status", FilterOp.Equals, "Active"), record));
        Assert.False(Match(P("Status", FilterOp.NotEquals, "Active"), record));
        Assert.False(Match(P("Status", FilterOp.Contains, "A"), record));
    }

    [Fact]
    public void FieldLookup_IsCaseInsensitive()
    {
        var record = Rec(("STATUS", "Active"));
        Assert.True(Match(P("status", FilterOp.Equals, "Active"), record));
    }

    // ── Group semantics (OR of AND groups) ───────────────────────────────────

    private static ObjectFilter Groups(params FilterGroup[] groups) =>
        new() { AnyOf = groups.ToList() };

    [Fact]
    public void MatchesRecord_NoGroups_MatchesEverything()
    {
        var engine = new FilterEngine(FixedNow);
        Assert.True(engine.MatchesRecord(null, Rec(("A", "1"))));
        Assert.True(engine.MatchesRecord(new ObjectFilter(), Rec(("A", "1"))));
    }

    [Fact]
    public void MatchesRecord_AllOf_RequiresEveryPredicate()
    {
        var engine = new FilterEngine(FixedNow);
        var filter = Groups(new FilterGroup
        {
            AllOf =
            {
                P("Status", FilterOp.Equals, "Active"),
                P("Region", FilterOp.In, null, "EMEA"),
            },
        });
        Assert.True(engine.MatchesRecord(filter, Rec(("Status", "Active"), ("Region", "EMEA"))));
        Assert.False(engine.MatchesRecord(filter, Rec(("Status", "Active"), ("Region", "NA"))));
        Assert.False(engine.MatchesRecord(filter, Rec(("Status", "Closed"), ("Region", "EMEA"))));
    }

    [Fact]
    public void MatchesRecord_AnyOf_IsOrOfGroups()
    {
        var engine = new FilterEngine(FixedNow);
        var filter = Groups(
            new FilterGroup { AllOf = { P("Status", FilterOp.Equals, "Open") } },
            new FilterGroup
            {
                AllOf =
                {
                    P("Status", FilterOp.Equals, "Closed"),
                    P("Priority", FilterOp.Equals, "High"),
                },
            });
        Assert.True(engine.MatchesRecord(filter, Rec(("Status", "Open"))));
        Assert.True(engine.MatchesRecord(filter, Rec(("Status", "Closed"), ("Priority", "High"))));
        Assert.False(engine.MatchesRecord(filter, Rec(("Status", "Closed"), ("Priority", "Low"))));
    }

    // ── Partition-key evaluation ─────────────────────────────────────────────

    private static Dictionary<string, string> Keys(params (string K, string V)[] kvs) =>
        kvs.ToDictionary(x => x.K, x => x.V, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void MatchesPartition_PresentKeyMismatch_Prunes()
    {
        var engine = new FilterEngine(FixedNow);
        var predicates = new List<FilterPredicate> { P("region", FilterOp.In, null, "EMEA") };
        Assert.True(engine.MatchesPartition(predicates, Keys(("region", "EMEA"))));
        Assert.False(engine.MatchesPartition(predicates, Keys(("region", "NA"))));
    }

    [Fact]
    public void MatchesPartition_AbsentKey_NeverPrunes()
    {
        var engine = new FilterEngine(FixedNow);
        var predicates = new List<FilterPredicate> { P("region", FilterOp.In, null, "EMEA") };
        // The region key may appear deeper — an absent key must not prune.
        Assert.True(engine.MatchesPartition(predicates, Keys(("dt", "2026-07-01"))));
        Assert.True(engine.MatchesPartition(predicates, Keys()));
    }

    [Fact]
    public void MatchesPartition_DtDateOperators()
    {
        var engine = new FilterEngine(FixedNow);
        var within = new List<FilterPredicate> { P("dt", FilterOp.WithinLastDays, "10") };
        Assert.True(engine.MatchesPartition(within, Keys(("dt", "2026-07-15"))));
        Assert.False(engine.MatchesPartition(within, Keys(("dt", "2026-06-01"))));

        var after = new List<FilterPredicate> { P("dt", FilterOp.After, "2026-07-01") };
        Assert.True(engine.MatchesPartition(after, Keys(("dt", "2026-07-02"))));
        Assert.False(engine.MatchesPartition(after, Keys(("dt", "2026-07-01"))));
    }

    [Fact]
    public void MatchesPartition_KeyLookup_IsCaseInsensitive()
    {
        var engine = new FilterEngine(FixedNow);
        var predicates = new List<FilterPredicate> { P("REGION", FilterOp.Equals, "emea") };
        Assert.True(engine.MatchesPartition(predicates, Keys(("region", "EMEA"))));
    }
}
