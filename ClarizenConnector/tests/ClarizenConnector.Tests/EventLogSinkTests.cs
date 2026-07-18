// EventLogSinkTests.cs — dispatch rules for the optional Windows Event Log
// mirror (EVENTLOG_ENABLED / EVENTLOG_LEVEL), asserted through the
// IEventLogWriter seam so no test touches a real event log.

using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

/// <summary>Recording writer; optionally throws to exercise the disable latch.</summary>
public sealed class FakeEventLogWriter : IEventLogWriter
{
    public List<(string Message, EventLogEntryKind Kind, int EventId)> Entries { get; } = new();

    public Exception? ThrowOnWrite { get; set; }

    public void Write(string message, EventLogEntryKind kind, int eventId)
    {
        if (ThrowOnWrite is not null)
            throw ThrowOnWrite;
        Entries.Add((message, kind, eventId));
    }
}

public class EventLogSinkTests : IDisposable
{
    public EventLogSinkTests() => EventLogSink.ResetForTests();

    public void Dispose() => EventLogSink.ResetForTests();

    [Fact]
    public void Mirror_ErrorAndWarning_DispatchAtDefaultLevel()
    {
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null));
        var writer = new FakeEventLogWriter();
        EventLogSink.OverrideWriter(writer);

        EventLogSink.Mirror(LogLevel.Error, "clarizen_connector.graph", "boom");
        EventLogSink.Mirror(LogLevel.Warning, "clarizen_connector", "careful");

        Assert.Equal(2, writer.Entries.Count);
        Assert.Equal(EventLogEntryKind.Error, writer.Entries[0].Kind);
        Assert.Equal(EventLogSink.EventIdError, writer.Entries[0].EventId);
        Assert.Contains("clarizen_connector.graph: boom", writer.Entries[0].Message);
        Assert.Equal(EventLogEntryKind.Warning, writer.Entries[1].Kind);
        Assert.Equal(EventLogSink.EventIdWarning, writer.Entries[1].EventId);
    }

    [Fact]
    public void Mirror_Info_RequiresEventLogLevelInfo()
    {
        var writer = new FakeEventLogWriter();
        EventLogSink.OverrideWriter(writer);

        using (new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null)))
        {
            EventLogSink.Mirror(LogLevel.Info, "clarizen_connector", "chatty");
            Assert.Empty(writer.Entries);  // Info dropped at the default mirror level
        }

        using (new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", "info")))
        {
            EventLogSink.Mirror(LogLevel.Info, "clarizen_connector", "chatty");
            var entry = Assert.Single(writer.Entries);
            Assert.Equal(EventLogEntryKind.Information, entry.Kind);
            Assert.Equal(EventLogSink.EventIdInfo, entry.EventId);

            // Debug is never mirrored, even with EVENTLOG_LEVEL=info.
            EventLogSink.Mirror(LogLevel.Debug, "clarizen_connector", "trace");
            Assert.Single(writer.Entries);
        }
    }

    [Fact]
    public void Mirror_DisabledByDefault_NothingDispatched()
    {
        using var env = new EnvScope(("EVENTLOG_ENABLED", null));
        var writer = new FakeEventLogWriter();
        EventLogSink.OverrideWriter(writer);

        EventLogSink.Mirror(LogLevel.Error, "clarizen_connector", "boom");
        Assert.Empty(writer.Entries);
    }

    [Fact]
    public void Logger_Wiring_ErrorAndWarningReachTheSink()
    {
        // Through the REAL logging path (Logging.Write → EventLogSink.Mirror),
        // proving the interface wiring, not just the sink in isolation.
        using var env = new EnvScope(
            ("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null), ("LOG_LEVEL", null));
        var writer = new FakeEventLogWriter();
        EventLogSink.OverrideWriter(writer);
        var logger = Logging.GetLogger("clarizen_connector.eventlogtest");

        logger.Error("mirrored error");
        logger.Warning("mirrored warning");
        logger.Info("not mirrored at default level");

        Assert.Equal(2, writer.Entries.Count);
        Assert.Equal(EventLogSink.EventIdError, writer.Entries[0].EventId);
        Assert.Contains("clarizen_connector.eventlogtest: mirrored error", writer.Entries[0].Message);
        Assert.Equal(EventLogSink.EventIdWarning, writer.Entries[1].EventId);
    }

    [Fact]
    public void ServiceLifecycle_AlwaysDispatches_WithDedicatedEventIds()
    {
        // Lifecycle events bypass EVENTLOG_LEVEL (default level here) so SIEMs
        // always see service start/stop transitions.
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null));
        var writer = new FakeEventLogWriter();
        EventLogSink.OverrideWriter(writer);

        EventLogSink.ServiceLifecycle("service starting", starting: true);
        EventLogSink.ServiceLifecycle("service stopping", starting: false);

        Assert.Equal(2, writer.Entries.Count);
        Assert.All(writer.Entries, e => Assert.Equal(EventLogEntryKind.Information, e.Kind));
        Assert.Equal(EventLogSink.EventIdServiceStart, writer.Entries[0].EventId);
        Assert.Equal(EventLogSink.EventIdServiceStop, writer.Entries[1].EventId);
    }

    [Fact]
    public void Mirror_WriterFailure_DisablesSinkAndNeverThrows()
    {
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"));
        var writer = new FakeEventLogWriter { ThrowOnWrite = new InvalidOperationException("no log") };
        EventLogSink.OverrideWriter(writer);

        // Must not throw into the logging path…
        EventLogSink.Mirror(LogLevel.Error, "clarizen_connector", "boom");

        // …and the first failure latches the sink off for the process lifetime.
        writer.ThrowOnWrite = null;
        EventLogSink.Mirror(LogLevel.Error, "clarizen_connector", "boom again");
        Assert.Empty(writer.Entries);
    }

    [Fact]
    public void Mirror_OversizeMessage_IsTruncatedBelowEventLogCap()
    {
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"));
        var writer = new FakeEventLogWriter();
        EventLogSink.OverrideWriter(writer);

        EventLogSink.Mirror(LogLevel.Error, "clarizen_connector", new string('x', 40_000));

        var entry = Assert.Single(writer.Entries);
        Assert.True(entry.Message.Length < 31_839, "entry must fit the Event Log message cap");
        Assert.EndsWith("[truncated]", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultWriter_OffWindows_IsAStrictNoOp()
    {
        if (OperatingSystem.IsWindows())
            return;  // the no-op contract is for non-Windows hosts

        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"));
        // No override writer: the sink resolves the DEFAULT writer, which must
        // be null off Windows — nothing to assert beyond "does not throw".
        EventLogSink.Mirror(LogLevel.Error, "clarizen_connector", "boom");
        EventLogSink.ServiceLifecycle("start", starting: true);
    }
}
