// TestHelpers.cs — shared fakes and builders. No test touches the network.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

/// <summary>Builds AppConfig objects for tests without touching the environment.</summary>
public static class TestConfig
{
    public static AppConfig Build(
        string connectorId = "SeismicSales",
        ExclusionRules? exclusions = null,
        string fallbackAcl = "skip",
        IEnumerable<string>? objects = null,
        bool enrichUsage = false,
        bool liveDocFieldIndexing = false,
        bool permissionReacl = false,
        bool classification = false,
        bool classificationEnforceAcl = false,
        string? classificationEnforceGroup = null,
        int graphItemTtlDays = 0,
        int chunkSize = 10,
        int graphBatchSize = 5,
        int batchWorkers = 2,
        bool contentGate = false,
        string? contentGateIcapUrl = null,
        string contentGateBinaryFailMode = "closed",
        string contentGateTextFailMode = "open",
        long contentGateMaxScanBytes = 25L * 1024 * 1024,
        ClassificationRules? contentGateRules = null)
    {
        return new AppConfig
        {
            ContentGate = new SeismicConnector.Security.ContentGateSettings
            {
                Enabled = contentGate,
                IcapUrl = contentGateIcapUrl,
                BinaryFailMode = contentGateBinaryFailMode,
                TextFailMode = contentGateTextFailMode,
                MaxScanBytes = contentGateMaxScanBytes,
            },
            // Mirrors AppConfig.Load: the ruleset is only populated when the
            // gate is on, so a gate-off config reads no rules at all.
            ContentGateRules = contentGate
                ? contentGateRules ?? SeismicConnector.Security.InjectionRules.Default()
                : contentGateRules ?? new ClassificationRules(),
            Connector = new ConnectorSettings
            {
                Id = connectorId,
                Name = "Seismic Sales Content",
                Description = "Test connector",
            },
            Seismic = new SeismicSettings
            {
                Tenant = "contoso",
                ApiBaseUrl = "https://api.seismic.local",
                TokenUrl = "https://auth.seismic.local/token",
                ClientId = "client",
                ClientSecret = "secret",
                FallbackAcl = fallbackAcl,
                EnrichUsage = enrichUsage,
                LiveDocFieldIndexing = liveDocFieldIndexing,
                PermissionReacl = permissionReacl,
                Classification = classification,
                ClassificationEnforceAcl = classificationEnforceAcl,
                ClassificationEnforceGroup = classificationEnforceGroup,
                GraphItemTtlDays = graphItemTtlDays,
            },
            Graph = new GraphSettings
            {
                TenantId = "tenant-guid",
                ClientId = "graph-client",
                ClientSecret = "graph-secret",
                MaxRetries = 3,
                RetryBackoffBase = 2,
            },
            Ingest = new IngestSettings
            {
                ChunkSize = chunkSize,
                GraphBatchSize = graphBatchSize,
                BatchWorkers = batchWorkers,
            },
            Schema = new SchemaConfig
            {
                Objects = (objects ?? new[] { "ContentItem", "Library" })
                    .Select(name => new SchemaObject { Name = name, Enabled = true })
                    .ToList(),
            },
            Exclusions = exclusions ?? new ExclusionRules(),
            Classification = ClassificationRules.Default(),
            ConfigDir = Path.Combine(AppContext.BaseDirectory, "config"),
        };
    }
}

/// <summary>Scripted HttpMessageHandler: route prefix → responder. Records every request.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, string Url, string? Body);

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, string?, HttpResponseMessage> Respond)> _routes = new();

    /// <summary>
    /// Recorded requests. Appends are serialized by <see cref="_recordGate"/>
    /// because the ingest pipeline dispatches concurrent $batch workers —
    /// unsynchronized List.Add would drop/corrupt entries at scale and break
    /// exact-count assertions. Enumerate only when the client is quiescent.
    /// </summary>
    public List<Recorded> Requests { get; } = new();

    private readonly object _recordGate = new();

    public void When(
        Func<HttpRequestMessage, bool> match,
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond) =>
        _routes.Add((match, respond));

    /// <summary>Convenience: match method + URL substring.</summary>
    public void When(HttpMethod method, string urlContains, Func<HttpRequestMessage, string?, HttpResponseMessage> respond) =>
        When(
            request => request.Method == method
                && request.RequestUri!.ToString().Contains(urlContains, StringComparison.Ordinal),
            respond);

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_recordGate)
            Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(), body));

        foreach (var (match, respond) in _routes)
        {
            if (match(request))
                return respond(request, body);
        }
        return Json(HttpStatusCode.NotFound, """{"error":{"message":"no route"}}""");
    }

    /// <summary>Standard success responder for Graph /$batch: every request id → 200.</summary>
    public static HttpResponseMessage BatchSuccess(HttpRequestMessage request, string? body)
    {
        var responses = new JsonArray();
        if (body is not null)
        {
            var envelope = JsonNode.Parse(body)!.AsObject();
            foreach (var req in envelope["requests"]!.AsArray())
            {
                responses.Add(new JsonObject
                {
                    ["id"] = req!["id"]!.GetValue<string>(),
                    ["status"] = 200,
                });
            }
        }
        return Json(HttpStatusCode.OK, new JsonObject { ["responses"] = responses }.ToJsonString());
    }
}

/// <summary>In-memory SeismicClient: canned teamsites/contents, no HTTP.</summary>
public class FakeSeismicClient : SeismicClient
{
    public List<SeismicTeamsite> Teamsites { get; } = new();
    public Dictionary<string, List<SeismicContent>> ContentsByTeamsite { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
    public List<SeismicUser> Users { get; } = new();
    public List<SeismicGroup> Groups { get; } = new();
    public Dictionary<string, SeismicContentUsage> UsageByContentId { get; } = new(StringComparer.Ordinal);
    public int UsageFetches { get; private set; }
    public bool UsageFetchThrows { get; set; }

    public FakeSeismicClient()
        : base(TestConfig.Build().Seismic, new FakeHttpHandler())
    {
        OverrideAccessToken = "fake-token";
    }

    public override Task<List<SeismicTeamsite>> GetTeamsitesAsync(CancellationToken ct = default) =>
        Task.FromResult(Teamsites.ToList());

    public override Task<List<SeismicContent>> GetContentsAsync(
        string teamsiteId, DateTime? modifiedSince = null, CancellationToken ct = default)
    {
        var contents = ContentsByTeamsite.TryGetValue(teamsiteId, out var list)
            ? list
            : new List<SeismicContent>();
        if (modifiedSince is not null)
            contents = contents.Where(c => c.ModifiedAt is null || c.ModifiedAt >= modifiedSince).ToList();
        return Task.FromResult(contents.ToList());
    }

    public override Task<SeismicContent?> GetContentAsync(
        string teamsiteId, string contentId, CancellationToken ct = default)
    {
        var content = ContentsByTeamsite.TryGetValue(teamsiteId, out var list)
            ? list.FirstOrDefault(c => c.Id == contentId)
            : null;
        return Task.FromResult(content);
    }

    public override Task<byte[]?> DownloadContentAsync(
        string teamsiteId, string contentId, CancellationToken ct = default) =>
        Task.FromResult(Payloads.TryGetValue(contentId, out var payload) ? payload : null);

    public override Task<Dictionary<string, SeismicContentUsage>> GetContentUsageAsync(
        CancellationToken ct = default)
    {
        UsageFetches++;
        if (UsageFetchThrows)
        {
            // The REAL client swallows analytics failures; the fake mimics that
            // contract so the pipeline test exercises the degraded path.
            return Task.FromResult(new Dictionary<string, SeismicContentUsage>(StringComparer.Ordinal));
        }
        return Task.FromResult(new Dictionary<string, SeismicContentUsage>(UsageByContentId, StringComparer.Ordinal));
    }

    public override Task<List<SeismicUser>> GetUsersAsync(CancellationToken ct = default) =>
        Task.FromResult(Users.ToList());

    public override Task<List<SeismicGroup>> GetGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult(Groups.ToList());

    /// <summary>LiveDoc field metadata by content id (only fetched for LiveDoc items).</summary>
    public Dictionary<string, List<SeismicLiveDocField>> LiveDocFieldsByContentId { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Content ids the LiveDoc-field endpoint was actually called for.</summary>
    public List<string> LiveDocFieldFetches { get; } = new();

    public bool LiveDocFetchThrows { get; set; }

    public override Task<List<SeismicLiveDocField>> GetLiveDocFieldsAsync(
        string teamsiteId, string contentId, CancellationToken ct = default)
    {
        LiveDocFieldFetches.Add(contentId);
        if (LiveDocFetchThrows)
        {
            // The REAL client swallows fetch failures; the fake mimics that
            // contract so the pipeline exercises the degraded path.
            return Task.FromResult(new List<SeismicLiveDocField>());
        }
        return Task.FromResult(
            LiveDocFieldsByContentId.TryGetValue(contentId, out var fields)
                ? new List<SeismicLiveDocField>(fields)
                : new List<SeismicLiveDocField>());
    }
}

/// <summary>Redirects SyncState.LogsDir (and cleans env) for a test's lifetime.</summary>
public sealed class TempStateDir : IDisposable
{
    public string Dir { get; }
    private readonly string _previousLogsDir;

    public TempStateDir()
    {
        Dir = Path.Combine(Path.GetTempPath(), "seismic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        _previousLogsDir = SyncState.LogsDir;
        SyncState.LogsDir = Dir;
        SyncState.ResetProviderCache();  // ensure the file backend is active
    }

    public void Dispose()
    {
        SyncState.LogsDir = _previousLogsDir;
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch
        {
        }
    }
}

public static class TestContent
{
    public static SeismicContent Make(
        string id,
        string teamsiteId = "ts1",
        string versionId = "v1",
        string status = "published",
        DateTime? expiresAt = null,
        List<SeismicProperty>? properties = null,
        List<SeismicPermission>? permissions = null,
        string format = "txt",
        DateTime? modifiedAt = null) => new()
    {
        Id = id,
        TeamsiteId = teamsiteId,
        Name = $"Item {id}",
        Description = $"Description of {id}",
        Format = format,
        Status = status,
        CurrentVersionId = versionId,
        ExpiresAt = expiresAt,
        ModifiedAt = modifiedAt ?? DateTime.UtcNow.AddDays(-1),
        Properties = properties ?? new List<SeismicProperty>(),
        Permissions = permissions ?? new List<SeismicPermission>
        {
            new() { PrincipalId = "seismic-user-1", PrincipalType = "user", Email = "amy@contoso.com" },
        },
    };
}
