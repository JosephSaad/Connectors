// AlertingHandlerFactoryTests.cs
// -----------------------------
// Alerting.HandlerFactory is what lets four connectors share the alerting logic
// while keeping four different transports. These pin the two properties that
// makes safe: the host's factory is actually used, and a factory that fails
// degrades instead of taking the process down from a static property getter.

using System.Net;
using Connector.Chassis;

namespace SeismicConnector.Tests;

public class AlertingHandlerFactoryTests : IDisposable
{
    private readonly Func<HttpMessageHandler>? _previous = Alerting.HandlerFactory;

    public void Dispose()
    {
        Alerting.HandlerFactory = _previous;
        Alerting.HttpClient = null!;
    }

    private sealed class MarkerHandler : HttpMessageHandler
    {
        public static bool Used;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Used = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public void HostFactoryIsUsedInsteadOfTheChassisTransport()
    {
        var called = 0;
        Alerting.HandlerFactory = () => { called++; return new MarkerHandler(); };
        Alerting.HttpClient = null!;      // force reconstruction

        _ = Alerting.HttpClient;

        Assert.Equal(1, called);
    }

    [Fact]
    public void AFailingFactoryDegradesRatherThanThrowing()
    {
        // The whole point of the guard: alerting is how an operator learns
        // something else broke, so it must never be the thing that crashes.
        Alerting.HandlerFactory = () => throw new InvalidOperationException("bad CA bundle");
        Alerting.HttpClient = null!;

        var client = Alerting.HttpClient;   // must not throw

        Assert.NotNull(client);
        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
    }

    [Fact]
    public void ANullReturningFactoryAlsoDegrades()
    {
        Alerting.HandlerFactory = () => null!;
        Alerting.HttpClient = null!;

        Assert.NotNull(Alerting.HttpClient);
    }
}
