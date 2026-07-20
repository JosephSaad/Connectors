// ConfigLoadErrorReportingTests.cs
// --------------------------------
// Runtime.Create loaded schema.json and filters.json with no try/catch, so any
// mistake in either file escaped to Program's final backstop, which prints the
// whole exception — stack frames and all. The operator's mistake is a typo in a
// file they can edit, the loaders already produce a sentence that names the key
// and says what to do, and burying that under a stack trace trains people to
// stop reading it.
//
// Config-shaped failures on the crawl path now surface as a CliExit: the message
// only, exit code 2 (the code the CLI already uses for bad input), with the full
// exception still written to the run log. A failure that is NOT config-shaped
// still reaches the backstop with its stack, because that is a bug and the stack
// is the point.


using HadoopConnector.Config;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class ConfigLoadErrorReportingTests
{
    private sealed class LogCapture : IDisposable
    {
        public List<(LogLevel Level, string Logger, string Message)> Lines { get; } = new();

        public LogCapture() =>
            Logging.TestSink = (level, logger, message) => Lines.Add((level, logger, message));

        public bool Any(LogLevel level, string substring) =>
            Lines.Any(l => l.Level == level && l.Message.Contains(substring, StringComparison.Ordinal));

        public void Dispose() => Logging.TestSink = null;
    }

    private static EnvScope GoodEnv() => new(
        ("CONNECTOR_ID", "BdhHadoopMart"),
        ("AAD_APP_TENANT_ID", "tenant"),
        ("AAD_APP_CLIENT_ID", "client"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "secret"),
        ("HDFS_MODE", "webhdfs"),
        ("HDFS_NAMENODE_URL", "http://namenode.example:9870/webhdfs/v1"),
        ("BDH_EXPORT_PATH", null),
        ("BDH_FILTERS_PATH", null),
        ("USE_KEY_VAULT", null),
        ("USE_SQL_SERVER", null),
        ("HA_MODE", null),
        ("EVENTLOG_ENABLED", null),
        (ShardingConfig.EnvVar, null));

    /// <summary>Run one CLI command with CWD pointed at a workspace holding the
    /// given config files, capturing stderr and the exit code.</summary>
    private static async Task<(int ExitCode, string StdErr, LogCapture Logs)> RunAsync(
        string schemaJson, string filtersJson, params string[] args)
    {
        using var env = GoodEnv();
        using var workspace = new TempDir();
        using var logsDir = new TempDir();
        var logs = new LogCapture();

        Directory.CreateDirectory(Path.Combine(workspace.Path, "config"));
        File.WriteAllText(Path.Combine(workspace.Path, "config", "schema.json"), schemaJson);
        File.WriteAllText(Path.Combine(workspace.Path, "config", "filters.json"), filtersJson);

        var previousCwd = Directory.GetCurrentDirectory();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousLogsRoot = Logging.LogsRoot;
        var stderr = new StringWriter();
        try
        {
            Directory.SetCurrentDirectory(workspace.Path);
            Console.SetOut(TextWriter.Null);
            Console.SetError(stderr);
            Logging.LogsRoot = logsDir.Path;
            var exitCode = await Program.ExecuteAsync(args);
            return (exitCode, stderr.ToString(), logs);
        }
        finally
        {
            Logging.LogsRoot = previousLogsRoot;
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
            logs.Dispose();
        }
    }

    private const string GoodFilters = """
        {"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}},
         "fullScanAllowed": []}
        """;

    private const string GoodSchema = """
        {"objectList":[{"objectName":"Contact","aclMode":"ownerOnly",
          "selectedFields":{"Id":"RecordId","Name":"Title"}}]}
        """;

    // Every schema mistake, not just one shape: the blocker's null value, a null
    // list element, a duplicate mapping, an unparseable file, a bad attestation.
    [Theory]
    [InlineData("""{"objectList":[{"objectName":"Contact","selectedFields":{"Id":"Id","X__c":null}}]}""", "X__c")]
    [InlineData("""{"objectList":[null]}""", "objectList[0]")]
    [InlineData("""{"objectList":null}""", "objectList")]
    [InlineData("""{"objectList":[{"objectName":"Contact","selectedFields":{"A":"P","B":"P"}}]}""", "same Graph property")]
    [InlineData("""{"objectList":[{"objectName":"Contact","selectedFields":{"N":"T"},"aclMode":"everyone"}]}""", "aclMode")]
    [InlineData("""{"objectList":[{"objectName":"Contact","selectedFields":{"N":"T"},"coarseAclAcknowledged":null}]}""", "coarseAclAcknowledged")]
    [InlineData("""{"objectList": [ """, "schema.json")]
    public async Task ABadSchema_PrintsAnActionableMessageWithNoStackTrace(string schemaJson, string expected)
    {
        var (exitCode, stderr, logs) = await RunAsync(
            schemaJson, GoodFilters, "ingest-object", "--type", "Contact");

        Assert.Equal(2, exitCode);
        Assert.Contains(expected, stderr, StringComparison.Ordinal);
        Assert.Contains("schema.json", stderr, StringComparison.Ordinal);
        Assert.Contains("validate-config", stderr, StringComparison.Ordinal);
        Assert.Contains("error: schema.json is invalid", stderr, StringComparison.Ordinal);
        // THE regression being locked: no stack frames anywhere on stderr.
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--- End of stack trace", stderr, StringComparison.Ordinal);
        // …and the full record, exception included, is still in the run log.
        Assert.True(logs.Any(LogLevel.Error, "schema.json"),
            "expected an ERROR naming the config file in the run log");
    }

    // The same treatment for the OTHER config file the bootstrap loads — the
    // guard is per file, not one special case for schema.json.
    [Fact]
    public async Task ABadFiltersFile_PrintsAnActionableMessageWithNoStackTrace()
    {
        var (exitCode, stderr, logs) = await RunAsync(
            GoodSchema,
            """{"objects": {"Contact": {"partition": [{"key": "dt", "op": "nonsenseOp"}]}}}""",
            "ingest-object", "--type", "Contact");

        Assert.Equal(2, exitCode);
        Assert.Contains("filters.json", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.True(logs.Any(LogLevel.Error, "filters.json"));
    }

    // A file that is simply MISSING is a config error too, not a stack trace.
    [Fact]
    public async Task AMissingSchemaFile_PrintsAnActionableMessageWithNoStackTrace()
    {
        using var env = GoodEnv();
        using var workspace = new TempDir();
        using var logsDir = new TempDir();
        var previousCwd = Directory.GetCurrentDirectory();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousLogsRoot = Logging.LogsRoot;
        var stderr = new StringWriter();
        try
        {
            Directory.SetCurrentDirectory(workspace.Path);   // no config/ at all
            Console.SetOut(TextWriter.Null);
            Console.SetError(stderr);
            Logging.LogsRoot = logsDir.Path;

            var exitCode = await Program.ExecuteAsync(new[] { "ingest-object", "--type", "Contact" });

            Assert.Equal(2, exitCode);
            Assert.Contains("schema.json", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Logging.LogsRoot = previousLogsRoot;
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    // The guard must not swallow real bugs: an ENV/AppConfig failure still takes
    // the backstop path (exit 1, structured log with command context), which
    // DiagnosabilityTests pins. Asserted here too so the two cannot silently
    // converge onto one code path.
    [Fact]
    public async Task AnEnvConfigError_StillTakesTheBackstopPath()
    {
        using var env = new EnvScope(("CONNECTOR_ID", null));
        using var workspace = new TempDir();
        using var logsDir = new TempDir();
        var previousCwd = Directory.GetCurrentDirectory();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousLogsRoot = Logging.LogsRoot;
        try
        {
            Directory.SetCurrentDirectory(workspace.Path);
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Logging.LogsRoot = logsDir.Path;

            var exitCode = await Program.ExecuteAsync(new[] { "ingest-object", "--type", "Contact" });

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Logging.LogsRoot = previousLogsRoot;
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
        }
    }
}
