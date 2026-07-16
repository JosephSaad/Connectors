// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;

namespace SalesforceCopilotConnector.Salesforce;

public static class Utils
{
    // Test seam — Python tests patch the module-level ``delay`` function
    // (e.g. ``@patch("graph.schema.delay")``); tests substitute a no-op here.
    internal static Func<double, Task> Sleeper = seconds =>
        Task.Delay(TimeSpan.FromSeconds(seconds));

    /// <summary>Sleep for the given number of seconds.</summary>
    public static Task DelayAsync(double seconds) => Sleeper(seconds);

    /// <summary>Return the current UTC datetime.</summary>
    public static DateTime UtcNow() => DateTime.UtcNow;

    /// <summary>Return the Unix epoch (1970-01-01T00:00:00Z) as a UTC datetime.</summary>
    public static DateTime UnixEpoch() => DateTime.UnixEpoch;

    /// <summary>Ensure <paramref name="value"/> is a UTC-aware datetime, assuming UTC if naive.</summary>
    public static DateTime NormalizeDatetime(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return value.ToUniversalTime();
    }

    /// <summary>Format <paramref name="value"/> as an ISO-8601 string with a trailing ``Z`` suffix.</summary>
    public static string ToIsoZ(DateTime value)
    {
        var utc = NormalizeDatetime(value);
        // Python ``datetime.isoformat()`` omits the fractional part when it is zero
        // and otherwise emits microseconds (6 digits) — match that output exactly.
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss"
            : "yyyy-MM-dd'T'HH:mm:ss.ffffff";
        return utc.ToString(format, CultureInfo.InvariantCulture) + "Z";
    }

    /// <summary>Parse an ISO-8601 string (with optional ``Z`` suffix) into a UTC datetime.</summary>
    public static DateTime ParseDatetime(string value)
    {
        var normalized = value.Replace("Z", "+00:00");
        var parsed = DateTimeOffset.Parse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return parsed.UtcDateTime;
    }
}
