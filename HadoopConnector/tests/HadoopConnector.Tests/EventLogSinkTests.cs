// Windows Event Log mirroring (Infrastructure/EventLogSink.cs): dispatch
// mapping, EVENTLOG_ENABLED gating, EVENTLOG_LEVEL=info opt-in, lifecycle
// events, and the never-throws guarantee — all through the injected writer
// seam, so the suite stays offline and OS-independent.

using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class EventLogSinkTests : IDisposable
{
    private sealed class RecordingWriter : IEventLogWriter
    {
        public List<(string Message, EventLogEntryLevel Level, int EventId)> Entries { get; } = new();
        public bool Disposed { get; private set; }
        public Exception? ThrowOnWrite { get; set; }

        public void WriteEntry(string message, EventLogEntryLevel level, int eventId)
        {
            if (ThrowOnWrite is not null)
                throw ThrowOnWrite;
            Entries.Add((message, level, eventId));
        }

        public void Dispose() => Disposed = true;
    }

    public EventLogSinkTests() => EventLogSink.ResetForTests();

    public void Dispose() => EventLogSink.ResetForTests();

    private static RecordingWriter Activate(EnvScope env)
    {
        env.Set("EVENTLOG_ENABLED", "true");
        var writer = new RecordingWriter();
        EventLogSink.OverrideWriter = writer;
        EventLogSink.Initialize();
        return writer;
    }

    [Fact]
    public void DisabledByDefault_NothingMirrored()
    {
        using var env = new EnvScope(("EVENTLOG_ENABLED", null), ("EVENTLOG_LEVEL", null));
        var writer = new RecordingWriter();
        EventLogSink.OverrideWriter = writer;

        EventLogSink.Initialize();
        Assert.False(EventLogSink.Enabled);

        EventLogSink.Mirror(LogLevel.Error, "hadoop_connector", "boom");
        Assert.Empty(writer.Entries);
    }

    [Fact]
    public void Enabled_MirrorsErrorAndWarning_WithStableEventIds()
    {
        using var env = new EnvScope(("EVENTLOG_LEVEL", null));
        var writer = Activate(env);
        Assert.True(EventLogSink.Enabled);

        EventLogSink.Mirror(LogLevel.Error, "hadoop_connector.graph", "ingest failed");
        EventLogSink.Mirror(LogLevel.Warning, "hadoop_connector.webhdfs", "retrying");

        // lifecycle start + the two mirrored lines
        Assert.Equal(3, writer.Entries.Count);
        Assert.Equal(EventLogEntryLevel.Information, writer.Entries[0].Level);
        Assert.Equal(EventLogSink.EventIdLifecycleStart, writer.Entries[0].EventId);

        Assert.Equal(EventLogEntryLevel.Error, writer.Entries[1].Level);
        Assert.Equal(EventLogSink.EventIdError, writer.Entries[1].EventId);
        Assert.Contains("hadoop_connector.graph: ingest failed", writer.Entries[1].Message);

        Assert.Equal(EventLogEntryLevel.Warning, writer.Entries[2].Level);
        Assert.Equal(EventLogSink.EventIdWarning, writer.Entries[2].EventId);
    }

    [Fact]
    public void InfoAndDebug_NotMirrored_ByDefault()
    {
        using var env = new EnvScope(("EVENTLOG_LEVEL", null));
        var writer = Activate(env);

        EventLogSink.Mirror(LogLevel.Info, "hadoop_connector", "started crawl");
        EventLogSink.Mirror(LogLevel.Debug, "hadoop_connector", "chatty");

        Assert.Single(writer.Entries);  // lifecycle start only
    }

    [Fact]
    public void EventLogLevelInfo_MirrorsInfo_NeverDebug()
    {
        using var env = new EnvScope(("EVENTLOG_LEVEL", "info"));
        var writer = Activate(env);

        EventLogSink.Mirror(LogLevel.Info, "hadoop_connector", "started crawl");
        EventLogSink.Mirror(LogLevel.Debug, "hadoop_connector", "chatty");

        Assert.Equal(2, writer.Entries.Count);  // lifecycle + info
        Assert.Equal(EventLogSink.EventIdInfo, writer.Entries[1].EventId);
        Assert.Equal(EventLogEntryLevel.Information, writer.Entries[1].Level);
    }

    [Fact]
    public void Shutdown_WritesLifecycleStop_AndDeactivates()
    {
        using var env = new EnvScope(("EVENTLOG_LEVEL", null));
        var writer = Activate(env);

        EventLogSink.Shutdown();
        Assert.False(EventLogSink.Enabled);
        Assert.Equal(EventLogSink.EventIdLifecycleStop, writer.Entries[^1].EventId);

        // After shutdown nothing is mirrored, and shutdown is idempotent.
        EventLogSink.Mirror(LogLevel.Error, "x", "late");
        EventLogSink.Shutdown();
        Assert.Equal(2, writer.Entries.Count);
        // The injected writer is owned by the test, never disposed by the sink.
        Assert.False(writer.Disposed);
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        using var env = new EnvScope(("EVENTLOG_LEVEL", null));
        var writer = Activate(env);
        EventLogSink.Initialize();
        EventLogSink.Initialize();
        Assert.Single(writer.Entries);  // exactly one lifecycle start
    }

    [Fact]
    public void ThrowingWriter_NeverPropagates()
    {
        using var env = new EnvScope(("EVENTLOG_LEVEL", null));
        var writer = Activate(env);
        writer.ThrowOnWrite = new InvalidOperationException("event log full");

        // Neither the mirror path nor shutdown may throw.
        EventLogSink.Mirror(LogLevel.Error, "hadoop_connector", "boom");
        EventLogSink.Shutdown();
    }

    [Fact]
    public void LoggerPipeline_RoutesThroughSink()
    {
        using var env = new EnvScope(("EVENTLOG_LEVEL", null), ("LOG_LEVEL", null));
        var writer = Activate(env);

        // End-to-end: a real IAppLogger call reaches the event-log writer.
        var logger = Logging.GetLogger("hadoop_connector.test_sink");
        logger.Error("pipeline error");
        logger.Warning("pipeline warning");
        logger.Info("pipeline info");

        var mirrored = writer.Entries.Where(e => e.Message.StartsWith("hadoop_connector.test_sink", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mirrored.Count);
        Assert.Equal(EventLogSink.EventIdError, mirrored[0].EventId);
        Assert.Equal(EventLogSink.EventIdWarning, mirrored[1].EventId);
    }

    [Fact]
    public void NonWindows_WithoutInjectedWriter_IsNoOp()
    {
        // On the CI/dev platforms this suite runs on both branches are honest:
        // on non-Windows the OS check makes the sink inert even when enabled;
        // on Windows Initialize would need a registered source and either
        // activates or degrades to inert via the never-throws path.
        using var env = new EnvScope(("EVENTLOG_ENABLED", "true"), ("EVENTLOG_LEVEL", null));
        EventLogSink.OverrideWriter = null;
        if (!OperatingSystem.IsWindows())
        {
            EventLogSink.Initialize();
            Assert.False(EventLogSink.Enabled);
            EventLogSink.Mirror(LogLevel.Error, "x", "y");  // still safe
        }
    }
}
