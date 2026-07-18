// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for Infrastructure/EventLogSink.cs — Windows Event Log mirroring.
// The OS write is behind the IEventLogWriter seam, so dispatch, level mapping,
// the INFO opt-in, lifecycle-logger handling, truncation and the never-throw
// contract are all asserted without a Windows event log.

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

/// <summary>
/// Touches process-global state (EVENTLOG_* env vars, Logging.Root handlers,
/// Logging.JsonFormat) — joins the "EnvVars" collection and restores everything.
/// </summary>
[Collection("EnvVars")]
public sealed class EventLogSinkTests : IDisposable
{
    private sealed class FakeEventLogWriter : IEventLogWriter
    {
        public readonly List<(EventLogEntrySeverity Severity, string Message, int EventId)> Entries = new();
        public Exception? ThrowOnWrite { get; set; }
        public int WriteAttempts { get; private set; }

        public void Write(EventLogEntrySeverity severity, string message, int eventId)
        {
            WriteAttempts++;
            if (ThrowOnWrite != null)
                throw ThrowOnWrite;
            Entries.Add((severity, message, eventId));
        }
    }

    private readonly string? _savedEnabled = Environment.GetEnvironmentVariable("EVENTLOG_ENABLED");
    private readonly string? _savedLevel = Environment.GetEnvironmentVariable("EVENTLOG_LEVEL");

    public EventLogSinkTests()
    {
        Logging.JsonFormat = false;  // assert against the deterministic text format
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EVENTLOG_ENABLED", _savedEnabled);
        Environment.SetEnvironmentVariable("EVENTLOG_LEVEL", _savedLevel);
        EventLogSink.DetachForTests();
        Logging.ResetJsonFormatCache();
    }

    private static LogRecord Record(int level, string message = "boom", string name = "salesforce_connector") =>
        new() { Name = name, Level = level, Message = message };

    private static (EventLogSink Sink, FakeEventLogWriter Writer) NewSink(bool mirrorInfo = false)
    {
        var writer = new FakeEventLogWriter();
        return (new EventLogSink(writer, mirrorInfo), writer);
    }

    // ── Dispatch and level mapping ───────────────────────────────────────────

    [Fact]
    public void ErrorIsMirroredAsErrorEntry()
    {
        var (sink, writer) = NewSink();
        sink.Handle(Record(LogLevels.Error, "ingest crashed"));

        var entry = Assert.Single(writer.Entries);
        Assert.Equal(EventLogEntrySeverity.Error, entry.Severity);
        Assert.Equal(EventLogSink.EventIdError, entry.EventId);
        Assert.Contains("ingest crashed", entry.Message);
        Assert.Contains("ERROR", entry.Message);  // full formatted line, not bare text
    }

    [Fact]
    public void WarningIsMirroredAsWarningEntry()
    {
        var (sink, writer) = NewSink();
        sink.Handle(Record(LogLevels.Warning, "429 storm"));

        var entry = Assert.Single(writer.Entries);
        Assert.Equal(EventLogEntrySeverity.Warning, entry.Severity);
        Assert.Equal(EventLogSink.EventIdWarning, entry.EventId);
    }

    [Fact]
    public void CriticalMapsToErrorEntry()
    {
        var (sink, writer) = NewSink();
        sink.Handle(Record(LogLevels.Critical));
        Assert.Equal(EventLogEntrySeverity.Error, Assert.Single(writer.Entries).Severity);
    }

    [Fact]
    public void ExceptionDetailIsIncluded()
    {
        var (sink, writer) = NewSink();
        var record = new LogRecord
        {
            Name = "salesforce_connector",
            Level = LogLevels.Error,
            Message = "crawl failed",
            Exception = new InvalidOperationException("token expired"),
        };
        sink.Handle(record);
        Assert.Contains("token expired", Assert.Single(writer.Entries).Message);
    }

    // ── INFO policy ──────────────────────────────────────────────────────────

    [Fact]
    public void InfoIsNotMirroredByDefault()
    {
        var (sink, writer) = NewSink(mirrorInfo: false);
        sink.Handle(Record(LogLevels.Info, "chunk done"));
        sink.Handle(Record(LogLevels.Debug, "noise"));
        Assert.Empty(writer.Entries);
    }

    [Fact]
    public void InfoIsMirroredWithLevelOptIn()
    {
        var (sink, writer) = NewSink(mirrorInfo: true);  // EVENTLOG_LEVEL=info
        sink.Handle(Record(LogLevels.Info, "chunk done"));

        var entry = Assert.Single(writer.Entries);
        Assert.Equal(EventLogEntrySeverity.Information, entry.Severity);
        Assert.Equal(EventLogSink.EventIdInformation, entry.EventId);
    }

    [Fact]
    public void DebugIsNeverMirroredEvenWithOptIn()
    {
        var (sink, writer) = NewSink(mirrorInfo: true);
        sink.Handle(Record(LogLevels.Debug));
        Assert.Empty(writer.Entries);
    }

    [Fact]
    public void ServiceLifecycleInfoIsAlwaysMirrored()
    {
        var (sink, writer) = NewSink(mirrorInfo: false);
        sink.Handle(Record(
            LogLevels.Info,
            "Running as a Windows service: full-deployment --continuous",
            name: EventLogSink.ServiceLoggerName));

        var entry = Assert.Single(writer.Entries);
        Assert.Equal(EventLogEntrySeverity.Information, entry.Severity);
        Assert.Contains("Running as a Windows service", entry.Message);
    }

    // ── Never-throw contract ─────────────────────────────────────────────────

    [Fact]
    public void WriterExceptionIsSwallowedAndSubsequentRecordsStillAttempted()
    {
        var (sink, writer) = NewSink();
        writer.ThrowOnWrite = new UnauthorizedAccessException("source not registered");

        sink.Handle(Record(LogLevels.Error));   // must not throw
        writer.ThrowOnWrite = null;
        sink.Handle(Record(LogLevels.Error, "second"));

        Assert.Equal(2, writer.WriteAttempts);
        Assert.Contains("second", Assert.Single(writer.Entries).Message);
    }

    // ── Truncation ───────────────────────────────────────────────────────────

    [Fact]
    public void OversizedMessagesAreTruncatedBelowTheWindowsCap()
    {
        var (sink, writer) = NewSink();
        sink.Handle(Record(LogLevels.Error, new string('x', EventLogSink.MaxMessageLength + 5_000)));

        var entry = Assert.Single(writer.Entries);
        Assert.True(entry.Message.Length <= EventLogSink.MaxMessageLength + 20);
        Assert.EndsWith("…[truncated]", entry.Message);
    }

    // ── Env mapping and attach behavior ──────────────────────────────────────

    [Fact]
    public void EnvFlagsControlEnabledAndInfoOptIn()
    {
        Environment.SetEnvironmentVariable("EVENTLOG_ENABLED", null);
        Assert.False(EventLogSink.Enabled);
        Environment.SetEnvironmentVariable("EVENTLOG_ENABLED", "true");
        Assert.True(EventLogSink.Enabled);

        Environment.SetEnvironmentVariable("EVENTLOG_LEVEL", null);
        Assert.False(EventLogSink.MirrorInfoFromEnv);
        Environment.SetEnvironmentVariable("EVENTLOG_LEVEL", "info");
        Assert.True(EventLogSink.MirrorInfoFromEnv);
        Environment.SetEnvironmentVariable("EVENTLOG_LEVEL", "INFO");
        Assert.True(EventLogSink.MirrorInfoFromEnv);
    }

    [Fact]
    public void AttachIfEnabledIsANoOpWhenDisabled()
    {
        Environment.SetEnvironmentVariable("EVENTLOG_ENABLED", null);
        var before = Logging.Root.Handlers.Count;
        EventLogSink.AttachIfEnabled();
        Assert.Equal(before, Logging.Root.Handlers.Count);
    }

    [Fact]
    public void AttachIfEnabledRespectsPlatformAndIsIdempotent()
    {
        Environment.SetEnvironmentVariable("EVENTLOG_ENABLED", "true");
        var before = Logging.Root.Handlers.Count;

        EventLogSink.AttachIfEnabled();
        EventLogSink.AttachIfEnabled();  // second call must not duplicate

        var after = Logging.Root.Handlers.Count;
        if (OperatingSystem.IsWindows())
            Assert.Equal(before + 1, after);   // attached exactly once
        else
            Assert.Equal(before, after);       // strict no-op off Windows

        EventLogSink.DetachForTests();
        Assert.Equal(before, Logging.Root.Handlers.Count);
    }
}
