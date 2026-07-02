// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for Infrastructure/Metrics.cs (#9): counter/gauge math and the
// Prometheus text-exposition rendering.

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

/// <summary>
/// Metrics is process-global static state; the runner does not parallelize
/// collections (xunit.runner.json), and each test resets the registry first.
/// </summary>
public sealed class MetricsTests
{
    public MetricsTests() => Metrics.ResetForTests();

    [Fact]
    public void CountersStartAtZero()
    {
        Metrics.ResetForTests();
        Assert.Equal(0, Metrics.ItemsIngested);
        Assert.Equal(0, Metrics.ItemsFailed);
        Assert.Equal(0, Metrics.ItemsDeleted);
        Assert.Equal(0, Metrics.ItemsSkipped);
        Assert.Equal(0, Metrics.CrawlsStarted);
        Assert.Equal(0, Metrics.CrawlsCompleted);
        Assert.Equal(0, Metrics.Throttle429Total);
        Assert.Equal(0, Metrics.DeadLetterDepth);
        Assert.Equal(0, Metrics.LastCrawlCompletedUnix);
    }

    [Fact]
    public void IncrementsAccumulate()
    {
        Metrics.ResetForTests();
        Metrics.IncItemsIngested();          // +1
        Metrics.IncItemsIngested(4);         // +4
        Metrics.IncItemsFailed(2);
        Metrics.IncItemsDeleted(3);
        Metrics.IncItemsSkipped(5);
        Metrics.IncCrawlsStarted();
        Metrics.IncCrawlsCompleted();
        Metrics.IncThrottle429(7);

        Assert.Equal(5, Metrics.ItemsIngested);
        Assert.Equal(2, Metrics.ItemsFailed);
        Assert.Equal(3, Metrics.ItemsDeleted);
        Assert.Equal(5, Metrics.ItemsSkipped);
        Assert.Equal(1, Metrics.CrawlsStarted);
        Assert.Equal(1, Metrics.CrawlsCompleted);
        Assert.Equal(7, Metrics.Throttle429Total);
    }

    [Fact]
    public void GaugeSetOverwrites()
    {
        Metrics.ResetForTests();
        Metrics.SetDeadLetterDepth(11);
        Assert.Equal(11, Metrics.DeadLetterDepth);
        Metrics.SetDeadLetterDepth(4);       // set, not add
        Assert.Equal(4, Metrics.DeadLetterDepth);

        Metrics.SetLastCrawlCompletedUnix(1751457600);
        Assert.Equal(1751457600, Metrics.LastCrawlCompletedUnix);
    }

    [Fact]
    public void MarkCrawlCompletedNowSetsRecentTimestamp()
    {
        Metrics.ResetForTests();
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Metrics.MarkCrawlCompletedNow();
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(Metrics.LastCrawlCompletedUnix, before, after);
    }

    [Fact]
    public void ConcurrentIncrementsAreThreadSafe()
    {
        Metrics.ResetForTests();
        const int threads = 8;
        const int perThread = 1000;
        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
            {
                Metrics.IncItemsIngested();
            }
        });
        Assert.Equal(threads * perThread, Metrics.ItemsIngested);
    }

    [Fact]
    public void RenderPrometheusHasHelpTypeAndSampleForEveryMetric()
    {
        Metrics.ResetForTests();
        Metrics.IncItemsIngested(42);
        Metrics.SetDeadLetterDepth(3);

        var text = Metrics.RenderPrometheus();
        var lines = text.Split('\n');

        // Every metric name we expose, with its declared type.
        var expected = new (string Name, string Type)[]
        {
            ("salesforce_connector_items_ingested_total", "counter"),
            ("salesforce_connector_items_failed_total", "counter"),
            ("salesforce_connector_items_deleted_total", "counter"),
            ("salesforce_connector_items_skipped_total", "counter"),
            ("salesforce_connector_crawls_started_total", "counter"),
            ("salesforce_connector_crawls_completed_total", "counter"),
            ("salesforce_connector_throttled_429_total", "counter"),
            ("salesforce_connector_dead_letter_depth", "gauge"),
            ("salesforce_connector_last_crawl_completed_timestamp_seconds", "gauge"),
            ("salesforce_connector_uptime_seconds", "gauge"),
        };

        foreach (var (name, type) in expected)
        {
            Assert.Contains($"# HELP {name} ", text, StringComparison.Ordinal);
            Assert.Contains($"# TYPE {name} {type}", text, StringComparison.Ordinal);
            // A sample line: "<name> <value>" with no extra leading token.
            Assert.Contains(lines, l => l.StartsWith(name + " ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RenderPrometheusReflectsCounterValues()
    {
        Metrics.ResetForTests();
        Metrics.IncItemsIngested(42);
        Metrics.IncItemsFailed(3);
        Metrics.SetDeadLetterDepth(9);

        var text = Metrics.RenderPrometheus();

        Assert.Contains("\nsalesforce_connector_items_ingested_total 42\n", text, StringComparison.Ordinal);
        Assert.Contains("\nsalesforce_connector_items_failed_total 3\n", text, StringComparison.Ordinal);
        Assert.Contains("\nsalesforce_connector_dead_letter_depth 9\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPrometheusUsesInvariantNumberFormatting()
    {
        Metrics.ResetForTests();
        // uptime is a double rendered with "0.###" — must use '.' not ',' regardless of culture.
        var text = Metrics.RenderPrometheus();
        var uptimeLine = text
            .Split('\n')
            .Single(l => l.StartsWith("salesforce_connector_uptime_seconds ", StringComparison.Ordinal));
        var value = uptimeLine.Split(' ')[1];
        Assert.DoesNotContain(",", value);
        Assert.True(double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out _));
    }
}
