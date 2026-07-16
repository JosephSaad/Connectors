// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the LOG_FORMAT=json structured-logging switch in
// Infrastructure/Logging.cs (#10). Verifies:
//   * the JSON formatter shape for a sample record (with and without exception);
//   * that default (text) mode is byte-identical to the historical format;
//   * that MessageOnly handlers (the progress console) stay bare in json mode;
//   * that LOG_FORMAT=json is picked up lazily from the environment.

using System.Globalization;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

/// <summary>
/// A handler that captures the exact string <see cref="LogHandler.Format"/>
/// produces, so tests can assert on the rendered line for either format mode.
/// </summary>
file sealed class FormatCapturingHandler : LogHandler
{
    public readonly List<string> Lines = new();

    // Expose the protected Format for a record without going through a stream.
    public string Render(LogRecord record) => Format(record);

    protected override void Emit(LogRecord record) => Lines.Add(Format(record));
}

/// <summary>
/// Touches the process-global <c>LOG_FORMAT</c> env var and
/// <c>Logging.JsonFormat</c>; joins the "EnvVars" collection and restores both.
/// </summary>
[Collection("EnvVars")]
public sealed class LogFormatTests : IDisposable
{
    private readonly string? _savedEnv;

    public LogFormatTests()
    {
        _savedEnv = Environment.GetEnvironmentVariable("LOG_FORMAT");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOG_FORMAT", _savedEnv);
        Logging.ResetJsonFormatCache();
    }

    private static LogRecord SampleRecord(DateTime ts, Exception? ex = null) => new()
    {
        Name = "salesforce_connector",
        Level = LogLevels.Info,
        Message = "Starting ingestion process...",
        Exception = ex,
        Timestamp = ts,
    };

    // ── Default text mode is byte-identical ──────────────────────────────────

    [Fact]
    public void DefaultTextModeIsByteIdentical()
    {
        Logging.JsonFormat = false;
        try
        {
            var ts = new DateTime(2026, 7, 2, 14, 3, 11, 254, DateTimeKind.Local);
            var handler = new FormatCapturingHandler();
            var rendered = handler.Render(SampleRecord(ts));

            var expectedAsctime = ts.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture);
            var expected = $"{expectedAsctime} - salesforce_connector - INFO - Starting ingestion process...";
            Assert.Equal(expected, rendered);
        }
        finally
        {
            Logging.JsonFormat = false;
        }
    }

    [Fact]
    public void DefaultTextModeWithExceptionIsByteIdentical()
    {
        Logging.JsonFormat = false;
        try
        {
            var ts = new DateTime(2026, 7, 2, 14, 3, 11, 254, DateTimeKind.Local);
            var ex = new InvalidOperationException("boom");
            var handler = new FormatCapturingHandler();
            var rendered = handler.Render(SampleRecord(ts, ex));

            var asctime = ts.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture);
            // Historical behavior: message + "\n" + exception.ToString(), then the prefix.
            var expected = $"{asctime} - salesforce_connector - INFO - Starting ingestion process...\n{ex}";
            Assert.Equal(expected, rendered);
        }
        finally
        {
            Logging.JsonFormat = false;
        }
    }

    [Fact]
    public void MessageOnlyIsBareInBothModes()
    {
        var ts = new DateTime(2026, 7, 2, 14, 3, 11, 254, DateTimeKind.Local);
        var record = SampleRecord(ts);

        Logging.JsonFormat = false;
        try
        {
            var textHandler = new FormatCapturingHandler { MessageOnly = true };
            Assert.Equal("Starting ingestion process...", textHandler.Render(record));

            Logging.JsonFormat = true;
            var jsonHandler = new FormatCapturingHandler { MessageOnly = true };
            // Progress-console output must NOT become JSON.
            Assert.Equal("Starting ingestion process...", jsonHandler.Render(record));
        }
        finally
        {
            Logging.JsonFormat = false;
        }
    }

    // ── JSON mode shape ──────────────────────────────────────────────────────

    [Fact]
    public void JsonModeEmitsExpectedShape()
    {
        Logging.JsonFormat = true;
        try
        {
            var ts = new DateTime(2026, 7, 2, 14, 3, 11, 254, DateTimeKind.Local);
            var handler = new FormatCapturingHandler();
            var rendered = handler.Render(SampleRecord(ts));

            // Single line (no embedded raw newline).
            Assert.DoesNotContain("\n", rendered);

            var obj = JsonNode.Parse(rendered)!.AsObject();
            Assert.Equal(
                ts.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture),
                obj["timestamp"]!.GetValue<string>());
            Assert.Equal("INFO", obj["level"]!.GetValue<string>());
            Assert.Equal("salesforce_connector", obj["logger"]!.GetValue<string>());
            Assert.Equal("Starting ingestion process...", obj["message"]!.GetValue<string>());
            Assert.False(obj.ContainsKey("exception"));
        }
        finally
        {
            Logging.JsonFormat = false;
        }
    }

    [Fact]
    public void JsonModeIncludesExceptionTypeAndMessage()
    {
        Logging.JsonFormat = true;
        try
        {
            var ts = new DateTime(2026, 7, 2, 14, 3, 11, 254, DateTimeKind.Local);
            var ex = new InvalidOperationException("kaput");
            var handler = new FormatCapturingHandler();
            var rendered = handler.Render(SampleRecord(ts, ex));

            var obj = JsonNode.Parse(rendered)!.AsObject();
            var exObj = obj["exception"]!.AsObject();
            Assert.Equal("System.InvalidOperationException", exObj["type"]!.GetValue<string>());
            Assert.Equal("kaput", exObj["message"]!.GetValue<string>());
            // The message field itself is the plain message (no appended stack).
            Assert.Equal("Starting ingestion process...", obj["message"]!.GetValue<string>());
        }
        finally
        {
            Logging.JsonFormat = false;
        }
    }

    [Fact]
    public void JsonModeEscapesQuotesAndControlChars()
    {
        Logging.JsonFormat = true;
        try
        {
            var record = new LogRecord
            {
                Name = "salesforce_connector",
                Level = LogLevels.Warning,
                Message = "quote \" and\ttab and newline\n end",
                Timestamp = DateTime.Now,
            };
            var handler = new FormatCapturingHandler();
            var rendered = handler.Render(record);

            // Must remain a single physical line and round-trip to the original message.
            Assert.DoesNotContain("\n", rendered.TrimEnd('\n'));
            var obj = JsonNode.Parse(rendered)!.AsObject();
            Assert.Equal("quote \" and\ttab and newline\n end", obj["message"]!.GetValue<string>());
        }
        finally
        {
            Logging.JsonFormat = false;
        }
    }

    // ── Env-var resolution ───────────────────────────────────────────────────

    [Fact]
    public void JsonFormatResolvesFromEnvLazily()
    {
        Environment.SetEnvironmentVariable("LOG_FORMAT", "json");
        Logging.ResetJsonFormatCache();
        Assert.True(Logging.JsonFormat);

        Environment.SetEnvironmentVariable("LOG_FORMAT", "text");
        Logging.ResetJsonFormatCache();
        Assert.False(Logging.JsonFormat);

        Environment.SetEnvironmentVariable("LOG_FORMAT", null);
        Logging.ResetJsonFormatCache();
        Assert.False(Logging.JsonFormat);

        // Case-insensitive.
        Environment.SetEnvironmentVariable("LOG_FORMAT", "JSON");
        Logging.ResetJsonFormatCache();
        Assert.True(Logging.JsonFormat);
    }
}
