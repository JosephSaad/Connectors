// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/ItemExpiry.cs
// -------------------
// #8 — optional stale-index expiry. When GRAPH_ITEM_TTL_DAYS is set to a
// positive integer, every ingested external item is stamped with
//
//     expirationDateTime = <ingest time> + TTL days   (UTC, ISO-8601 'Z')
//
// so the Microsoft Graph index self-expires items that stop being refreshed.
// This is defense-in-depth AFTER an outage: if crawling stops (the connector
// host dies, credentials lapse, the source goes offline) the index does not
// keep serving stale — and potentially over-permissioned — content forever;
// each item ages out on its own once TTL elapses without a re-ingest.
//
// A healthy connector re-stamps every item on each crawl, pushing the
// expiration forward, so items never expire while crawling is alive. Choose a
// TTL comfortably larger than the crawl cadence (e.g. several times the full-
// crawl interval) so a single missed crawl never expires live content.
//
// Default UNSET ⇒ no expirationDateTime is written and behavior is byte-
// identical to today.

using System.Globalization;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Graph;

/// <summary>Optional TTL stamping for ingested external items (GRAPH_ITEM_TTL_DAYS).</summary>
public static class ItemExpiry
{
    public const string TtlDaysEnvVar = "GRAPH_ITEM_TTL_DAYS";

    /// <summary>Top-level item property carrying the Graph expiry timestamp.</summary>
    public const string ExpirationProperty = "expirationDateTime";

    /// <summary>
    /// Configured TTL in days, or <c>null</c> when unset / blank / non-positive /
    /// unparseable (feature off — no expiry stamped). Live env read.
    /// </summary>
    public static int? TtlDays
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(TtlDaysEnvVar);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
                return days;
            return null;
        }
    }

    /// <summary>True when a positive GRAPH_ITEM_TTL_DAYS is configured.</summary>
    public static bool Enabled => TtlDays is > 0;

    /// <summary>
    /// Stamp <paramref name="item"/> with <c>expirationDateTime = <paramref name="now"/> + TTL</c>
    /// when configured. Returns <c>true</c> if a stamp was written. No-op (returns
    /// <c>false</c>) when the feature is off or <paramref name="ttlDays"/> is null —
    /// callers that already read the setting once per chunk pass it in to avoid a
    /// per-item env read on the hot path.
    /// </summary>
    public static bool ApplyExpiry(JsonObject item, DateTimeOffset now, int? ttlDays)
    {
        if (ttlDays is not > 0)
            return false;
        // Guard the date arithmetic: a fat-fingered GRAPH_ITEM_TTL_DAYS large
        // enough to push <now + TTL> past DateTimeOffset.MaxValue would otherwise
        // throw ArgumentOutOfRangeException from AddDays — and because ApplyExpiry
        // runs inside the per-item transform try/catch, that turns EVERY item in
        // the crawl into a dead-lettered "transform failure". Clamp instead: a TTL
        // that far out is already "effectively never expires", so the item is
        // stamped with the maximum representable expiry rather than crashing.
        var maxDays = (DateTimeOffset.MaxValue - now).TotalDays;
        var expiry = ttlDays.Value < maxDays
            ? now.AddDays(ttlDays.Value).ToUniversalTime()
            : DateTimeOffset.MaxValue;
        item[ExpirationProperty] = expiry.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Metrics.IncItemsExpiryStamped();
        return true;
    }

    /// <summary>Convenience overload reading the configured TTL live.</summary>
    public static bool ApplyExpiry(JsonObject item, DateTimeOffset now) =>
        ApplyExpiry(item, now, TtlDays);
}
