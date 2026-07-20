// Shared test fixtures: env-var scopes, temp directories, config factories and
// a scriptable HttpMessageHandler (no real network calls anywhere in the suite).

using System.Net;
using System.Text;
using ClarizenConnector.Config;

namespace ClarizenConnector.Tests;

/// <summary>Sets env vars for the scope and restores the previous values on dispose.</summary>
public sealed class EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new();

    public EnvScope(params (string Name, string? Value)[] vars)
    {
        foreach (var (name, value) in vars)
            Set(name, value);
    }

    public void Set(string name, string? value)
    {
        if (!_previous.ContainsKey(name))
            _previous[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>
/// Points <see cref="ClarizenConnector.Graph.GraphPropertyRegistry"/> at a
/// temporary graph-schema.json = the real deployed declaration PLUS the extra
/// property names a test needs, and restores the default on dispose.
/// <para>
/// This is how a test models an operator who extended BOTH config/schema.json
/// and config/graph-schema.json. It is deliberately the only way to get an extra
/// name accepted: the registry has no "test mode" that waves properties through,
/// because a bypass in the guard is the defect the guard exists to prevent.
/// </para>
/// </summary>
public sealed class GraphSchemaScope : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _previous;

    public GraphSchemaScope(params string[] extraPropertyNames)
    {
        _previous = ClarizenConnector.Graph.GraphPropertyRegistry.OverridePath;

        var baseline = System.Text.Json.Nodes.JsonNode
            .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json")))!
            .AsArray();
        var extended = new System.Text.Json.Nodes.JsonArray();
        foreach (var property in baseline.ToList())
        {
            baseline.Remove(property);
            extended.Add(property!);
        }
        foreach (var name in extraPropertyNames)
        {
            extended.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = name,
                ["type"] = "String",
                ["isSearchable"] = true,
                ["isRetrievable"] = true,
                ["isQueryable"] = false,
                ["isRefinable"] = false,
                ["description"] = "Test-scoped extension property.",
            });
        }

        var path = Path.Combine(_dir.Path, "graph-schema.json");
        File.WriteAllText(path, extended.ToJsonString());
        ClarizenConnector.Graph.GraphPropertyRegistry.OverridePath = path;
    }

    public void Dispose()
    {
        ClarizenConnector.Graph.GraphPropertyRegistry.OverridePath = _previous;
        _dir.Dispose();
    }
}

/// <summary>Creates (and deletes on dispose) a unique temp directory.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "clzconn_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>Redirects SyncState.LogsDir to a temp dir for the scope.</summary>
public sealed class SyncStateScope : IDisposable
{
    private readonly string _previousLogsDir;
    private readonly TempDir _dir = new();

    public SyncStateScope()
    {
        _previousLogsDir = SyncState.LogsDir;
        SyncState.LogsDir = _dir.Path;
        SyncState.ResetProviderCache();
    }

    public string LogsDir => _dir.Path;

    public void Dispose()
    {
        SyncState.LogsDir = _previousLogsDir;
        SyncState.ResetProviderCache();
        _dir.Dispose();
    }
}

public static class TestConfig
{
    public static AppConfig Make(
        string connectorId = "ClarizenAdaptiveWork",
        int graphMaxRetries = 4,
        double backoffBase = 2.0,
        int ingestChunkSize = 200,
        int graphBatchSize = 20,
        string financialMode = "tag",
        string? financialGroupId = null,
        string? tdwExportPath = null,
        int queryLimit = 0,
        bool attachmentIngestion = false,
        long attachmentMaxBytes = 10L * 1024 * 1024,
        IReadOnlySet<string>? attachmentAllowedTypes = null,
        bool classification = false,
        bool classificationManifest = false,
        bool classificationEnforceAcl = false,
        string? classificationRestrictedGroupId = null,
        bool contentGate = false,
        string? contentGateIcapUrl = null,
        string contentGateBinaryFailMode = "closed",
        string contentGateTextFailMode = "open",
        int contentGateMaxScanMb = 16,
        int graphItemTtlDays = 0) => new()
    {
        ConnectorId = connectorId,
        ConnectorName = "Clarizen AdaptiveWork",
        ConnectorDescription = "Test connector",
        ClarizenBaseUrl = "https://api.clarizen.example/v2.0/services",
        ClarizenUsername = "svc@example.com",
        ClarizenPassword = "pw",
        AadTenantId = "11111111-1111-1111-1111-111111111111",
        AadClientId = "22222222-2222-2222-2222-222222222222",
        AadClientSecret = "secret",
        GraphMaxRetries = graphMaxRetries,
        GraphRetryBackoffBase = backoffBase,
        IngestChunkSize = ingestChunkSize,
        GraphBatchSize = graphBatchSize,
        FinancialDataMode = financialMode,
        FinancialDataGroupId = financialGroupId,
        TdwExportPath = tdwExportPath,
        ClarizenQueryLimit = queryLimit,
        AttachmentIngestion = attachmentIngestion,
        AttachmentMaxBytes = attachmentMaxBytes,
        AttachmentAllowedTypes = attachmentAllowedTypes ?? AppConfig.DefaultAttachmentTypes,
        Classification = classification,
        ClassificationManifest = classificationManifest,
        ClassificationEnforceAcl = classificationEnforceAcl,
        ClassificationRestrictedGroupId = classificationRestrictedGroupId,
        ContentGateEnabled = contentGate,
        ContentGateIcapUrl = contentGateIcapUrl,
        ContentGateBinaryFailMode = contentGateBinaryFailMode,
        ContentGateTextFailMode = contentGateTextFailMode,
        ContentGateMaxScanMb = contentGateMaxScanMb,
        GraphItemTtlDays = graphItemTtlDays,
    };
}

/// <summary>Scriptable HTTP handler: routes each request through a delegate.</summary>
public sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public List<(string Url, string Body)> Requests { get; } = new();

    public MockHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.ToString(), body));
        return _responder(request, body);
    }
}
