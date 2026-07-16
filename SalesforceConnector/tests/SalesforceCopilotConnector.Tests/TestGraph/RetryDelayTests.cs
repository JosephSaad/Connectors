// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

/// <summary>
/// GRAPH_RETRY_JITTER delay bounds (Graph/RetryDelay.cs). The delay computation
/// is a pure function, so no sleeps are involved. Env-flipping tests join the
/// "EnvVars" collection.
/// </summary>
[Collection("EnvVars")]
public sealed class RetryDelayTests : IDisposable
{
    private readonly string? _saved;

    public RetryDelayTests()
    {
        _saved = Environment.GetEnvironmentVariable("GRAPH_RETRY_JITTER");
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", _saved);
    }

    // ── Pure seam: ApplyJitter ───────────────────────────────────────────────

    [Fact]
    public void DisabledReturnsDelayBitIdentical()
    {
        // Default behaviour must be byte-identical: no scaling at all.
        Assert.Equal(16.0, RetryDelay.ApplyJitter(16.0, jitterEnabled: false, uniformSample: 0.0));
        Assert.Equal(16.0, RetryDelay.ApplyJitter(16.0, jitterEnabled: false, uniformSample: 0.999));
        Assert.Equal(2.0, RetryDelay.ApplyJitter(2.0, jitterEnabled: false, uniformSample: 0.5));
    }

    [Fact]
    public void EnabledMapsSampleExtremesToPlusMinusTwentyPercent()
    {
        // sample 0 → −20%, sample 0.5 → unchanged, sample → 1 approaches +20%.
        Assert.Equal(8.0, RetryDelay.ApplyJitter(10.0, jitterEnabled: true, uniformSample: 0.0), precision: 10);
        Assert.Equal(10.0, RetryDelay.ApplyJitter(10.0, jitterEnabled: true, uniformSample: 0.5), precision: 10);
        Assert.Equal(12.0, RetryDelay.ApplyJitter(10.0, jitterEnabled: true, uniformSample: 1.0), precision: 10);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(16.0)]
    [InlineData(60.0)]
    public void EnabledStaysWithinBoundsForAnySample(double delay)
    {
        for (var i = 0; i <= 100; i++)
        {
            var sample = i / 100.0;
            var jittered = RetryDelay.ApplyJitter(delay, jitterEnabled: true, uniformSample: sample);
            const double eps = 1e-9;  // floating-point headroom at the sample=1.0 extreme
            Assert.InRange(jittered, delay * 0.8 - eps, delay * 1.2 + eps);
        }
    }

    // ── Env-driven wrapper: Jitter ───────────────────────────────────────────

    [Fact]
    public void EnvUnsetJitterIsIdentity()
    {
        Assert.False(RetryDelay.JitterEnabled);
        Assert.Equal(16.0, RetryDelay.Jitter(16.0));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("banana")]
    public void EnvNotTrueJitterIsIdentity(string value)
    {
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", value);
        Assert.False(RetryDelay.JitterEnabled);
        Assert.Equal(16.0, RetryDelay.Jitter(16.0));
    }

    [Fact]
    public void EnvTrueJitterStaysWithinBoundsAndVaries()
    {
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", "TRUE");
        Assert.True(RetryDelay.JitterEnabled);
        var samples = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var jittered = RetryDelay.Jitter(10.0);
            Assert.InRange(jittered, 8.0, 12.0);
            samples.Add(jittered);
        }
        // Uniform draws over ±20% of 10s cannot all be equal.
        Assert.True(samples.Distinct().Count() > 1, "jittered delays should vary");
    }
}
