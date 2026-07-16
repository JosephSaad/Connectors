using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Tests;

public class TdwBulkReaderTests
{
    [Fact]
    public void ParseCsv_HeaderAndRows()
    {
        var records = TdwBulkReader.ParseCsv("id,Name,State\n/Task/1,Alpha,Active\n/Task/2,Beta,Draft\n");
        Assert.Equal(2, records.Count);
        Assert.Equal("/Task/1", records[0]["id"]!.GetValue<string>());
        Assert.Equal("Beta", records[1]["Name"]!.GetValue<string>());
        Assert.Equal("Draft", records[1]["State"]!.GetValue<string>());
    }

    [Fact]
    public void ParseCsv_QuotedFieldsWithCommasAndEscapedQuotes()
    {
        var csv = "id,Name,Description\n"
                  + "/Task/1,\"Phase 1, kickoff\",\"He said \"\"go\"\"\"\n";
        var records = TdwBulkReader.ParseCsv(csv);
        var record = Assert.Single(records);
        Assert.Equal("Phase 1, kickoff", record["Name"]!.GetValue<string>());
        Assert.Equal("He said \"go\"", record["Description"]!.GetValue<string>());
    }

    [Fact]
    public void ParseCsv_EmbeddedNewlineInsideQuotes()
    {
        var csv = "id,Notes\n/Task/1,\"line one\nline two\"\n/Task/2,plain\n";
        var records = TdwBulkReader.ParseCsv(csv);
        Assert.Equal(2, records.Count);
        Assert.Equal("line one\nline two", records[0]["Notes"]!.GetValue<string>());
    }

    [Fact]
    public void ParseCsv_MissingTrailingFields_AreEmpty_AndEmptyBecomesNull()
    {
        var records = TdwBulkReader.ParseCsv("id,Name,State\n/Task/1,OnlyName\n/Task/2,,Active\n");
        Assert.Equal(2, records.Count);
        Assert.Null(records[0]["State"]);
        Assert.Null(records[1]["Name"]);
    }

    [Fact]
    public void ParseCsv_CrLfLineEndings()
    {
        var records = TdwBulkReader.ParseCsv("id,Name\r\n/Task/1,Alpha\r\n");
        var record = Assert.Single(records);
        Assert.Equal("Alpha", record["Name"]!.GetValue<string>());
    }

    [Fact]
    public void ParseJson_BareArray()
    {
        var records = TdwBulkReader.ParseJson("""[{"id": "/Task/1", "Name": "A"}, {"id": "/Task/2"}]""");
        Assert.Equal(2, records.Count);
        Assert.Equal("A", records[0]["Name"]!.GetValue<string>());
    }

    [Fact]
    public void ParseJson_EntitiesWrapper()
    {
        var records = TdwBulkReader.ParseJson("""{"entities": [{"id": "/Project/9"}]}""");
        Assert.Equal("/Project/9", Assert.Single(records)["id"]!.GetValue<string>());
    }

    [Fact]
    public void ParseJson_InvalidShape_Throws()
    {
        Assert.Throws<InvalidDataException>(() => TdwBulkReader.ParseJson("""{"nope": 1}"""));
    }

    [Fact]
    public void FindFile_PrefersJsonOverCsv_AndHasExport()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "Task.csv"), "id\n/Task/1\n");
        File.WriteAllText(Path.Combine(dir.Path, "Task.json"), "[]");
        var reader = new TdwBulkReader(dir.Path);

        Assert.True(reader.HasExport("Task"));
        Assert.False(reader.HasExport("Project"));
        Assert.EndsWith("Task.json", reader.FindFile("Task"));
    }

    [Fact]
    public void ReadObject_CsvFile_EndToEnd()
    {
        using var dir = new TempDir();
        File.WriteAllText(
            Path.Combine(dir.Path, "Project.csv"),
            "id,Name,PlannedCost\n/Project/1,Apollo,120000\n");
        var reader = new TdwBulkReader(dir.Path);
        var records = reader.ReadObject("Project");
        var record = Assert.Single(records);
        Assert.Equal("Apollo", record["Name"]!.GetValue<string>());
    }

    [Fact]
    public void ReadObject_MissingFile_Throws()
    {
        using var dir = new TempDir();
        var reader = new TdwBulkReader(dir.Path);
        Assert.Throws<FileNotFoundException>(() => reader.ReadObject("Ghost"));
    }
}

/// <summary>
/// Regression: a TDW export with no recognized id column used to collapse
/// every row to the single item id "{Object}_" (each row overwriting the same
/// Graph item, and the deletion sweep seeing every real id as stale). Such an
/// export must be rejected with a hard error; individual id-less rows are
/// dropped and logged, never ingested.
/// </summary>
public class SourceRecordIdValidationTests
{
    private static ClarizenRecord Record(string objectType, string? id, string name = "n")
    {
        var fields = new JsonObject { ["Name"] = name };
        if (id is not null)
            fields["id"] = id;
        return new ClarizenRecord(objectType, fields);
    }

    [Fact]
    public void AllRecordsMissingId_RejectedWithHardError()
    {
        var records = new List<ClarizenRecord> { Record("Task", null), Record("Task", null) };
        var exc = Assert.Throws<InvalidDataException>(
            () => IngestPipeline.ValidateSourceRecordIds("Task", records, "TDW export"));
        Assert.Contains("no recognized id column", exc.Message);
    }

    [Fact]
    public void RecordsWithEmptyId_Dropped_OthersKept()
    {
        var records = new List<ClarizenRecord>
        {
            Record("Task", "/Task/1"),
            Record("Task", null),          // no id property at all
            Record("Task", "/Task/2"),
        };
        var kept = IngestPipeline.ValidateSourceRecordIds("Task", records, "TDW export");
        Assert.Equal(new[] { "Task_1", "Task_2" }, kept.Select(r => r.ItemId));
    }

    [Fact]
    public void EmptyBatch_PassesThrough()
    {
        var kept = IngestPipeline.ValidateSourceRecordIds(
            "Task", new List<ClarizenRecord>(), "TDW export");
        Assert.Empty(kept);
    }

    [Fact]
    public async Task TdwExport_WithoutIdColumn_RejectedBeforeIngest()
    {
        // End-to-end through the TDW fetch path: a CSV whose header has no
        // id/Id column must be rejected, not ingested as one collapsed item.
        using var dir = new TempDir();
        File.WriteAllText(
            Path.Combine(dir.Path, "Task.csv"),
            "Name,State\nAlpha,Active\nBeta,Draft\n");

        var config = TestConfig.Make(tdwExportPath: dir.Path);
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}"""));
        var clarizen = new ClarizenClient(
            config, new ApiBudget(100_000, callsPerMinute: 6_000_000), handler);
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Task",
            SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IngestPipeline.FetchSourceRecordsAsync(
                config, clarizen, objectConfig, fullCrawl: true, since: null, CancellationToken.None));
    }

    [Fact]
    public async Task TdwExport_WithIdColumn_PartialRows_DropsOnlyIdless()
    {
        using var dir = new TempDir();
        File.WriteAllText(
            Path.Combine(dir.Path, "Task.csv"),
            "id,Name\n/Task/1,Alpha\n,NoIdRow\n/Task/2,Beta\n");

        var config = TestConfig.Make(tdwExportPath: dir.Path);
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}"""));
        var clarizen = new ClarizenClient(
            config, new ApiBudget(100_000, callsPerMinute: 6_000_000), handler);
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Task",
            SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
        };

        var records = await IngestPipeline.FetchSourceRecordsAsync(
            config, clarizen, objectConfig, fullCrawl: true, since: null, CancellationToken.None);

        Assert.Equal(new[] { "Task_1", "Task_2" }, records.Select(r => r.ItemId));
    }
}
