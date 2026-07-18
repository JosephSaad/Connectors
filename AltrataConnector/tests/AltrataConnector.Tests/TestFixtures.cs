// Shared test helpers: temp dirs, delivery builders, fakes.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public static class TestFixtures
{
    /// <summary>Create a unique temp directory (auto-cleaned by the OS temp policy).</summary>
    public static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"altrata_tests_{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Sha256Of(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Write a delivery directory: data files + manifest with real checksums.</summary>
    public static Delivery WriteDelivery(string feedPath, string deliveryId,
        params (string FileName, string Dataset, string Content, int RecordCount)[] files)
    {
        var dir = Path.Combine(feedPath, deliveryId);
        Directory.CreateDirectory(dir);
        var manifestFiles = new List<ManifestFile>();
        foreach (var (fileName, dataset, content, recordCount) in files)
        {
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            manifestFiles.Add(new ManifestFile
            {
                Name = fileName,
                Dataset = dataset,
                Sha256 = Sha256Of(path),
                RecordCount = recordCount,
            });
        }
        var manifest = new Manifest
        {
            DeliveryId = deliveryId,
            DeliveryType = "full",
            GeneratedUtc = DateTime.UtcNow,
            Files = manifestFiles,
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return new Delivery(dir, manifest);
    }

    /// <summary>JSON array of simple person records: [{id, person_name, email, employer}].</summary>
    public static string PersonJson(params (string Id, string Name, string? Email, string? Employer)[] persons)
    {
        var records = persons.Select(p => new Dictionary<string, object?>
        {
            ["id"] = p.Id,
            ["person_name"] = p.Name,
            ["email"] = p.Email,
            ["employer"] = p.Employer,
        });
        return JsonSerializer.Serialize(records);
    }

    public static AppConfig NewConfig(string? feedPath = null, string? seatListPath = null,
        string? dataDir = null, int retentionDays = 0, string retentionMode = "archive",
        string? crmContactsPath = null) => new()
    {
        ConnectorId = "AltrataTest",
        ConnectorName = "Altrata Test",
        ConnectorDescription = "Test connector",
        AadClientId = "client-id",
        AadTenantId = "tenant-id",
        AadClientSecret = "secret",
        FeedPath = feedPath,
        SeatListPath = seatListPath ?? Path.Combine("config", "seats.json"),
        DataDirectory = dataDir ?? "data",
        RetentionDays = retentionDays,
        RetentionMode = retentionMode,
        CrmContactsPath = crmContactsPath,
        GraphMaxRetries = 2,
        GraphRetryBackoffBase = 0.01,
        GraphBatchSize = 2,
        // Workers = 1 keeps the superchunk equal to the batch size, so
        // checkpoint/graceful-stop tests reason about exact record positions.
        GraphBatchWorkers = 1,
    };

    public static List<SeatPrincipal> DefaultSeats() => new()
    {
        new SeatPrincipal(SeatPrincipalKind.UserUpn, "alice@contoso.com"),
        new SeatPrincipal(SeatPrincipalKind.UserObjectId, "3f2a1b7c-9d4e-4f6a-8b2c-1d5e7f9a0b3c"),
    };

    public static void WriteSeatFile(string path, params string[] users)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { users, groups = Array.Empty<string>() }));
    }

    /// <summary>Build a Runtime over fakes/local stores for command-level tests.</summary>
    public static Runtime NewRuntime(AppConfig config, FakeGraphClient graph, string tempRoot)
    {
        var state = new FileStateStore(config.ConnectorId,
            logsDir: Path.Combine(tempRoot, "logs"), dataDir: Path.Combine(tempRoot, "data"));
        var identity = new SqliteIdentityStore(Path.Combine(tempRoot, "data", "identity.db"));
        return new Runtime
        {
            Config = config,
            State = state,
            Identity = identity,
            Graph = graph,
            Seats = new SeatService(config, identity, state),
            Alerts = new Alerting(config.ConnectorId),
            Audit = new AuditLog(config.ConnectorId, logsDir: Path.Combine(tempRoot, "logs")),
            Erasure = new ErasureLedger(config.ConnectorId, logsDir: Path.Combine(tempRoot, "logs")),
            Decisions = new DecisionLedger(config.ConnectorId, logsDir: Path.Combine(tempRoot, "logs")),
            GraphBreaker = new CircuitBreaker("graph", new CircuitBreakerOptions { Critical = true }),
            ApiBreaker = new CircuitBreaker("altrata-api", new CircuitBreakerOptions { Critical = false }),
        };
    }
}

// ---- fakes ------------------------------------------------------------------

public sealed class FakeGraphClient : IGraphClient
{
    public List<ExternalItem> PutItems { get; } = new();
    public List<string> DeletedItems { get; } = new();
    public List<(string ItemId, IReadOnlyList<AclEntry> Acl)> AclUpdates { get; } = new();
    public bool ConnectionCreated { get; private set; }
    public bool SchemaRegistered { get; private set; }

    /// <summary>Item ids that should fail on PUT (simulates exhausted retries).</summary>
    public HashSet<string> FailingItemIds { get; } = new();

    /// <summary>Item ids that should fail on DELETE.</summary>
    public HashSet<string> FailingDeletes { get; } = new();

    /// <summary>When set, every Nth PUT fails (1-based).</summary>
    public Func<ExternalItem, bool>? FailWhen { get; set; }

    /// <summary>Simulated breaker state for degraded-mode crawl tests.</summary>
    public CircuitState BreakerState { get; set; } = CircuitState.Closed;

    public Task EnsureConnectionAsync(CancellationToken ct = default)
    {
        ConnectionCreated = true;
        return Task.CompletedTask;
    }

    public Task<bool> ConnectionExistsAsync(CancellationToken ct = default) =>
        Task.FromResult(ConnectionCreated);

    public Task RegisterSchemaAsync(GraphSchema schema, CancellationToken ct = default)
    {
        SchemaRegistered = true;
        return Task.CompletedTask;
    }

    public Task PutItemAsync(ExternalItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailingItemIds.Contains(item.Id) || (FailWhen?.Invoke(item) ?? false))
            throw new GraphClientException(500, $"simulated failure for {item.Id}");
        PutItems.Add(item);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BatchOpResult>> PutItemsBatchAsync(
        IReadOnlyList<ExternalItem> items, CancellationToken ct = default)
    {
        var results = new List<BatchOpResult>(items.Count);
        foreach (var item in items)
        {
            AltrataConnector.Entitlement.SeatAclBuilder.AssertNeverEveryone(item.Acl);
            try
            {
                await PutItemAsync(item, ct);
                results.Add(new BatchOpResult(item.Id, true, 200, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GraphClientException exc)
            {
                results.Add(new BatchOpResult(item.Id, false, exc.StatusCode, exc.Message));
            }
        }
        return results;
    }

    public Task UpdateItemAclAsync(string itemId, IReadOnlyList<AclEntry> acl, CancellationToken ct = default)
    {
        AclUpdates.Add((itemId, acl));
        return Task.CompletedTask;
    }

    /// <summary>Item ids whose ACL PATCH should fail.</summary>
    public HashSet<string> FailingAclUpdates { get; } = new();

    public Task<IReadOnlyList<BatchOpResult>> UpdateItemAclsBatchAsync(
        IReadOnlyList<AclUpdate> updates, CancellationToken ct = default)
    {
        var results = new List<BatchOpResult>(updates.Count);
        foreach (var update in updates)
        {
            AltrataConnector.Entitlement.SeatAclBuilder.AssertNeverEveryone(update.Acl);
            if (FailingAclUpdates.Contains(update.ItemId))
            {
                results.Add(new BatchOpResult(update.ItemId, false, 500, "simulated ACL failure"));
                continue;
            }
            AclUpdates.Add((update.ItemId, update.Acl));
            results.Add(new BatchOpResult(update.ItemId, true, 200, null));
        }
        return Task.FromResult<IReadOnlyList<BatchOpResult>>(results);
    }

    public Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        if (FailingDeletes.Contains(itemId))
            throw new GraphClientException(503, $"simulated delete failure for {itemId}");
        DeletedItems.Add(itemId);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BatchOpResult>> DeleteItemsBatchAsync(
        IReadOnlyList<string> itemIds, CancellationToken ct = default)
    {
        var results = new List<BatchOpResult>(itemIds.Count);
        foreach (var itemId in itemIds)
        {
            try
            {
                await DeleteItemAsync(itemId, ct);
                results.Add(new BatchOpResult(itemId, true, 204, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GraphClientException exc)
            {
                results.Add(new BatchOpResult(itemId, false, exc.StatusCode, exc.Message));
            }
        }
        return results;
    }
}

public sealed class FakeAlertSink : IAlertSink
{
    public List<(string Severity, string Event, string Message)> Alerts { get; } = new();

    public Task SendAsync(string severity, string @event, string message,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        Alerts.Add((severity, @event, message));
        return Task.CompletedTask;
    }

    public Task CheckDeadLetterDepthAsync(int depth) => Task.CompletedTask;
}

/// <summary>Scripted HTTP handler for GraphClient/AltrataApiClient transport tests.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public ScriptedHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _script.Enqueue(responder);
        return this;
    }

    public ScriptedHandler EnqueueJson(int status, string json,
        Action<HttpResponseMessage>? configure = null)
    {
        return Enqueue(_ =>
        {
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            configure?.Invoke(response);
            return response;
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_script.Count == 0)
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        return Task.FromResult(_script.Dequeue()(request));
    }
}
