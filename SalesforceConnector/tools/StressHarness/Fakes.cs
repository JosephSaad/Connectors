// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Self-contained test doubles for the stress harness.  Mirrors the
// CheckpointSupport fakes (tests/SalesforceCopilotConnector.Tests/
// CheckpointResumeTests.cs) without referencing the test assembly — the
// harness drives the REAL Ingest.IngestContentAsync pipeline and only fakes
// the Salesforce fetch / ACL / transform / Graph seams.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;

namespace StressHarness;

/// <summary>Converter stub with no registered objects (avoids disk config loads).</summary>
internal sealed class StubConverter : SalesforceConverter
{
    public StubConverter()
        : base(
            "https://stress.my.salesforce.com",
            config: new JsonObject { ["objectList"] = new JsonArray() })
    {
    }

    public override List<string> ObjectNames => new();

    public override SalesforceObjectHandler? GetHandler(string objectName) => null;
}

/// <summary>
/// Transformer fake — turns a synthetic Salesforce record into a realistic
/// Graph external-item payload carrying the record's ~2 KB content blob.
/// </summary>
internal sealed class HarnessTransformer : SalesforceItemTransformer
{
    public HarnessTransformer()
        : base("https://stress.my.salesforce.com", new JsonArray(), new StubConverter())
    {
    }

    public override Dictionary<string, SalesforceObjectHandler> Handlers => new();

    public override List<JsonObject> TransformRecord(
        JsonObject item, List<Dictionary<string, string>>? acl = null)
    {
        var recordId = item["Id"]!.GetValue<string>();
        var aclArray = new JsonArray();
        if (acl != null)
        {
            foreach (var entry in acl)
            {
                var node = new JsonObject();
                foreach (var (key, value) in entry)
                    node[key] = value;
                aclArray.Add(node);
            }
        }
        return new List<JsonObject>
        {
            new JsonObject
            {
                ["id"] = recordId,
                ["properties"] = new JsonObject
                {
                    ["ObjectName"] = item["objectType"]?.GetValue<string>(),
                    ["Title"] = item["Name"]?.GetValue<string>(),
                    ["Url"] = item["url"]?.GetValue<string>(),
                },
                ["acl"] = aclArray,
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["value"] = item["Description"]?.GetValue<string>() ?? "",
                },
            },
        };
    }
}

/// <summary>Legacy ACL resolver fake — resolves every record to a single grant entry.</summary>
internal sealed class HarnessAclResolver : AclResolver
{
    public HarnessAclResolver(AppConfig config)
        : base(config)
    {
    }

    public override Task<Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>>
        ResolveAsync(Dictionary<string, List<JsonObject>> recordsByObjectType)
    {
        var result = new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>();
        foreach (var (objectType, records) in recordsByObjectType)
        {
            var perRecord = new Dictionary<string, List<Dictionary<string, string>>>(records.Count);
            foreach (var record in records)
            {
                perRecord[record["Id"]!.GetValue<string>()] = new List<Dictionary<string, string>>
                {
                    new()
                    {
                        ["accessType"] = "grant",
                        ["type"] = "everyone",
                        ["value"] = "everyone",
                    },
                };
            }
            result[objectType] = perRecord;
        }
        return Task.FromResult(result);
    }

    public override Task PrewarmCachesAsync() => Task.CompletedTask;
}

/// <summary>
/// Graph client fake — simulates per-$batch latency, tracks in-flight
/// concurrency and records every successfully acknowledged item ID so
/// scenarios can assert exactly-once ingestion.
/// </summary>
internal sealed class HarnessGraphClient : GraphClient
{
    public int LatencyMs { get; init; }

    /// <summary>
    /// Soak mode: count acks without retaining every acked item id. The Acked
    /// dictionary intentionally holds one entry per item to prove exactly-once
    /// ingestion, which makes the HARNESS's own RSS grow with item count — for
    /// long-soak leak attribution that bookkeeping can be switched off so the
    /// measured footprint is the pipeline's alone.
    /// </summary>
    public bool CountOnlyAcks { get; init; }

    /// <summary>Responder: (1-based batch call index, sub-request payload) → sub-responses.</summary>
    public Func<int, List<JsonObject>, List<JsonObject>> OnBatch { get; set; } =
        (_, payload) => AllOk(payload);

    private int _calls;
    private int _inFlight;
    private int _maxInFlight;
    private long _ackedTotal;

    /// <summary>Successfully acknowledged item IDs → ack count (should always be 1).</summary>
    public ConcurrentDictionary<string, int> Acked { get; } = new();

    public int Calls => Volatile.Read(ref _calls);

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public long AckedTotal => Interlocked.Read(ref _ackedTotal);

    public override async Task<List<JsonObject>> BatchRequestsAsync(List<JsonObject> requestsPayload)
    {
        var call = Interlocked.Increment(ref _calls);
        var inFlight = Interlocked.Increment(ref _inFlight);
        int seen;
        while (inFlight > (seen = Volatile.Read(ref _maxInFlight)))
            Interlocked.CompareExchange(ref _maxInFlight, inFlight, seen);
        try
        {
            if (LatencyMs > 0)
                await Task.Delay(LatencyMs);
            var responses = OnBatch(call, requestsPayload);

            // Record acks: match each 2xx sub-response back to its sub-request URL.
            var byId = new Dictionary<string, JsonObject>(requestsPayload.Count);
            foreach (var req in requestsPayload)
                byId[req["id"]!.ToString()] = req;
            foreach (var resp in responses)
            {
                var status = resp["status"]?.GetValue<int>() ?? 0;
                if (status is < 200 or >= 300)
                    continue;
                if (!byId.TryGetValue(resp["id"]?.ToString() ?? "", out var req))
                    continue;
                if (!CountOnlyAcks)
                {
                    var url = req["url"]!.ToString();
                    var itemId = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
                    Acked.AddOrUpdate(itemId, 1, (_, v) => v + 1);
                }
                Interlocked.Increment(ref _ackedTotal);
            }
            return responses;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public static List<JsonObject> AllOk(List<JsonObject> payload)
        => payload.Select(p => (JsonObject)new JsonObject
        {
            ["id"] = p["id"]!.ToString(),
            ["status"] = 200,
        }).ToList();

    public static JsonObject Throttled(JsonObject request) => new()
    {
        ["id"] = request["id"]!.ToString(),
        ["status"] = 429,
        ["headers"] = new JsonObject { ["Retry-After"] = "1" },
        ["body"] = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = "activityLimitReached",
                ["message"] = "Too many requests (simulated)",
            },
        },
    };

    public static JsonObject ServerError(JsonObject request) => new()
    {
        ["id"] = request["id"]!.ToString(),
        ["status"] = 500,
        ["body"] = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = "internalServerError",
                ["message"] = "Simulated permanent 500",
            },
        },
    };
}

/// <summary>Deterministic synthetic Salesforce data (fixed seed, ~2 KB content per record).</summary>
internal static class SyntheticData
{
    public const int Seed = 42;

    public static readonly string[] ObjectTypes = { "Account", "Contact", "Case" };

    private static readonly Dictionary<string, string> PrefixByType = new()
    {
        ["Account"] = "ACC",
        ["Contact"] = "CON",
        ["Case"] = "CAS",
    };

    private static readonly Dictionary<string, string> TypeByPrefix =
        PrefixByType.ToDictionary(kv => kv.Value, kv => kv.Key);

    private static readonly string SharedBlob = BuildBlob(2000);

    private static string BuildBlob(int length)
    {
        const string words = "salesforce copilot connector graph ingest pipeline external item stress payload record ";
        var rng = new Random(Seed);
        var sb = new System.Text.StringBuilder(length + 16);
        while (sb.Length < length)
        {
            var start = rng.Next(words.Length - 8);
            sb.Append(words, start, 8);
        }
        return sb.ToString(0, length);
    }

    public static string ItemId(string objectType, int index)
        => $"{PrefixByType[objectType]}{index:D7}";

    public static (string ObjectType, int Index) ParseItemId(string itemId)
        => (TypeByPrefix[itemId[..3]], int.Parse(itemId[3..], CultureInfo.InvariantCulture));

    /// <summary>Deterministic split of <paramref name="total"/> across the 3 object types.</summary>
    public static Dictionary<string, int> Split(int total)
    {
        var perType = total / ObjectTypes.Length;
        var remainder = total - perType * ObjectTypes.Length;
        var result = new Dictionary<string, int>();
        for (var i = 0; i < ObjectTypes.Length; i++)
            result[ObjectTypes[i]] = perType + (i == 0 ? remainder : 0);
        return result;
    }

    public static JsonObject MakeRecord(string objectType, int index)
    {
        var id = ItemId(objectType, index);
        return new JsonObject
        {
            ["Id"] = id,
            ["objectType"] = objectType,
            ["Name"] = $"{objectType} {index}",
            ["url"] = $"https://stress.my.salesforce.com/{id}",
            ["Description"] = $"{id} | {SharedBlob}",
        };
    }

    /// <summary>Lazily generated chunks — only one chunk is materialised at a time.</summary>
    public static async IAsyncEnumerable<List<JsonObject>> ChunksAsync(
        string objectType, int count, int chunkSize)
    {
        for (var start = 1; start <= count; start += chunkSize)
        {
            var n = Math.Min(chunkSize, count - start + 1);
            var chunk = new List<JsonObject>(n);
            for (var i = 0; i < n; i++)
                chunk.Add(MakeRecord(objectType, start + i));
            yield return chunk;
            await Task.Yield();
        }
    }

    /// <summary>Chunk an in-memory record list (used by the failures retry pass).</summary>
    public static async IAsyncEnumerable<List<JsonObject>> ChunkListAsync(
        List<JsonObject> records, int chunkSize)
    {
        for (var start = 0; start < records.Count; start += chunkSize)
        {
            yield return records.Skip(start).Take(chunkSize).ToList();
            await Task.Yield();
        }
    }
}

/// <summary>
/// Log handler that observes the pipeline's internal AdaptiveConcurrency dial
/// ("Graph concurrency ramped up to N" / "concurrency reduced to N") and the
/// per-sub-batch retry delays ("Retrying N throttled items in Xs").
/// </summary>
internal sealed class ConcurrencyCapture : LogHandler
{
    public sealed record ConcurrencyEvent(double AtSecs, int Level, string Kind);

    private readonly object _lock = new();
    private readonly List<ConcurrencyEvent> _events = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _retryDelaySecs;
    private int _retryEvents;
    private long _throttledItemRetries;
    private int _errorCount;
    private long _deadLetterWriteFailures;

    private static readonly Regex RampedUp = new(@"concurrency ramped up to (\d+)", RegexOptions.Compiled);
    private static readonly Regex Reduced = new(@"concurrency reduced to (\d+)", RegexOptions.Compiled);
    private static readonly Regex Retrying = new(@"^Retrying (\d+) throttled items in (\d+)s", RegexOptions.Compiled);
    private static readonly Regex DeadLetterWriteFailure = new(
        @"^Failed to write (\d+) failed record\(s\) to dead-letter file", RegexOptions.Compiled);

    public void Reset()
    {
        lock (_lock)
        {
            _events.Clear();
            _retryDelaySecs = 0;
            _retryEvents = 0;
            _throttledItemRetries = 0;
            _errorCount = 0;
            _deadLetterWriteFailures = 0;
            _clock.Restart();
        }
    }

    public List<ConcurrencyEvent> Events
    {
        get
        {
            lock (_lock)
                return _events.ToList();
        }
    }

    public double RetryDelaySecs { get { lock (_lock) return _retryDelaySecs; } }

    public int RetryEvents { get { lock (_lock) return _retryEvents; } }

    public long ThrottledItemRetries { get { lock (_lock) return _throttledItemRetries; } }

    public int ErrorCount { get { lock (_lock) return _errorCount; } }

    /// <summary>Items diverted to log-only "UNRECORDED FAILURE" output because the
    /// dead-letter file could not be opened (e.g. Windows sharing violation).</summary>
    public long DeadLetterWriteFailures { get { lock (_lock) return _deadLetterWriteFailures; } }

    protected override void Emit(LogRecord record)
    {
        Match m;
        if ((m = Reduced.Match(record.Message)).Success)
        {
            lock (_lock)
                _events.Add(new ConcurrencyEvent(
                    _clock.Elapsed.TotalSeconds, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), "down"));
        }
        else if ((m = RampedUp.Match(record.Message)).Success)
        {
            lock (_lock)
                _events.Add(new ConcurrencyEvent(
                    _clock.Elapsed.TotalSeconds, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), "up"));
        }
        else if ((m = Retrying.Match(record.Message)).Success)
        {
            lock (_lock)
            {
                _retryEvents++;
                _throttledItemRetries += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                _retryDelaySecs += double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }
        else if ((m = DeadLetterWriteFailure.Match(record.Message)).Success)
        {
            lock (_lock)
            {
                _errorCount++;
                _deadLetterWriteFailures += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }
        else if (record.Level >= LogLevels.Error)
        {
            lock (_lock)
                _errorCount++;
        }
    }
}
