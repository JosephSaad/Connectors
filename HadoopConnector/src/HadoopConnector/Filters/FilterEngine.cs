// Filters/FilterEngine.cs
// -----------------------
// Predicate evaluation for the filter layer, applied in strict order with
// per-stage accounting (see BdhFetcher):
//
//   1. Partition filters  — evaluated on partition KEYS during the directory
//      walk (zero file I/O for pruned dirs). A predicate on a key that is
//      ABSENT at the current level never prunes (the key may appear deeper);
//      pruning only happens on a present-key mismatch.
//   2. Record predicates  — evaluated per row while streaming (OR of AND
//      groups). Field lookup is case-insensitive; string comparison is
//      case-insensitive ordinal; numeric/date operators parse invariant.
//
// The engine is pure and clock-injectable (withinLastDays) for tests.

using System.Globalization;
using System.Text.Json.Nodes;
using HadoopConnector.Hdfs;

namespace HadoopConnector.Filters;

public sealed class FilterEngine
{
    private readonly Func<DateTime> _nowUtc;

    public FilterEngine(Func<DateTime>? nowUtc = null)
    {
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    // ── Partition-key evaluation (directory pruning) ─────────────────────────

    /// <summary>
    /// Evaluate the partition predicates against the partition keys accumulated
    /// so far. Returns false (PRUNE) only when a predicate's key is PRESENT and
    /// fails; absent keys never prune (they may appear deeper in the layout).
    /// </summary>
    public bool MatchesPartition(IReadOnlyList<FilterPredicate> predicates, IReadOnlyDictionary<string, string> keys)
    {
        foreach (var predicate in predicates)
        {
            var key = keys.Keys.FirstOrDefault(k =>
                string.Equals(k, predicate.Field, StringComparison.OrdinalIgnoreCase));
            if (key is null)
                continue;  // key not present at this level — cannot prune yet
            if (!EvaluateScalar(keys[key], predicate))
                return false;
        }
        return true;
    }

    // ── Record evaluation (streaming) ────────────────────────────────────────

    /// <summary>True when the row passes the object's record predicates
    /// (OR of AND groups; no groups → everything matches).</summary>
    public bool MatchesRecord(ObjectFilter? filter, BdhRecord record)
    {
        if (filter is null || filter.AnyOf.Count == 0)
            return true;
        foreach (var group in filter.AnyOf)
        {
            var all = true;
            foreach (var predicate in group.AllOf)
            {
                if (!Evaluate(record.Get(predicate.Field), predicate))
                {
                    all = false;
                    break;
                }
            }
            if (all)
                return true;
        }
        return false;
    }

    /// <summary>Evaluate one predicate against a JSON field value.</summary>
    internal bool Evaluate(JsonNode? node, FilterPredicate predicate)
    {
        var text = NodeToString(node);
        var isNull = string.IsNullOrEmpty(text);
        switch (predicate.Op)
        {
            case FilterOp.IsNull:
                return isNull;
            case FilterOp.IsNotNull:
                return !isNull;
        }
        if (isNull)
            return false;  // every value-comparing operator fails on a missing value
        return EvaluateScalar(text!, predicate);
    }

    /// <summary>Evaluate a value-comparing predicate against a non-null string value.</summary>
    internal bool EvaluateScalar(string value, FilterPredicate predicate)
    {
        switch (predicate.Op)
        {
            case FilterOp.Equals:
                return string.Equals(value, predicate.Value, StringComparison.OrdinalIgnoreCase);
            case FilterOp.NotEquals:
                return !string.Equals(value, predicate.Value, StringComparison.OrdinalIgnoreCase);
            case FilterOp.In:
                return predicate.Values.Any(v => string.Equals(value, v, StringComparison.OrdinalIgnoreCase));
            case FilterOp.NotIn:
                return !predicate.Values.Any(v => string.Equals(value, v, StringComparison.OrdinalIgnoreCase));
            case FilterOp.Prefix:
                return value.StartsWith(predicate.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            case FilterOp.Contains:
                return value.Contains(predicate.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            case FilterOp.Gte:
                return CompareNumeric(value, predicate.Value) is { } gte && gte >= 0;
            case FilterOp.Lte:
                return CompareNumeric(value, predicate.Value) is { } lte && lte <= 0;
            case FilterOp.Between:
                return CompareNumeric(value, predicate.Values[0]) is { } low && low >= 0
                    && CompareNumeric(value, predicate.Values[1]) is { } high && high <= 0;
            case FilterOp.WithinLastDays:
                if (!TryParseDate(value, out var withinDate)
                    || !int.TryParse(predicate.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                {
                    return false;
                }
                return withinDate >= _nowUtc().AddDays(-days);
            case FilterOp.After:
                return TryParseDate(value, out var afterValue)
                    && TryParseDate(predicate.Value!, out var afterBound)
                    && afterValue > afterBound;
            case FilterOp.Before:
                return TryParseDate(value, out var beforeValue)
                    && TryParseDate(predicate.Value!, out var beforeBound)
                    && beforeValue < beforeBound;
            case FilterOp.IsNull:
                return false;   // a present scalar is not null
            case FilterOp.IsNotNull:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Numeric comparison (value vs operand); null when either side is
    /// non-numeric — a non-numeric field NEVER matches a numeric operator.</summary>
    internal static int? CompareNumeric(string value, string? operand)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
            || !double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
        {
            return null;
        }
        return left.CompareTo(right);
    }

    /// <summary>Parse yyyy-MM-dd or ISO-8601 date/time (invariant, UTC-assumed).</summary>
    internal static bool TryParseDate(string value, out DateTime parsedUtc)
    {
        if (DateTime.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsedUtc))
        {
            return true;
        }
        if (DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            parsedUtc = dto.UtcDateTime;
            return true;
        }
        parsedUtc = default;
        return false;
    }

    /// <summary>Flatten a JSON node to its comparable string form (null → null).</summary>
    internal static string? NodeToString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"'),
        _ => node.ToJsonString(),
    };
}
