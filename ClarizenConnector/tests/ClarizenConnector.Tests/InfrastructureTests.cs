using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

public class AlertingTests
{
    [Fact]
    public async Task RaiseAsync_NoWebhookUrl_IsNoOp()
    {
        using var scope = new EnvScope((Alerting.WebhookUrlEnvVar, null));
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.RaiseAsync("test", "message");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RaiseAsync_PostsEnvelope()
    {
        using var scope = new EnvScope((Alerting.WebhookUrlEnvVar, "https://hooks.example/x"));
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        Alerting.HttpClient = new HttpClient(handler);
        Alerting.ConnectorId = "TestConn";

        await Alerting.RaiseAsync("crawl_failed", "boom", new { code = 7 });

        var (url, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://hooks.example/x", url);
        var envelope = JsonNode.Parse(body)!;
        Assert.Equal("crawl_failed", envelope["kind"]!.GetValue<string>());
        Assert.Equal("boom", envelope["message"]!.GetValue<string>());
        Assert.Equal("TestConn", envelope["connector"]!.GetValue<string>());
        Assert.NotNull(envelope["timestamp"]);
        Assert.Equal(7, envelope["data"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task RaiseAsync_SwallowsDeliveryFailures()
    {
        using var scope = new EnvScope((Alerting.WebhookUrlEnvVar, "https://hooks.example/x"));
        var handler = new MockHttpHandler((_, _) => throw new HttpRequestException("net down"));
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.RaiseAsync("k", "m");  // must not throw
    }

    [Theory]
    [InlineData(null, 100, 0)]     // threshold unset → never
    [InlineData("0", 100, 0)]      // non-positive → never
    [InlineData("50", 50, 0)]      // equal → not exceeded
    [InlineData("50", 51, 1)]      // exceeded → alert
    public async Task MaybeAlertDeadLetter_ThresholdLogic(string? threshold, int depth, int expectedPosts)
    {
        using var scope = new EnvScope(
            (Alerting.WebhookUrlEnvVar, "https://hooks.example/x"),
            (Alerting.DeadLetterThresholdEnvVar, threshold));
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        Alerting.HttpClient = new HttpClient(handler);

        await Alerting.MaybeAlertDeadLetterAsync("Conn", depth);
        Assert.Equal(expectedPosts, handler.Requests.Count);
    }

    [Fact]
    public void BuildEnvelope_OmitsConnectorWhenUnset()
    {
        Alerting.ConnectorId = null;
        var envelope = JsonNode.Parse(Alerting.BuildEnvelope("k", "m", null))!;
        Assert.Null(envelope["connector"]);
        Assert.Null(envelope["data"]);
    }
}

public class MetricsTests
{
    [Fact]
    public void Counters_AccumulateAndRender()
    {
        Metrics.ResetForTests();
        Metrics.IncItemsIngested(5);
        Metrics.IncItemsFailed();
        Metrics.IncThrottle429(3);
        Metrics.IncClarizenApiCalls(7);
        Metrics.IncAttachmentsExtracted(4);
        Metrics.IncAttachmentsSkipped(2);
        Metrics.IncWebhookEventsReceived(9);
        Metrics.IncWebhookEventsAccepted(6);
        Metrics.IncWebhookEventsRejected(3);
        Metrics.SetWebhookReceiverUp(true);
        Metrics.SetDeadLetterDepth(2);
        Metrics.SetApiBudgetRemaining(24_993);

        Assert.Equal(5, Metrics.ItemsIngested);
        Assert.Equal(1, Metrics.ItemsFailed);
        Assert.Equal(4, Metrics.AttachmentsExtracted);
        Assert.Equal(2, Metrics.AttachmentsSkipped);

        var text = Metrics.RenderPrometheus();
        Assert.Contains("# TYPE clarizen_connector_items_ingested_total counter", text);
        Assert.Contains("clarizen_connector_items_ingested_total 5", text);
        Assert.Contains("clarizen_connector_throttled_429_total 3", text);
        Assert.Contains("clarizen_connector_clarizen_api_calls_total 7", text);
        Assert.Contains("clarizen_connector_attachments_extracted_total 4", text);
        Assert.Contains("clarizen_connector_attachments_skipped_total 2", text);
        Assert.Contains("clarizen_connector_webhook_events_received_total 9", text);
        Assert.Contains("clarizen_connector_webhook_events_accepted_total 6", text);
        Assert.Contains("clarizen_connector_webhook_events_rejected_total 3", text);
        Assert.Contains("clarizen_connector_webhook_receiver_up 1", text);
        Assert.Contains("clarizen_connector_dead_letter_depth 2", text);
        Assert.Contains("clarizen_connector_api_budget_remaining 24993", text);
        Assert.Contains("# HELP clarizen_connector_uptime_seconds", text);
        Metrics.ResetForTests();
    }

    [Fact]
    public void MarkCrawlCompletedNow_StampsGauge()
    {
        Metrics.ResetForTests();
        Assert.Equal(0, Metrics.LastCrawlCompletedUnix);
        Metrics.MarkCrawlCompletedNow();
        Assert.InRange(
            Metrics.LastCrawlCompletedUnix,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
        Metrics.ResetForTests();
    }
}

public class LogPrunerTests
{
    [Fact]
    public void Prune_RemovesOnlyExpiredRunDirs()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "ingest_20260101_120000"));   // old
        Directory.CreateDirectory(Path.Combine(dir.Path, "ingest_20260711_120000"));   // fresh
        Directory.CreateDirectory(Path.Combine(dir.Path, "not_a_run_dir"));            // untouched
        File.WriteAllText(Path.Combine(dir.Path, "sync_state.json"), "{}");            // state file
        File.WriteAllText(Path.Combine(dir.Path, "failed_records_X.jsonl"), "");

        var removed = LogPruner.Prune(dir.Path, retentionDays: 7, new DateTime(2026, 7, 12, 9, 0, 0));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "ingest_20260101_120000")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "ingest_20260711_120000")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "not_a_run_dir")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "sync_state.json")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "failed_records_X.jsonl")));
    }

    [Fact]
    public void PruneIfConfigured_DisabledWhenUnsetOrInvalid()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "ingest_20200101_000000"));

        using (new EnvScope((LogPruner.RetentionEnvVar, null)))
            Assert.Equal(0, LogPruner.PruneIfConfigured(dir.Path));
        using (new EnvScope((LogPruner.RetentionEnvVar, "0")))
            Assert.Equal(0, LogPruner.PruneIfConfigured(dir.Path));
        using (new EnvScope((LogPruner.RetentionEnvVar, "nope")))
            Assert.Equal(0, LogPruner.PruneIfConfigured(dir.Path));
        using (new EnvScope((LogPruner.RetentionEnvVar, "30")))
            Assert.Equal(1, LogPruner.PruneIfConfigured(dir.Path));
    }

    [Fact]
    public void Prune_MissingRoot_IsZero()
    {
        Assert.Equal(0, LogPruner.Prune("/nonexistent/logs/root", 7, DateTime.Now));
    }
}

public class EnvLoaderTests
{
    [Fact]
    public void ParseLine_Variants()
    {
        Assert.Equal(("KEY", "value"), EnvLoader.ParseLine("KEY=value"));
        Assert.Equal(("KEY", "spaced value"), EnvLoader.ParseLine("  KEY = spaced value  "));
        Assert.Equal(("KEY", "quoted"), EnvLoader.ParseLine("KEY=\"quoted\""));
        Assert.Equal(("KEY", "single"), EnvLoader.ParseLine("KEY='single'"));
        Assert.Equal(("KEY", "value"), EnvLoader.ParseLine("export KEY=value"));
        Assert.Equal(("KEY", "v"), EnvLoader.ParseLine("KEY=v # trailing comment"));
        Assert.Equal(("KEY", "a # not comment"), EnvLoader.ParseLine("KEY=\"a # not comment\""));
        Assert.Null(EnvLoader.ParseLine("# comment").Key);
        Assert.Null(EnvLoader.ParseLine("").Key);
        Assert.Null(EnvLoader.ParseLine("=nokey").Key);
        Assert.Null(EnvLoader.ParseLine("no equals sign").Key);
    }

    [Fact]
    public void LoadLayered_ProcessEnvWins_AndFirstFileWins()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "env"));
        File.WriteAllText(
            Path.Combine(dir.Path, "env", ".env.local"),
            "CLZTEST_A=from-env-local\nCLZTEST_B=from-env-local\n");
        File.WriteAllText(
            Path.Combine(dir.Path, "env", ".env.local.user"),
            "CLZTEST_B=from-user-file\nCLZTEST_C=from-user-file\n");

        using var scope = new EnvScope(
            ("CLZTEST_A", "from-process"), ("CLZTEST_B", null), ("CLZTEST_C", null));
        EnvLoader.LoadLayered(dir.Path);

        Assert.Equal("from-process", Environment.GetEnvironmentVariable("CLZTEST_A"));   // process wins
        Assert.Equal("from-env-local", Environment.GetEnvironmentVariable("CLZTEST_B")); // first file wins
        Assert.Equal("from-user-file", Environment.GetEnvironmentVariable("CLZTEST_C"));

        scope.Set("CLZTEST_B", null);
        scope.Set("CLZTEST_C", null);
    }
}

public class SecretProviderTests : IDisposable
{
    public SecretProviderTests() => SecretProvider.ResetForTests();

    public void Dispose() => SecretProvider.ResetForTests();

    [Fact]
    public void Passthrough_WhenKeyVaultDisabled()
    {
        using var scope = new EnvScope(
            ("USE_KEY_VAULT", null), ("SECRET_TEST_VALUE", "plain-env"));
        Assert.Equal("plain-env", SecretProvider.GetSecret("SECRET_TEST_VALUE"));
    }

    [Fact]
    public void ToSecretName_LowercasesAndHyphenates()
    {
        Assert.Equal(
            "secret-clarizen-password", SecretProvider.ToSecretName("SECRET_CLARIZEN_PASSWORD"));
    }

    [Fact]
    public void KeyVault_FetchesViaMappedName()
    {
        using var scope = new EnvScope(("USE_KEY_VAULT", "true"), ("SECRET_KV_ONLY", null));
        string? requested = null;
        SecretProvider.OverrideFetch = name =>
        {
            requested = name;
            return "vault-value";
        };
        Assert.Equal("vault-value", SecretProvider.GetSecret("SECRET_KV_ONLY"));
        Assert.Equal("secret-kv-only", requested);
    }

    [Fact]
    public void KeyVault_FailureFallsBackToEnv()
    {
        using var scope = new EnvScope(
            ("USE_KEY_VAULT", "true"), ("SECRET_FALLBACK", "env-fallback"));
        SecretProvider.OverrideFetch = _ => throw new InvalidOperationException("vault down");
        Assert.Equal("env-fallback", SecretProvider.GetSecret("SECRET_FALLBACK"));
    }

    [Fact]
    public void KeyVault_FailureWithoutEnv_ReturnsNull()
    {
        using var scope = new EnvScope(("USE_KEY_VAULT", "true"), ("SECRET_MISSING", null));
        SecretProvider.OverrideFetch = _ => throw new InvalidOperationException("vault down");
        Assert.Null(SecretProvider.GetSecret("SECRET_MISSING"));
    }

    [Fact]
    public void KeyVault_SuccessfulFetchIsCached_FailureIsNot()
    {
        using var scope = new EnvScope(("USE_KEY_VAULT", "true"), ("SECRET_CACHED", "env"));
        var calls = 0;
        SecretProvider.OverrideFetch = _ =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("transient");
            return "vault-ok";
        };
        Assert.Equal("env", SecretProvider.GetSecret("SECRET_CACHED"));      // failure → fallback, uncached
        Assert.Equal("vault-ok", SecretProvider.GetSecret("SECRET_CACHED")); // retried, now cached
        Assert.Equal("vault-ok", SecretProvider.GetSecret("SECRET_CACHED"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void KeyVault_MissingUriWithoutOverride_Throws()
    {
        using var scope = new EnvScope(("USE_KEY_VAULT", "true"), ("KEY_VAULT_URI", null));
        SecretProvider.OverrideFetch = null;
        var exc = Assert.Throws<ArgumentException>(() => SecretProvider.GetSecret("SECRET_X"));
        Assert.Contains("KEY_VAULT_URI", exc.Message);
    }
}

public class EnvFlagsTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTrue_TruthySemantics(string? value, bool expected)
    {
        using var scope = new EnvScope(("CLZTEST_FLAG", value));
        Assert.Equal(expected, EnvFlags.IsTrue("CLZTEST_FLAG"));
    }

    [Fact]
    public void UseSqlServer_RequiresBothVars()
    {
        using (new EnvScope(("USE_SQL_SERVER", "true"), ("SQL_CONNECTION_STRING", null)))
            Assert.False(EnvFlags.UseSqlServer);
        using (new EnvScope(("USE_SQL_SERVER", "true"), ("SQL_CONNECTION_STRING", "Server=x;Database=y")))
            Assert.True(EnvFlags.UseSqlServer);
        using (new EnvScope(("USE_SQL_SERVER", null), ("SQL_CONNECTION_STRING", "Server=x")))
            Assert.False(EnvFlags.UseSqlServer);
    }
}

public class ServiceStopTests
{
    [Fact]
    public void RequestAndReset()
    {
        ServiceStop.Reset();
        Assert.False(ServiceStop.Requested);
        ServiceStop.Request();
        Assert.True(ServiceStop.Requested);
        Assert.True(ServiceStop.Token.IsCancellationRequested);
        ServiceStop.Reset();
        Assert.False(ServiceStop.Requested);
    }
}
