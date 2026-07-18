// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// DiagnosabilityHardeningTests.cs
// -------------------------------
// Focused tests for the troubleshooting-diagnosability hardening pass:
//
//   * LOG_FORMAT=json exception records carry the full stack (type + message
//     were never enough to answer "what failed and where").
//   * The legacy resolver's group-nesting depth cap warns once PER GROUP with
//     the group id and the cap value (previously once per process, so every
//     truncated group after the first was silent).
//   * A failed bulk prewarm logs ONE error with the full exception, and the
//     single-flight retry-on-next-call announces itself.
//   * A corrupt dead-letter line is identified by file and line number before
//     the parse exception propagates (behavior unchanged — still throws).
//
// All tests attach a capturing handler to the shared logger and filter on
// their own unique message markers, so records from other components never
// affect the assertions.

using System.Reflection;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests;

/// <summary>Log handler that records emitted records (Python `caplog`).</summary>
file sealed class RecordingLogHandler : LogHandler
{
    public readonly List<LogRecord> Records = new();

    protected override void Emit(LogRecord record)
    {
        lock (Records)
            Records.Add(record);
    }

    public List<LogRecord> Where(string marker)
    {
        lock (Records)
            return Records.Where(r => r.Message.Contains(marker, StringComparison.Ordinal)).ToList();
    }
}

/// <summary>Handler subclass that exposes the protected Format for direct assertions.</summary>
file sealed class RenderingHandler : LogHandler
{
    public string Render(LogRecord record) => Format(record);

    protected override void Emit(LogRecord record)
    {
    }
}

public class JsonLogStackTests
{
    [Fact]
    public void JsonFormat_ExceptionRecord_IncludesTypeMessageAndStack()
    {
        var handler = new RenderingHandler();
        var prior = Logging.JsonFormat;
        Logging.JsonFormat = true;
        try
        {
            Exception thrown;
            try
            {
                throw new InvalidOperationException("kaput");
            }
            catch (Exception e)
            {
                thrown = e;
            }

            var rendered = handler.Render(new LogRecord
            {
                Name = "salesforce_connector",
                Level = LogLevels.Error,
                Message = "Starting ingestion process...",
                Exception = thrown,
            });

            // Single line (stack newlines are JSON-escaped).
            Assert.DoesNotContain("\n", rendered.TrimEnd('\n'));

            var obj = JsonNode.Parse(rendered)!.AsObject();
            var exObj = obj["exception"]!.AsObject();
            Assert.Equal("System.InvalidOperationException", exObj["type"]!.GetValue<string>());
            Assert.Equal("kaput", exObj["message"]!.GetValue<string>());

            var stack = exObj["stack"]!.GetValue<string>();
            Assert.Contains("System.InvalidOperationException", stack);
            Assert.Contains("kaput", stack);
            Assert.Contains("at ", stack);  // an actual stack frame made it through

            // The message field itself stays the plain message.
            Assert.Equal("Starting ingestion process...", obj["message"]!.GetValue<string>());
        }
        finally
        {
            Logging.JsonFormat = prior;
        }
    }
}

public class LegacyAclDepthCapWarningTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string UserPrefix = "005";

    private sealed class TestableResolver : AclResolver
    {
        public TestableResolver()
            : base(new AppConfig { TenantId = TenantId })
        {
        }
    }

    /// <summary>Linear chain groupPrefix0 → groupPrefix1 → … each holding one user member.</summary>
    private static (Dictionary<string, Group> Groups, Dictionary<string, List<string>> Members) LinearChain(
        string groupPrefix, int depth)
    {
        var groups = new Dictionary<string, Group>();
        var members = new Dictionary<string, List<string>>();
        for (var i = 0; i < depth; i++)
        {
            var gid = $"{groupPrefix}{i:D5}";
            groups[gid] = new Group { Id = gid, Type = "Regular" };
            var list = new List<string> { $"{UserPrefix}{i:D6}" };
            if (i + 1 < depth)
                list.Add($"{groupPrefix}{i + 1:D5}");
            members[gid] = list;
        }
        return (groups, members);
    }

    private static AclResolver MakeResolver(
        Dictionary<string, Group> groupsById,
        Dictionary<string, List<string>> membersByGroup)
    {
        var resolver = new TestableResolver();
        typeof(AclResolver)
            .GetField("_allGroupsById", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(resolver, groupsById);
        typeof(AclResolver)
            .GetField("_allGroupMembersByGroup", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(resolver, membersByGroup);
        return resolver;
    }

    [Fact]
    public async Task DepthCap_WarnsOncePerDistinctGroup_WithGroupIdAndCap()
    {
        const string marker = "exceeded the depth cap";
        const int depth = 450;  // beyond MaxGroupNestingDepth (400)

        // Two disjoint chains merged into ONE resolver's pre-warmed caches.
        var (groupsA, membersA) = LinearChain("00GAAA", depth);
        var (groupsB, membersB) = LinearChain("00GBBB", depth);
        var groups = groupsA.Concat(groupsB).ToDictionary(kv => kv.Key, kv => kv.Value);
        var members = membersA.Concat(membersB).ToDictionary(kv => kv.Key, kv => kv.Value);
        var resolver = MakeResolver(groups, members);

        var handler = new RecordingLogHandler();
        var logger = Logging.GetLoggerObject("salesforce_connector");
        var priorLevel = logger.Level;
        logger.Level = LogLevels.Debug;
        logger.AddHandler(handler);
        try
        {
            // First resolve of chain A: exactly one warning, naming the truncated
            // group and the cap value.
            var (usersA, _) = await resolver.ResolveGroupAsync("00GAAA00000");
            var warningsAfterA = handler.Where(marker);
            Assert.Single(warningsAfterA);
            Assert.Equal(LogLevels.Warning, warningsAfterA[0].Level);
            Assert.Contains("400 levels", warningsAfterA[0].Message);
            Assert.Contains("00GAAA", warningsAfterA[0].Message);
            // Fail-closed semantics unchanged: bounded grant, never over-grant.
            Assert.InRange(usersA.Count, 1, depth);

            // Re-resolving the same chain must NOT warn again (no per-record flood).
            _ = await resolver.ResolveGroupAsync("00GAAA00000");
            Assert.Single(handler.Where(marker));

            // A DIFFERENT truncated group must warn — previously the process-wide
            // once-only flag silenced every cap hit after the first.
            _ = await resolver.ResolveGroupAsync("00GBBB00000");
            var warningsAfterB = handler.Where(marker);
            Assert.Equal(2, warningsAfterB.Count);
            Assert.Contains(warningsAfterB, r => r.Message.Contains("00GBBB", StringComparison.Ordinal));
        }
        finally
        {
            logger.RemoveHandler(handler);
            logger.Level = priorLevel;
        }
    }
}

public class LegacyAclPrewarmFailureLoggingTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";

    private sealed class ThrowingPrewarmResolver : AclResolver
    {
        public int CoreCalls;

        public ThrowingPrewarmResolver()
            : base(new AppConfig { TenantId = TenantId })
        {
        }

        internal override Task PrewarmCachesCoreAsync()
        {
            CoreCalls++;
            return Task.FromException(new InvalidOperationException("SOQL reference fetch failed (simulated)"));
        }
    }

    [Fact]
    public async Task PrewarmFailure_LogsOneErrorWithException_AndAnnouncesRetry()
    {
        var resolver = new ThrowingPrewarmResolver();
        var handler = new RecordingLogHandler();
        var logger = Logging.GetLoggerObject("salesforce_connector");
        var priorLevel = logger.Level;
        logger.Level = LogLevels.Debug;
        logger.AddHandler(handler);
        try
        {
            // First attempt: fails, and the failure is logged ONCE with the full
            // exception object (type + stack land in the log output).
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.PrewarmCachesAsync());
            var failures = handler.Where("BULK PRE-WARM FAILED");
            Assert.Single(failures);
            Assert.Equal(LogLevels.Error, failures[0].Level);
            Assert.NotNull(failures[0].Exception);
            Assert.IsType<InvalidOperationException>(failures[0].Exception);
            Assert.Contains("SOQL reference fetch failed", failures[0].Message);
            Assert.Equal(1, resolver.CoreCalls);

            // Second call: the single-flight latch replaces the faulted attempt —
            // the retry is announced (naming the prior failure) and the core runs
            // again; the new attempt's own failure is logged separately.
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.PrewarmCachesAsync());
            Assert.True(resolver.CoreCalls >= 2);
            var retryNotes = handler.Where("Previous bulk prewarm attempt failed");
            Assert.NotEmpty(retryNotes);
            Assert.All(retryNotes, r => Assert.Equal(LogLevels.Warning, r.Level));
            Assert.Contains("InvalidOperationException", retryNotes[0].Message);
        }
        finally
        {
            logger.RemoveHandler(handler);
            logger.Level = priorLevel;
        }
    }
}

public class DeadLetterCorruptLineLoggingTests
{
    [Fact]
    public void ReadFailedRecords_CorruptLine_LogsFileAndLineNumber_ThenRethrows()
    {
        var priorLogsDir = SyncState.LogsDir;
        var tempDir = Path.Combine(Path.GetTempPath(), "sf-dlq-" + Guid.NewGuid().ToString("N"));
        SyncState.LogsDir = tempDir;
        SyncState.ResetProviderCache();

        var handler = new RecordingLogHandler();
        var logger = Logging.GetLoggerObject("salesforce_connector");
        var priorLevel = logger.Level;
        logger.Level = LogLevels.Debug;
        logger.AddHandler(handler);
        try
        {
            const string connectorId = "CorruptDlqConnector";
            var path = SyncState.FailedRecordsPath(connectorId);
            File.WriteAllLines(path, new[]
            {
                """{"item_id": "001A", "object_type": "Account", "error": "x"}""",
                """{"item_id": "001B", "object_type": "Account", "error": "y"}""",
                """{"item_id": "001C", "object_type": """,  // truncated mid-write
            });

            // Behavior unchanged: the parse failure still propagates …
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => SyncState.ReadFailedRecords(connectorId));

            // … but the log now names the file and the exact line.
            var errors = handler.Where("corrupt entry at line");
            Assert.Single(errors);
            Assert.Equal(LogLevels.Error, errors[0].Level);
            Assert.Contains(path, errors[0].Message);
            Assert.Contains("line 3", errors[0].Message);
        }
        finally
        {
            logger.RemoveHandler(handler);
            logger.Level = priorLevel;
            SyncState.LogsDir = priorLogsDir;
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup is best-effort on a shared runner.
            }
        }
    }
}
