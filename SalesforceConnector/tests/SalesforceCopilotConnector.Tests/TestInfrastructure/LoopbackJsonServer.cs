// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

internal sealed record RecordedRequest(string Method, string PathAndQuery, string? Authorization, string Body);

/// <summary>
/// Minimal scriptable JSON endpoint on a loopback port, used by the HTTP
/// transport suites (Graph and Salesforce) to drive the REAL clients over the
/// real wire. The Nth request receives <c>Script(N, request)</c>'s response;
/// every request is recorded verbatim for assertions.
/// </summary>
internal sealed class LoopbackJsonServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly object _lock = new();
    private int _count;

    public List<RecordedRequest> Requests { get; } = new();

    public Func<int, RecordedRequest, (int Status, string Body, Dictionary<string, string>? Headers)> Script { get; set; }
        = (_, _) => (200, "{}", null);

    public string BaseUrl { get; }

    public LoopbackJsonServer()
    {
        // HttpListener cannot bind port 0; reserve a free one first.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseUrl = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }  // listener disposed — test is over

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            var recorded = new RecordedRequest(
                context.Request.HttpMethod,
                context.Request.Url!.PathAndQuery,
                context.Request.Headers["Authorization"],
                body);
            int n;
            lock (_lock)
            {
                Requests.Add(recorded);
                n = _count++;
            }

            var (status, responseBody, headers) = Script(n, recorded);
            context.Response.StatusCode = status;
            if (headers != null)
                foreach (var (key, value) in headers)
                    context.Response.Headers[key] = value;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* already down */ }
    }
}
