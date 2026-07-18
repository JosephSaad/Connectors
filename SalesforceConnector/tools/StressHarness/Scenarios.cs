// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// The four stress scenarios. Each one drives the REAL Ingest.IngestContentAsync
// pipeline end-to-end (chunking, ACL, transform, $batch dispatch, adaptive
// concurrency, checkpointing, dead-letter) with fakes only at the documented
// internal seams.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace StressHarness;

internal sealed class ScenarioResult
{
    public required string Name { get; init; }
    public int Items { get; set; }
    public double WallSecs { get; set; }
    public double ItemsPerSec { get; set; }
    public double PeakRssMb { get; set; }
    public double AllocMb { get; set; }
    public List<string> PassedInvariants { get; } = new();
    public List<string> FailedInvariants { get; } = new();
    public List<string> Notes { get; } = new();
    public bool Pass => FailedInvariants.Count == 0;
}

internal sealed class ScenarioRunner
{
    private const string ConnectorId = "StressHarness";
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromMinutes(10);

    private readonly string _workdir;
    private readonly int? _latencyOverride;
    private readonly bool _countOnlyAcks;
    private readonly ConcurrencyCapture _capture = new();

    private CancellationTokenSource? _memCts;
    private Task? _memTask;
    private long _peakRss;

    public ScenarioRunner(string workdir, int? latencyOverride, bool countOnlyAcks = false)
    {
        _workdir = workdir;
        _latencyOverride = latencyOverride;
        _countOnlyAcks = countOnlyAcks;

        // Observe the pipeline's internal AdaptiveConcurrency + retry logging.
        // Propagate=false keeps the (very chatty) INFO/ERROR stream off the console;
        // the capture handler records what the scenarios need.
        var logger = Logging.GetLoggerObject("salesforce_connector");
        logger.Level = LogLevels.Info;
        logger.Propagate = false;
        logger.AddHandler(_capture);
    }

    // ── Shared plumbing ──────────────────────────────────────────────────────

    private static AppConfig BuildConfig(int concurrentBatches = 8, int parallelWorkers = 3) => new()
    {
        ClientId = "00000000-0000-0000-0000-000000000000",
        TenantId = "00000000-0000-0000-0000-000000000001",
        RepoRoot = "",
        SchemaConfig = new JsonObject(),
        OwdFieldMap = new Dictionary<string, string>(),
        ParentMap = new Dictionary<string, (string, string)>(),
        OwdOverrides = new Dictionary<string, string>(),
        ObjectNames = SyntheticData.ObjectTypes.ToList(),
        UseNewAclEngine = false,
        UseGroupAcl = false,
        UseEntityDefinitionOwd = false,
        DebugObjectType = null,
        DebugItemId = null,
        Tuning = new TuningSettings
        {
            GraphApiVersion = "v1.0",
            GraphMaxRetries = 4,
            GraphRetryBackoffBase = 1,
            ConnectionTimeoutSeconds = 600,
            ConnectionRetryIntervalSeconds = 15,
            SchemaRetryIntervalSeconds = 15,
            SalesforceQueryLimit = 0,
            SalesforceBatchSize = 100,
            AclMaxParentDepth = 5,
            IngestChunkSize = 500,
            IngestGraphBatchSize = 20,
            GraphConcurrentBatches = concurrentBatches,
            ParallelObjectWorkers = parallelWorkers,
        },
        Connector = new ConnectorSettings
        {
            Id = ConnectorId,
            Name = "Stress Harness",
            Description = "Synthetic stress-test connector (no real Graph/Salesforce calls).",
            Schema = new JsonArray(),
            Template = new JsonObject { ["id"] = "display" },
            Salesforce = new SalesforceSettings
            {
                InstanceUrl = "https://stress.my.salesforce.com",
                ApiVersion = "v60.0",
                ClientId = "stress-client-id",
                ClientSecret = "stress-client-secret",
            },
        },
    };

    private static void InstallStandardHooks(IReadOnlyDictionary<string, int> itemsPerType)
    {
        Ingest.GetObjectConfigHook = name => itemsPerType.ContainsKey(name)
            ? new SalesforceObjectConfig(ObjectType: name, Fields: new[] { "Id" })
            : null;
        Ingest.IterObjectChunksHook = (_, objCfg, _, chunkSize) =>
            SyntheticData.ChunksAsync(
                objCfg.ObjectType, itemsPerType.GetValueOrDefault(objCfg.ObjectType), chunkSize);
        Ingest.LegacyAclResolverFactory = (config, _, _) => new HarnessAclResolver(config);
        Ingest.TransformerFactory = (_, _, _) => new HarnessTransformer();
        Ingest.GetObjectCountsHook = (_, _) => Task.FromResult(new Dictionary<string, int>());
    }

    private string FreshLogsDir(string scenario)
    {
        var dir = Path.Combine(_workdir, scenario, "logs");
        var scenarioDir = Path.Combine(_workdir, scenario);
        if (Directory.Exists(scenarioDir))
            Directory.Delete(scenarioDir, recursive: true);
        Directory.CreateDirectory(dir);
        SyncState.LogsDir = dir;
        return dir;
    }

    private void StartMeasurement()
    {
        _capture.Reset();
        _peakRss = 0;
        _memCts = new CancellationTokenSource();
        var token = _memCts.Token;
        _memTask = Task.Run(async () =>
        {
            var proc = Process.GetCurrentProcess();
            while (!token.IsCancellationRequested)
            {
                proc.Refresh();
                var ws = proc.WorkingSet64;
                if (ws > Volatile.Read(ref _peakRss))
                    Volatile.Write(ref _peakRss, ws);
                try
                {
                    await Task.Delay(100, token);
                }
                catch (TaskCanceledException)
                {
                }
            }
        });
    }

    private async Task<double> StopMeasurementAsync()
    {
        _memCts!.Cancel();
        await _memTask!;
        _memCts.Dispose();
        return Volatile.Read(ref _peakRss) / (1024.0 * 1024.0);
    }

    /// <summary>Bounded wait — returns null if the pipeline did not come back in time (deadlock guard).</summary>
    private static async Task<IngestionStats?> AwaitBoundedAsync(Task<IngestionStats> run, TimeSpan timeout)
    {
        var done = await Task.WhenAny(run, Task.Delay(timeout));
        return done == run ? await run : null;
    }

    private static void Check(ScenarioResult result, bool ok, string invariant)
    {
        if (ok)
            result.PassedInvariants.Add(invariant);
        else
            result.FailedInvariants.Add(invariant);
    }

    private static bool Reconciles(IngestionStats stats)
        => stats.TotalFetched ==
           stats.SuccessCount + stats.FailedCount + stats.DeletedCount + stats.SkippedCount;

    private static string StatLine(IngestionStats s)
        => $"fetched={s.TotalFetched} success={s.SuccessCount} failed={s.FailedCount} " +
           $"deleted={s.DeletedCount} skipped={s.SkippedCount}";

    private int Latency(int defaultMs) => _latencyOverride ?? defaultMs;

    /// <summary>
    /// Tolerant dead-letter reader. Corrupt (unparseable) lines are counted, not
    /// thrown — concurrent SendOneAsync tasks all append to the same JSONL file
    /// with no synchronization (SyncState.AppendFailedRecords, SyncState.cs:231),
    /// so interleaved/overwritten lines are a real, observed failure mode that the
    /// failures scenario asserts on explicitly.
    /// </summary>
    private static (List<JsonObject> Entries, int CorruptLines) ReadDeadLetter(string path)
    {
        var entries = new List<JsonObject>();
        var corrupt = 0;
        if (!File.Exists(path))
            return (entries, corrupt);
        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0)
                continue;
            try
            {
                entries.Add(JsonNode.Parse(line)!.AsObject());
            }
            catch (System.Text.Json.JsonException)
            {
                corrupt++;
            }
        }
        return (entries, corrupt);
    }

    // ── Scenario 1: throughput ───────────────────────────────────────────────

    public async Task<ScenarioResult> RunThroughputAsync(int items)
    {
        var result = new ScenarioResult { Name = "throughput", Items = items };
        var latency = Latency(25);
        result.Notes.Add($"{items} items, 3 object types, {latency}ms simulated $batch latency, " +
                         "chunk=500, batch=20, concurrent_batches=8, workers=3");

        FreshLogsDir("throughput");
        ServiceStop.Reset();
        var perType = SyntheticData.Split(items);
        InstallStandardHooks(perType);
        var cfg = BuildConfig();
        var client = new HarnessGraphClient { LatencyMs = latency, CountOnlyAcks = _countOnlyAcks };
        if (_countOnlyAcks)
            result.Notes.Add("count-only-acks soak mode: harness does not retain per-item ack ids");

        StartMeasurement();
        var allocBefore = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();
        var stats = await AwaitBoundedAsync(Ingest.IngestContentAsync(cfg, client), ScenarioTimeout);
        sw.Stop();
        result.AllocMb = (GC.GetTotalAllocatedBytes() - allocBefore) / (1024.0 * 1024.0);
        result.PeakRssMb = await StopMeasurementAsync();
        result.WallSecs = sw.Elapsed.TotalSeconds;

        Check(result, stats != null, "run completed within bounded wait");
        if (stats == null)
            return result;

        result.ItemsPerSec = items / result.WallSecs;
        result.Notes.Add(StatLine(stats));
        result.Notes.Add($"batch calls={client.Calls}, max in-flight $batch={client.MaxInFlight}");

        Check(result, stats.TotalFetched == items, $"fetched == {items} (actual {stats.TotalFetched})");
        Check(result, Reconciles(stats), $"stats reconcile ({StatLine(stats)})");
        Check(result, stats.FailedCount == 0, $"zero failures (actual {stats.FailedCount})");
        Check(result, stats.SuccessCount == items, $"success == {items} (actual {stats.SuccessCount})");
        if (_countOnlyAcks)
            Check(result, client.AckedTotal == items, $"acked total == {items} (count-only soak mode)");
        else
            Check(result, client.Acked.Count == items && client.Acked.Values.All(v => v == 1),
                "every item PUT exactly once");
        Check(result, SyncState.ReadCheckpoint(ConnectorId) == null, "checkpoint cleared on completion");
        return result;
    }

    // ── Scenario 2: throttle ─────────────────────────────────────────────────

    public async Task<ScenarioResult> RunThrottleAsync(int items)
    {
        var result = new ScenarioResult { Name = "throttle", Items = items };
        // Default latency is raised to 150 ms so the run is still in flight when the
        // t=10s..20s burst window opens (override with --latency-ms).
        var latency = Latency(150);
        result.Notes.Add($"{items} items, {latency}ms latency; ALL batches 429 (Retry-After: 1) " +
                         "during t=10s..20s, then deterministic 5% of batches 429");

        FreshLogsDir("throttle");
        ServiceStop.Reset();
        var perType = SyntheticData.Split(items);
        InstallStandardHooks(perType);
        var cfg = BuildConfig();

        var burst429Batches = 0;
        var post429Batches = 0;
        var runClock = new Stopwatch();
        var client = new HarnessGraphClient { LatencyMs = latency };
        client.OnBatch = (call, payload) =>
        {
            var t = runClock.Elapsed.TotalSeconds;
            if (t >= 10 && t < 20)
            {
                Interlocked.Increment(ref burst429Batches);
                return payload.Select(HarnessGraphClient.Throttled).ToList();
            }
            // Deterministic "random" 5% after the burst (Knuth multiplicative hash of call index).
            if (t >= 20 && (uint)(call * 2654435761u) % 100 < 5)
            {
                Interlocked.Increment(ref post429Batches);
                return payload.Select(HarnessGraphClient.Throttled).ToList();
            }
            return HarnessGraphClient.AllOk(payload);
        };

        StartMeasurement();
        var allocBefore = GC.GetTotalAllocatedBytes();
        runClock.Start();
        var sw = Stopwatch.StartNew();
        var stats = await AwaitBoundedAsync(Ingest.IngestContentAsync(cfg, client), ScenarioTimeout);
        sw.Stop();
        result.AllocMb = (GC.GetTotalAllocatedBytes() - allocBefore) / (1024.0 * 1024.0);
        result.PeakRssMb = await StopMeasurementAsync();
        result.WallSecs = sw.Elapsed.TotalSeconds;

        Check(result, stats != null, "run completed within bounded wait");
        if (stats == null)
            return result;

        result.ItemsPerSec = items / result.WallSecs;
        result.Notes.Add(StatLine(stats));

        var events = _capture.Events;
        var downs = events.Where(e => e.Kind == "down").ToList();
        var ups = events.Where(e => e.Kind == "up").ToList();
        var minLevel = downs.Count > 0 ? downs.Min(e => e.Level) : 8;
        var finalLevel = events.Count > 0 ? events[^1].Level : 8;
        result.Notes.Add(
            $"AdaptiveConcurrency: start 8, dial-downs={downs.Count} (min {minLevel}), " +
            $"ramp-ups={ups.Count}, final {finalLevel}");
        if (downs.Count > 0)
        {
            result.Notes.Add(
                $"first dial-down t={downs[0].AtSecs:F1}s; " +
                $"first ramp-up t={(ups.Count > 0 ? ups[0].AtSecs.ToString("F1", CultureInfo.InvariantCulture) : "n/a")}s");
        }
        result.Notes.Add(
            $"429 batches: {burst429Batches} in burst, {post429Batches} in 5% tail; " +
            $"retry waits: {_capture.RetryEvents} sub-batch retries, " +
            $"{_capture.ThrottledItemRetries} item retries, " +
            $"total added retry delay {_capture.RetryDelaySecs:F0}s (overlapping)");

        Check(result, stats.TotalFetched == items, $"fetched == {items} (actual {stats.TotalFetched})");
        Check(result, Reconciles(stats), $"stats reconcile ({StatLine(stats)})");
        Check(result, burst429Batches > 0, "burst window engaged (run still in flight at t=10s)");
        Check(result, downs.Count > 0, "concurrency dialled down on 429");
        Check(result, ups.Count > 0, "concurrency ramped back up after burst");
        Check(result,
            stats.SuccessCount + stats.FailedCount == items,
            $"all items resolved to success or failure ({StatLine(stats)})");
        // Every acknowledged item acked exactly once, and acks + permanent failures == items.
        Check(result, client.Acked.Values.All(v => v == 1), "no duplicate PUT acks");
        Check(result, client.Acked.Count == stats.SuccessCount,
            $"acked PUTs ({client.Acked.Count}) == success count ({stats.SuccessCount})");
        if (stats.FailedCount > 0)
            result.Notes.Add($"{stats.FailedCount} item(s) exhausted 4 retries inside the burst " +
                             "(counted failed + dead-lettered — expected under sustained 429)");
        return result;
    }

    // ── Scenario 2b: adaptive-concurrency oscillation (flapping 429 waves) ───

    public async Task<ScenarioResult> RunOscillateAsync(int items)
    {
        // Flapping throttle waves: every batch 429s during t∈[3,5), [8,10),
        // [13,15), … (2s bad / 3s clean, forever) — the pattern that provokes
        // livelock or dial thrash in adaptive controllers. The pipeline must
        // keep making progress through every wave, dial down in each wave, ramp
        // fully back between/after them, and finish with zero failed items.
        var result = new ScenarioResult { Name = "oscillate", Items = items };
        var latency = Latency(100);
        result.Notes.Add($"{items} items, {latency}ms latency; ALL batches 429 (Retry-After: 1) during " +
                         "t∈[3,5)+5k s — flapping 2s-bad/3s-clean waves for the whole run");

        FreshLogsDir("oscillate");
        ServiceStop.Reset();
        var perType = SyntheticData.Split(items);
        InstallStandardHooks(perType);
        var cfg = BuildConfig();

        // windowIndex k covers t ∈ [3+5k, 5+5k)
        static bool InBadWindow(double t, out int window)
        {
            window = -1;
            if (t < 3)
                return false;
            window = (int)((t - 3) / 5);
            return (t - 3) % 5 < 2;
        }

        var badBatchesByWave = new ConcurrentDictionary<int, int>();
        var runClock = new Stopwatch();
        var client = new HarnessGraphClient { LatencyMs = latency };
        client.OnBatch = (_, payload) =>
        {
            if (InBadWindow(runClock.Elapsed.TotalSeconds, out var wave))
            {
                badBatchesByWave.AddOrUpdate(wave, 1, (_, v) => v + 1);
                return payload.Select(HarnessGraphClient.Throttled).ToList();
            }
            return HarnessGraphClient.AllOk(payload);
        };

        StartMeasurement();
        var allocBefore = GC.GetTotalAllocatedBytes();
        runClock.Start();
        var sw = Stopwatch.StartNew();
        var stats = await AwaitBoundedAsync(Ingest.IngestContentAsync(cfg, client), ScenarioTimeout);
        sw.Stop();
        result.AllocMb = (GC.GetTotalAllocatedBytes() - allocBefore) / (1024.0 * 1024.0);
        result.PeakRssMb = await StopMeasurementAsync();
        result.WallSecs = sw.Elapsed.TotalSeconds;

        Check(result, stats != null, "run completed within bounded wait (no livelock under flapping 429s)");
        if (stats == null)
            return result;

        result.ItemsPerSec = items / result.WallSecs;
        result.Notes.Add(StatLine(stats));

        var events = _capture.Events;
        var downs = events.Where(e => e.Kind == "down").ToList();
        var ups = events.Where(e => e.Kind == "up").ToList();
        var engagedWaves = badBatchesByWave.Keys.OrderBy(w => w).ToList();
        var finalLevel = events.Count > 0 ? events[^1].Level : 8;

        // Per-wave dial behavior: first dial-down inside the wave, the minimum
        // level reached before the next wave, and when the dial was back at max.
        var waveNotes = new List<string>();
        var wavesWithDialDown = 0;
        var recoveredAfterEachWave = 0;
        foreach (var wave in engagedWaves)
        {
            double start = 3 + 5.0 * wave, end = start + 2;
            var nextStart = start + 5;
            var inWave = downs.Where(e => e.AtSecs >= start && e.AtSecs < nextStart).ToList();
            var levelsUntilNext = events.Where(e => e.AtSecs >= start && e.AtSecs < nextStart).ToList();
            var minLevel = levelsUntilNext.Count > 0 ? levelsUntilNext.Min(e => e.Level) : 8;
            var recovery = events.FirstOrDefault(e => e.AtSecs >= end && e.Kind == "up" && e.Level == 8);
            if (inWave.Count > 0)
                wavesWithDialDown++;
            if (recovery != null)
                recoveredAfterEachWave++;
            waveNotes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "wave{0} t=[{1:F0},{2:F0}): {3} 429-batches, first dial-down t={4}, min level {5}, back at 8 t={6}",
                wave, start, end, badBatchesByWave[wave],
                inWave.Count > 0 ? inWave[0].AtSecs.ToString("F1", CultureInfo.InvariantCulture) + "s" : "n/a",
                minLevel,
                recovery != null ? recovery.AtSecs.ToString("F1", CultureInfo.InvariantCulture) + "s" : "n/a"));
        }
        result.Notes.Add(
            $"oscillation: {engagedWaves.Count} waves engaged over {result.WallSecs:F1}s " +
            $"({engagedWaves.Count / Math.Max(result.WallSecs, 1) * 60:F1} waves/min); " +
            $"dial-downs={downs.Count}, ramp-ups={ups.Count}, final level {finalLevel}");
        foreach (var note in waveNotes)
            result.Notes.Add(note);
        result.Notes.Add(
            $"retry waves: {_capture.RetryEvents} sub-batch retries, {_capture.ThrottledItemRetries} item retries, " +
            $"total added retry delay {_capture.RetryDelaySecs:F0}s (overlapping)");

        Check(result, stats.TotalFetched == items, $"fetched == {items} (actual {stats.TotalFetched})");
        Check(result, Reconciles(stats), $"stats reconcile ({StatLine(stats)})");
        Check(result, engagedWaves.Count >= 2,
            $"at least 2 throttle waves engaged (actual {engagedWaves.Count} — run long enough to flap)");
        Check(result, wavesWithDialDown == engagedWaves.Count,
            $"concurrency dialled down in every engaged wave ({wavesWithDialDown}/{engagedWaves.Count})");
        Check(result, recoveredAfterEachWave == engagedWaves.Count,
            $"concurrency ramped back to max after every wave ({recoveredAfterEachWave}/{engagedWaves.Count} — no thrash lock-in)");
        Check(result, finalLevel == 8, $"final concurrency recovered to max 8 (actual {finalLevel})");
        Check(result, stats.FailedCount == 0,
            $"zero items failed — every throttled item survived the flapping (actual {stats.FailedCount})");
        Check(result, stats.SuccessCount == items, $"success == {items} (actual {stats.SuccessCount})");
        Check(result, client.Acked.Count == items && client.Acked.Values.All(v => v == 1),
            "every item PUT exactly once (no duplicate acks under retry waves)");
        return result;
    }

    // ── Scenario 3: failures + dead-letter retry ─────────────────────────────

    public async Task<ScenarioResult> RunFailuresAsync(int items)
    {
        var result = new ScenarioResult { Name = "failures", Items = items };
        var latency = Latency(25);
        result.Notes.Add($"{items} items, {latency}ms latency, 10% of sub-responses permanent 500 " +
                         "(record index % 10 == 7), then dead-letter retry pass through a healthy fake");

        var logsDir = FreshLogsDir("failures");
        ServiceStop.Reset();
        var perType = SyntheticData.Split(items);
        InstallStandardHooks(perType);
        var cfg = BuildConfig();

        // Expected permanent failures: deterministic rule on the record index.
        static bool IsDoomed(int index) => index % 10 == 7;
        var expectedFailed = new HashSet<string>();
        foreach (var (objectType, count) in perType)
        {
            for (var i = 1; i <= count; i++)
            {
                if (IsDoomed(i))
                    expectedFailed.Add(SyntheticData.ItemId(objectType, i));
            }
        }

        var client = new HarnessGraphClient { LatencyMs = latency };
        client.OnBatch = (_, payload) => payload.Select(req =>
        {
            var url = req["url"]!.ToString();
            var itemId = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
            var (_, index) = SyntheticData.ParseItemId(itemId);
            return IsDoomed(index)
                ? HarnessGraphClient.ServerError(req)
                : new JsonObject { ["id"] = req["id"]!.ToString(), ["status"] = 200 };
        }).ToList();

        StartMeasurement();
        var allocBefore = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();
        var stats = await AwaitBoundedAsync(Ingest.IngestContentAsync(cfg, client), ScenarioTimeout);
        sw.Stop();
        result.AllocMb = (GC.GetTotalAllocatedBytes() - allocBefore) / (1024.0 * 1024.0);
        result.PeakRssMb = await StopMeasurementAsync();
        result.WallSecs = sw.Elapsed.TotalSeconds;

        Check(result, stats != null, "run completed within bounded wait");
        if (stats == null)
            return result;

        result.ItemsPerSec = items / result.WallSecs;
        result.Notes.Add("pass 1: " + StatLine(stats));

        Check(result, stats.TotalFetched == items, $"fetched == {items} (actual {stats.TotalFetched})");
        Check(result, Reconciles(stats), $"stats reconcile ({StatLine(stats)})");
        Check(result, stats.FailedCount == expectedFailed.Count,
            $"failed == {expectedFailed.Count} (actual {stats.FailedCount})");
        Check(result, stats.SuccessCount == items - expectedFailed.Count,
            $"success == {items - expectedFailed.Count} (actual {stats.SuccessCount})");

        // Dead-letter JSONL contains exactly the doomed IDs, once each.
        var dlPath = SyncState.FailedRecordsPath(ConnectorId);
        var (dlEntries, corruptLines) = ReadDeadLetter(dlPath);
        var dlIds = dlEntries
            .Where(e => e["item_id"] != null)
            .Select(e => e["item_id"]!.GetValue<string>())
            .ToList();
        Check(result, corruptLines == 0,
            $"dead-letter JSONL has no corrupted lines (actual {corruptLines} unparseable — " +
            "concurrent unsynchronized appends in SyncState.AppendFailedRecords, SyncState.cs:231)");
        Check(result, _capture.DeadLetterWriteFailures == 0,
            $"no dead-letter entries diverted to log-only fallback (actual {_capture.DeadLetterWriteFailures})");
        Check(result, dlIds.Count == expectedFailed.Count && expectedFailed.SetEquals(dlIds),
            $"dead-letter contains exactly the {expectedFailed.Count} failed IDs " +
            $"(actual {dlIds.Count} intact entries, {dlIds.Distinct().Count()} distinct)");
        Check(result,
            dlEntries.All(e => (e["error"]?.GetValue<string>() ?? "").Contains("HTTP 500")),
            "every intact dead-letter entry records the HTTP 500 detail");

        // ── Retry pass: re-drive the dead-letter IDs through the (now healthy) pipeline.
        // Note: the real `retry-failed` command constructs its own GraphClient/Salesforce
        // session internally, so the harness re-runs ingest over the dead-letter IDs instead.
        // Only the intact (parseable) dead-letter entries can be retried — which is
        // exactly why the corruption above matters in production.
        var retryIds = dlIds.Distinct().ToList();
        var retryRecordsByType = new Dictionary<string, List<JsonObject>>();
        foreach (var id in retryIds)
        {
            var (objectType, index) = SyntheticData.ParseItemId(id);
            if (!retryRecordsByType.TryGetValue(objectType, out var list))
                retryRecordsByType[objectType] = list = new List<JsonObject>();
            list.Add(SyntheticData.MakeRecord(objectType, index));
        }

        SyncState.LogsDir = Path.Combine(_workdir, "failures", "retry-logs");
        Directory.CreateDirectory(SyncState.LogsDir);
        Ingest.GetObjectConfigHook = name => retryRecordsByType.ContainsKey(name)
            ? new SalesforceObjectConfig(ObjectType: name, Fields: new[] { "Id" })
            : null;
        Ingest.IterObjectChunksHook = (_, objCfg, _, chunkSize) =>
            SyntheticData.ChunkListAsync(
                retryRecordsByType.GetValueOrDefault(objCfg.ObjectType) ?? new List<JsonObject>(),
                chunkSize);

        var retryClient = new HarnessGraphClient { LatencyMs = latency };
        var retryStats = await AwaitBoundedAsync(Ingest.IngestContentAsync(cfg, retryClient), ScenarioTimeout);
        Check(result, retryStats != null, "retry pass completed within bounded wait");
        if (retryStats == null)
            return result;

        result.Notes.Add($"retry pass over {retryIds.Count} recoverable dead-letter ID(s): " + StatLine(retryStats));
        Check(result, retryStats.TotalFetched == retryIds.Count,
            $"retry pass re-fetched all {retryIds.Count} recoverable dead-letter IDs (actual {retryStats.TotalFetched})");
        Check(result, retryStats.FailedCount == 0,
            $"retry pass recovered to zero failures (actual {retryStats.FailedCount})");
        Check(result, retryStats.SuccessCount == retryIds.Count,
            $"retry pass succeeded for all {retryIds.Count} retried IDs (actual {retryStats.SuccessCount})");
        Check(result,
            ReadDeadLetter(SyncState.FailedRecordsPath(ConnectorId)).Entries.Count == 0,
            "retry pass wrote no new dead-letter entries");
        return result;
    }

    // ── Scenario 4: stop-resume ──────────────────────────────────────────────

    public async Task<ScenarioResult> RunStopResumeAsync(int items)
    {
        var result = new ScenarioResult { Name = "stop-resume", Items = items };
        var latency = Latency(25);
        result.Notes.Add($"{items} items, {latency}ms latency; ServiceStop.Request() at ~30% acked, " +
                         "then ServiceStop.Reset() + resume from checkpoint");

        FreshLogsDir("stop-resume");
        ServiceStop.Reset();
        var perType = SyntheticData.Split(items);
        InstallStandardHooks(perType);
        var cfg = BuildConfig();

        StartMeasurement();
        var allocBefore = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();

        // ── Run 1: stop at ~30% progress ─────────────────────────────────────
        var client1 = new HarnessGraphClient { LatencyMs = latency };
        var threshold = (long)(items * 0.30);
        var run1 = Task.Run(() => Ingest.IngestContentAsync(cfg, client1));
        var watcher = Task.Run(async () =>
        {
            while (!run1.IsCompleted && client1.AckedTotal < threshold)
                await Task.Delay(10);
            if (!run1.IsCompleted)
                ServiceStop.Request();
        });
        var stats1 = await AwaitBoundedAsync(run1, TimeSpan.FromMinutes(3));
        await watcher;

        Check(result, stats1 != null, "run 1 returned within bounded wait after stop request");
        if (stats1 == null)
        {
            result.PeakRssMb = await StopMeasurementAsync();
            result.WallSecs = sw.Elapsed.TotalSeconds;
            return result;
        }

        result.Notes.Add($"run 1 (stopped at ~{100.0 * stats1.SuccessCount / items:F0}%): " + StatLine(stats1));
        Check(result, stats1.SuccessCount < items, "run 1 actually stopped early");
        Check(result, Reconciles(stats1), $"run 1 stats reconcile ({StatLine(stats1)})");
        Check(result, stats1.FailedCount == 0, $"run 1 zero failures (actual {stats1.FailedCount})");
        var checkpoint = SyncState.ReadCheckpoint(ConnectorId);
        Check(result, checkpoint != null, "checkpoint file exists after stop");
        Check(result, client1.Acked.Values.All(v => v == 1), "run 1: no duplicate PUT acks");
        Check(result, client1.Acked.Count == stats1.SuccessCount,
            $"run 1: acked PUTs ({client1.Acked.Count}) == success count ({stats1.SuccessCount})");

        // ── Run 2: resume ────────────────────────────────────────────────────
        ServiceStop.Reset();
        var client2 = new HarnessGraphClient { LatencyMs = latency };
        var stats2 = await AwaitBoundedAsync(Ingest.IngestContentAsync(cfg, client2), ScenarioTimeout);
        sw.Stop();
        result.AllocMb = (GC.GetTotalAllocatedBytes() - allocBefore) / (1024.0 * 1024.0);
        result.PeakRssMb = await StopMeasurementAsync();
        result.WallSecs = sw.Elapsed.TotalSeconds;

        Check(result, stats2 != null, "run 2 (resume) completed within bounded wait");
        if (stats2 == null)
            return result;

        result.ItemsPerSec = items / result.WallSecs;
        result.Notes.Add("run 2 (resume): " + StatLine(stats2));

        Check(result, Reconciles(stats2), $"run 2 stats reconcile ({StatLine(stats2)})");
        Check(result, stats2.SkippedCount == stats1.SuccessCount,
            $"resume skipped exactly the checkpointed items ({stats2.SkippedCount} skipped " +
            $"vs {stats1.SuccessCount} ingested in run 1)");
        Check(result, stats1.SuccessCount + stats2.SuccessCount == items,
            $"success across both runs == {items} " +
            $"({stats1.SuccessCount} + {stats2.SuccessCount} = {stats1.SuccessCount + stats2.SuccessCount})");

        // Exactly-once across both runs: union of acked IDs covers all items, no overlap.
        var doubleIngested = client1.Acked.Keys.Count(id => client2.Acked.ContainsKey(id));
        var union = client1.Acked.Count + client2.Acked.Count - doubleIngested;
        Check(result, doubleIngested == 0, $"no items double-ingested (overlap {doubleIngested})");
        Check(result, union == items, $"no items lost ({union} unique IDs acked of {items})");
        Check(result, SyncState.ReadCheckpoint(ConnectorId) == null, "checkpoint cleared after resume run");
        return result;
    }
}
