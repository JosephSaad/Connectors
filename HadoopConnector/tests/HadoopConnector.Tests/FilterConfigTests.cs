// FilterConfigTests.cs
// --------------------
// config/filters.json parsing + STRICT validation: malformed filters are
// config errors (never silently ignored — a dropped filter at BDH scale is
// an outage), unknown operators/keys rejected, arity enforced.

using HadoopConnector.Filters;

namespace HadoopConnector.Tests;

public class FilterConfigTests
{
    [Fact]
    public void Parse_FullShape_RoundTrips()
    {
        var set = FilterSet.Parse("""
            {
              "objects": {
                "Contact": {
                  "partition": [
                    { "key": "region", "op": "in", "values": ["EMEA", "NA"] },
                    { "key": "dt", "op": "withinLastDays", "value": "120" }
                  ],
                  "anyOf": [
                    { "allOf": [
                      { "field": "Status", "op": "equals", "value": "Active" },
                      { "field": "AnnualRevenue", "op": "gte", "value": "100000" }
                    ] }
                  ],
                  "notes": "doc comment"
                },
                "Case": { "allOf": [ { "field": "Status", "op": "notEquals", "value": "Closed" } ] }
              },
              "fullScanAllowed": ["Account"]
            }
            """);

        var contact = set.For("Contact")!;
        Assert.Equal(2, contact.Partition.Count);
        Assert.Single(contact.AnyOf);
        Assert.Equal(2, contact.AnyOf[0].AllOf.Count);
        Assert.True(contact.HasAnyFilter);

        // Top-level allOf is shorthand for one anyOf group.
        var caseFilter = set.For("Case")!;
        Assert.Single(caseFilter.AnyOf);
        Assert.Single(caseFilter.AnyOf[0].AllOf);

        Assert.True(set.IsFullScanAllowed("Account"));
        Assert.True(set.IsFullScanAllowed("ACCOUNT"));  // case-insensitive
        Assert.False(set.IsFullScanAllowed("Contact"));
        Assert.Null(set.For("Unknown"));
    }

    [Fact]
    public void Parse_ObjectLookup_IsCaseInsensitive()
    {
        var set = FilterSet.Parse(
            """{"objects": {"Contact": {"allOf": [{"field": "A", "op": "isNotNull"}]}}}""");
        Assert.NotNull(set.For("contact"));
    }

    [Fact]
    public void Parse_EmptyDocument_YieldsEmptySet()
    {
        var set = FilterSet.Parse("{}");
        Assert.Empty(set.Objects);
        Assert.Null(set.For("Contact"));
    }

    [Fact]
    public void Load_MissingFile_YieldsEmptySet()
    {
        using var dir = new TempDir();
        var set = FilterSet.Load(Path.Combine(dir.Path, "nope.json"));
        Assert.Empty(set.Objects);
    }

    [Fact]
    public void Load_ExistingFile_Parses()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "filters.json");
        File.WriteAllText(path,
            """{"objects": {"Lead": {"allOf": [{"field": "Status", "op": "isNotNull"}]}}}""");
        Assert.NotNull(FilterSet.Load(path).For("Lead"));
    }

    // ── Rejection matrix — malformed configs must throw, never be ignored ────

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"objects": []}""")]
    [InlineData("""{"objects": {"Contact": "nope"}}""")]
    [InlineData("""{"fullScanAllowed": "Account"}""")]
    [InlineData("""{"fullScanAllowed": [42]}""")]
    public void Parse_MalformedDocuments_Throw(string json) =>
        Assert.Throws<InvalidDataException>(() => FilterSet.Parse(json));

    [Fact]
    public void Parse_UnknownOperator_Throws()
    {
        var exc = Assert.Throws<InvalidDataException>(() => FilterSet.Parse(
            """{"objects": {"Contact": {"allOf": [{"field": "A", "op": "startsWith", "value": "x"}]}}}"""));
        Assert.Contains("unknown operator 'startsWith'", exc.Message);
    }

    [Fact]
    public void Parse_MissingField_Throws() =>
        Assert.Throws<InvalidDataException>(() => FilterSet.Parse(
            """{"objects": {"Contact": {"allOf": [{"op": "equals", "value": "x"}]}}}"""));

    [Fact]
    public void Parse_MissingOp_Throws() =>
        Assert.Throws<InvalidDataException>(() => FilterSet.Parse(
            """{"objects": {"Contact": {"allOf": [{"field": "A", "value": "x"}]}}}"""));

    [Theory]
    // in/notIn need values[]
    [InlineData("""{"field": "A", "op": "in"}""")]
    [InlineData("""{"field": "A", "op": "notIn", "values": []}""")]
    // between needs exactly two values
    [InlineData("""{"field": "A", "op": "between", "values": ["1"]}""")]
    [InlineData("""{"field": "A", "op": "between", "values": ["1", "2", "3"]}""")]
    // scalar ops need a value
    [InlineData("""{"field": "A", "op": "equals"}""")]
    [InlineData("""{"field": "A", "op": "gte"}""")]
    // withinLastDays needs a non-negative integer
    [InlineData("""{"field": "A", "op": "withinLastDays", "value": "soon"}""")]
    [InlineData("""{"field": "A", "op": "withinLastDays", "value": "-3"}""")]
    public void Parse_BadOperandArity_Throws(string predicate) =>
        Assert.Throws<InvalidDataException>(() => FilterSet.Parse(
            """{"objects": {"Contact": {"allOf": [""" + predicate + "]}}}"));

    [Fact]
    public void Parse_IsNullNeedsNoOperand()
    {
        var set = FilterSet.Parse(
            """{"objects": {"Contact": {"allOf": [{"field": "A", "op": "isNull"}]}}}""");
        Assert.Equal(FilterOp.IsNull, set.For("Contact")!.AnyOf[0].AllOf[0].Op);
    }

    [Fact]
    public void Parse_UnknownObjectKey_Throws()
    {
        // A typo like "filters" must not silently produce an unfiltered object.
        var exc = Assert.Throws<InvalidDataException>(() => FilterSet.Parse(
            """{"objects": {"Contact": {"predicates": []}}}"""));
        Assert.Contains("unknown key 'predicates'", exc.Message);
    }

    [Fact]
    public void Parse_BothAnyOfAndAllOf_Throws() =>
        Assert.Throws<InvalidDataException>(() => FilterSet.Parse(
            """
            {"objects": {"Contact": {
                "anyOf": [{"allOf": [{"field": "A", "op": "isNull"}]}],
                "allOf": [{"field": "B", "op": "isNull"}]
            }}}
            """));

    [Fact]
    public void Parse_EmptyAnyOfGroup_Throws() =>
        Assert.Throws<InvalidDataException>(() => FilterSet.Parse(
            """{"objects": {"Contact": {"anyOf": [{"allOf": []}]}}}"""));

    [Fact]
    public void Parse_AnyOfEntryWithoutAllOf_Throws() =>
        Assert.Throws<InvalidDataException>(() => FilterSet.Parse(
            """{"objects": {"Contact": {"anyOf": [{"field": "A", "op": "isNull"}]}}}"""));

    [Fact]
    public void Parse_PartitionOnlyFilter_CountsAsFiltered()
    {
        var set = FilterSet.Parse(
            """{"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}}}""");
        Assert.True(set.For("Contact")!.HasAnyFilter);
        Assert.Empty(set.For("Contact")!.AnyOf);
    }

    [Fact]
    public void Parse_ObjectWithNoPredicates_IsUnfiltered()
    {
        var set = FilterSet.Parse("""{"objects": {"Contact": {}}}""");
        Assert.False(set.For("Contact")!.HasAnyFilter);
    }

    [Fact]
    public void TryParseOp_CoversAliases()
    {
        Assert.True(FilterSet.TryParseOp(">=", out var gte));
        Assert.Equal(FilterOp.Gte, gte);
        Assert.True(FilterSet.TryParseOp("<=", out var lte));
        Assert.Equal(FilterOp.Lte, lte);
        Assert.False(FilterSet.TryParseOp("regex", out _));
    }

    [Fact]
    public void ShippedFiltersJson_IsValid_AndCoversEveryShippedObject()
    {
        // The repo's own config must always load, and every schema object must
        // carry a filter (the shipped default never relies on fullScanAllowed).
        var root = FindRepoRoot();
        var filters = FilterSet.Load(Path.Combine(root, "config", "filters.json"));
        var schema = Config.SchemaConfig.Load(Path.Combine(root, "config", "schema.json"));
        foreach (var obj in schema.ObjectList)
        {
            var filter = filters.For(obj.ObjectName);
            Assert.True(filter is { HasAnyFilter: true },
                $"shipped filters.json must filter '{obj.ObjectName}'");
        }
    }

    internal static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "HadoopConnector.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
