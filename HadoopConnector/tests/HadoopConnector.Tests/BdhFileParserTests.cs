// BdhFileParserTests.cs
// ---------------------
// Hardened streaming parser: CSV quoting/escaping, JSONL malformed-line
// accounting, JSON array/entities envelopes, byte bounds (BoundedStream),
// short/long rows, blank lines, file-type detection.

using System.Text;
using HadoopConnector.Hdfs;

namespace HadoopConnector.Tests;

public class BdhFileParserTests
{
    private static List<System.Text.Json.Nodes.JsonObject> Parse(
        string body, string fileName, long maxBytes = 1024 * 1024, BdhFileParser? parser = null)
    {
        parser ??= new BdhFileParser();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return parser.Parse(stream, fileName, maxBytes).ToList();
    }

    // ── File-type detection ──────────────────────────────────────────────────

    [Theory]
    [InlineData("part-0000.csv", true)]
    [InlineData("part-0000.jsonl", true)]
    [InlineData("part-0000.json", true)]
    [InlineData("part-0000.CSV", true)]
    [InlineData("part-0000.parquet", false)]
    [InlineData("part-0000.orc", false)]
    public void IsDataFile_Matrix(string name, bool expected) =>
        Assert.Equal(expected, BdhFileParser.IsDataFile(name));

    [Theory]
    [InlineData("_SUCCESS", true)]
    [InlineData(".part-0000.jsonl.crc", true)]
    [InlineData("part-0000.jsonl.tmp", true)]
    [InlineData("part-0000.jsonl", false)]
    public void IsIgnorableEntry_Matrix(string name, bool expected) =>
        Assert.Equal(expected, BdhFileParser.IsIgnorableEntry(name));

    // ── CSV ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_HeaderDrivesFieldNames()
    {
        var rows = Parse("Id,Name,Status\n001,Acme,Active\n002,Beta,Closed\n", "f.csv");
        Assert.Equal(2, rows.Count);
        Assert.Equal("001", rows[0]["Id"]!.GetValue<string>());
        Assert.Equal("Closed", rows[1]["Status"]!.GetValue<string>());
    }

    [Fact]
    public void Csv_QuotedFields_CommasNewlinesEscapes()
    {
        var body = "Id,Description\n" +
                   "001,\"Line one\nLine two, with comma and \"\"quotes\"\"\"\n";
        var rows = Parse(body, "f.csv");
        var description = Assert.Single(rows)["Description"]!.GetValue<string>();
        Assert.Equal("Line one\nLine two, with comma and \"quotes\"", description);
    }

    [Fact]
    public void Csv_CrLfLineEndings()
    {
        var rows = Parse("Id,Name\r\n001,Acme\r\n002,Beta\r\n", "f.csv");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Beta", rows[1]["Name"]!.GetValue<string>());
    }

    [Fact]
    public void Csv_MissingTrailingFields_AreNull()
    {
        var rows = Parse("Id,Name,Status\n001,Acme\n", "f.csv");
        Assert.Null(Assert.Single(rows)["Status"]);
    }

    [Fact]
    public void Csv_EmptyFields_AreNull()
    {
        var rows = Parse("Id,Name\n001,\n", "f.csv");
        Assert.Null(Assert.Single(rows)["Name"]);
    }

    [Fact]
    public void Csv_RowWithTooManyFields_IsSkippedAndCounted()
    {
        var parser = new BdhFileParser();
        var rows = Parse("Id,Name\n001,Acme,EXTRA,EXTRA\n002,Beta\n", "f.csv", parser: parser);
        Assert.Single(rows);
        Assert.Equal("002", rows[0]["Id"]!.GetValue<string>());
        Assert.Equal(1, parser.ParseErrors);
    }

    [Fact]
    public void Csv_TrailingBlankLine_Ignored()
    {
        var rows = Parse("Id\n001\n\n", "f.csv");
        Assert.Single(rows);
    }

    [Fact]
    public void Csv_EmptyFile_YieldsNothing()
    {
        Assert.Empty(Parse(string.Empty, "f.csv"));
    }

    [Fact]
    public void Csv_NoFinalNewline_LastRowKept()
    {
        var rows = Parse("Id,Name\n001,Acme", "f.csv");
        Assert.Equal("Acme", Assert.Single(rows)["Name"]!.GetValue<string>());
    }

    // ── JSONL ────────────────────────────────────────────────────────────────

    [Fact]
    public void Jsonl_OneObjectPerLine()
    {
        var rows = Parse("""{"Id":"001","Name":"Acme"}""" + "\n" + """{"Id":"002"}""" + "\n", "f.jsonl");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Acme", rows[0]["Name"]!.GetValue<string>());
    }

    [Fact]
    public void Jsonl_MalformedLines_SkippedAndCounted()
    {
        var parser = new BdhFileParser();
        var body = """{"Id":"001"}""" + "\n" +
                   "{ not json\n" +
                   "[1,2,3]\n" +                 // an array line is not an object
                   """{"Id":"002"}""" + "\n";
        var rows = Parse(body, "f.jsonl", parser: parser);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, parser.ParseErrors);
    }

    [Fact]
    public void Jsonl_BlankLines_Ignored()
    {
        var parser = new BdhFileParser();
        var rows = Parse("\n" + """{"Id":"001"}""" + "\n\n", "f.jsonl", parser: parser);
        Assert.Single(rows);
        Assert.Equal(0, parser.ParseErrors);
    }

    // ── JSON array ───────────────────────────────────────────────────────────

    [Fact]
    public void Json_BareArray()
    {
        var rows = Parse("""[{"Id":"001"},{"Id":"002"}]""", "f.json");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Json_EntitiesEnvelope()
    {
        var rows = Parse("""{"entities": [{"Id":"001"}]}""", "f.json");
        Assert.Single(rows);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("\"a string\"")]
    [InlineData("""{"rows": []}""")]
    public void Json_InvalidShapes_Throw(string body) =>
        Assert.Throws<InvalidDataException>(() => Parse(body, "f.json"));

    // ── Byte bound (BoundedStream) ───────────────────────────────────────────

    [Fact]
    public void BoundedStream_ThrowsPastTheCap()
    {
        var body = "Id\n" + string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"row{i}"));
        Assert.Throws<InvalidDataException>(() =>
            Parse(body, "f.csv", maxBytes: 64));
    }

    [Fact]
    public void BoundedStream_ExactSizeIsFine()
    {
        var body = "Id\n001\n";
        var rows = Parse(body, "f.csv", maxBytes: Encoding.UTF8.GetByteCount(body));
        Assert.Single(rows);
    }

    [Fact]
    public async Task BoundedStream_AsyncReadAccountsToo()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 100));
        await using var bounded = new BoundedStream(new MemoryStream(bytes), maxBytes: 50);
        var buffer = new byte[64];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            while (await bounded.ReadAsync(buffer.AsMemory()) > 0)
            {
            }
        });
    }

    [Fact]
    public void BoundedStream_IsReadOnly()
    {
        using var bounded = new BoundedStream(new MemoryStream(), 10);
        Assert.False(bounded.CanWrite);
        Assert.False(bounded.CanSeek);
        Assert.Throws<NotSupportedException>(() => bounded.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
    }
}
