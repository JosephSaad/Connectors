using ClarizenConnector.Clarizen;

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
