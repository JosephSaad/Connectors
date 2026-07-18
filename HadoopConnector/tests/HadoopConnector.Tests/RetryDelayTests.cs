using HadoopConnector.Graph;

namespace HadoopConnector.Tests;

public class RetryDelayTests
{
    [Theory]
    [InlineData(2.0, 0, 2.0)]
    [InlineData(2.0, 1, 4.0)]
    [InlineData(2.0, 2, 8.0)]
    [InlineData(2.0, 3, 16.0)]
    [InlineData(2.0, 10, 60.0)]  // capped
    [InlineData(1.0, 0, 1.0)]
    public void ComputeBackoff_ExponentialWithCap(double backoffBase, int attempt, double expected)
    {
        Assert.Equal(expected, RetryDelay.ComputeBackoff(backoffBase, attempt), 6);
    }

    [Fact]
    public void ApplyJitter_Disabled_IsIdentity()
    {
        Assert.Equal(10.0, RetryDelay.ApplyJitter(10.0, jitterEnabled: false, uniformSample: 0.999), 9);
        Assert.Equal(10.0, RetryDelay.ApplyJitter(10.0, jitterEnabled: false, uniformSample: 0.0), 9);
    }

    [Theory]
    [InlineData(0.0, 8.0)]    // lower bound: delay * 0.8
    [InlineData(0.5, 10.0)]   // midpoint: unchanged
    [InlineData(1.0, 12.0)]   // upper bound (exclusive in practice): delay * 1.2
    public void ApplyJitter_Enabled_MapsUniformSampleToPlusMinus20Percent(double sample, double expected)
    {
        Assert.Equal(expected, RetryDelay.ApplyJitter(10.0, jitterEnabled: true, uniformSample: sample), 6);
    }

    [Fact]
    public void Jitter_OffByDefault()
    {
        using var scope = new EnvScope(("GRAPH_RETRY_JITTER", null));
        Assert.False(RetryDelay.JitterEnabled);
        Assert.Equal(7.0, RetryDelay.Jitter(7.0), 9);
    }

    [Fact]
    public void Jitter_Enabled_StaysWithinBounds()
    {
        using var scope = new EnvScope(("GRAPH_RETRY_JITTER", "true"));
        Assert.True(RetryDelay.JitterEnabled);
        for (var i = 0; i < 200; i++)
        {
            var jittered = RetryDelay.Jitter(10.0);
            Assert.InRange(jittered, 8.0, 12.0);
        }
    }

    [Fact]
    public void ForAttempt_HonorsServerRetryAfter_NeverJittered()
    {
        using var scope = new EnvScope(("GRAPH_RETRY_JITTER", "true"));
        // A server Retry-After under the cap is honoured exactly — never jittered.
        Assert.Equal(42.0, RetryDelay.ForAttempt(2.0, 0, retryAfterSeconds: 42.0, out var clamped), 9);
        Assert.False(clamped);
        Assert.Equal(0.0, RetryDelay.ForAttempt(2.0, 5, retryAfterSeconds: 0.0), 9);
    }

    [Fact]
    public void ForAttempt_ClampsAbsurdRetryAfterTo60s()
    {
        using var scope = new EnvScope(("GRAPH_RETRY_JITTER", null));
        // 429 hardening: a poisoned/absurd Retry-After must not park the crawl.
        Assert.Equal(60.0, RetryDelay.ForAttempt(2.0, 1, retryAfterSeconds: 600.0, out var clamped), 9);
        Assert.True(clamped);
        Assert.Equal(60.0, RetryDelay.MaxRetryWaitSeconds, 9);
        // At exactly the cap there is nothing to clamp.
        Assert.Equal(60.0, RetryDelay.ForAttempt(2.0, 1, retryAfterSeconds: 60.0, out var atCap), 9);
        Assert.False(atCap);
    }

    [Fact]
    public void ForAttempt_UsesComputedBackoffWhenNoRetryAfter()
    {
        using var scope = new EnvScope(("GRAPH_RETRY_JITTER", null));
        Assert.Equal(4.0, RetryDelay.ForAttempt(2.0, 1, retryAfterSeconds: null), 9);
    }
}
