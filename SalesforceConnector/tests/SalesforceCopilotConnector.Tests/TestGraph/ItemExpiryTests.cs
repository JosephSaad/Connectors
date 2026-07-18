// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// ItemExpiryTests.cs — #8 stale-index expiry (GRAPH_ITEM_TTL_DAYS).

using System.Globalization;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

[Collection("EnvVars")]
public sealed class ItemExpiryTests : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable(ItemExpiry.TtlDaysEnvVar);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ItemExpiry.TtlDaysEnvVar, _saved);

    [Fact]
    public void ExpirationAbsentWhenUnset()
    {
        Environment.SetEnvironmentVariable(ItemExpiry.TtlDaysEnvVar, null);
        Assert.False(ItemExpiry.Enabled);
        var item = new JsonObject { ["id"] = "001" };
        var stamped = ItemExpiry.ApplyExpiry(item, DateTimeOffset.UtcNow);
        Assert.False(stamped);
        Assert.False(item.ContainsKey(ItemExpiry.ExpirationProperty));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void ExpirationAbsentForNonPositiveOrGarbage(string value)
    {
        Environment.SetEnvironmentVariable(ItemExpiry.TtlDaysEnvVar, value);
        Assert.False(ItemExpiry.Enabled);
        var item = new JsonObject { ["id"] = "001" };
        Assert.False(ItemExpiry.ApplyExpiry(item, DateTimeOffset.UtcNow));
        Assert.False(item.ContainsKey(ItemExpiry.ExpirationProperty));
    }

    [Fact]
    public void ExpirationSetToNowPlusTtlWhenConfigured()
    {
        Environment.SetEnvironmentVariable(ItemExpiry.TtlDaysEnvVar, "30");
        Assert.True(ItemExpiry.Enabled);
        Assert.Equal(30, ItemExpiry.TtlDays);

        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var item = new JsonObject { ["id"] = "001" };
        var stamped = ItemExpiry.ApplyExpiry(item, now, ItemExpiry.TtlDays);

        Assert.True(stamped);
        var iso = item[ItemExpiry.ExpirationProperty]!.GetValue<string>();
        Assert.Equal("2026-08-17T12:00:00Z", iso);
        // Parses as a real UTC instant 30 days out.
        var parsed = DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);
        Assert.Equal(now.AddDays(30).UtcDateTime, parsed.UtcDateTime);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(2_000_000_000)]
    [InlineData(3_000_000)]   // ~9999-limit boundary from a 2026 "now"
    public void HugeTtlClampsInsteadOfThrowing(int ttlDays)
    {
        // REGRESSION: a fat-fingered GRAPH_ITEM_TTL_DAYS large enough to push
        // <now + TTL> past DateTimeOffset.MaxValue used to throw
        // ArgumentOutOfRangeException from AddDays. ApplyExpiry runs inside the
        // per-item transform try/catch, so that exception turned EVERY item in the
        // crawl into a dead-lettered "transform failure". It must clamp instead.
        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var item = new JsonObject { ["id"] = "001" };

        var stamped = ItemExpiry.ApplyExpiry(item, now, ttlDays);   // must not throw

        Assert.True(stamped);
        var iso = item[ItemExpiry.ExpirationProperty]!.GetValue<string>();
        // Parses to a valid far-future instant (clamped to the max representable).
        var parsed = DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);
        Assert.True(parsed.UtcDateTime > now.UtcDateTime.AddYears(1000));
    }
}
