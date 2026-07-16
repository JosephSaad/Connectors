// Entitlement/SeatService.cs
// --------------------------
// Seat-based entitlement sync.
//
// Seat sources (first match wins):
//   1. SEAT_GROUP_ID          — a single Entra security group; the group id is
//                               the only ACL principal (membership is evaluated
//                               by Microsoft Search at query time).
//   2. config/seats.json (or SEAT_LIST_PATH) — explicit list of Entra UPNs
//                               and/or object IDs; accepts either a plain JSON
//                               array or {"users":[...],"groups":[...]}.
//
// SyncSeats loads the current seat source, stores it in the identity store,
// compares the seat-set hash with the last known hash in the state store and
// reports whether a re-ACL pass is needed. See docs/ENTITLEMENT.md.

using System.Text.Json;
using AltrataConnector.Config;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Entitlement;

public sealed record SeatSyncResult(
    IReadOnlyList<SeatPrincipal> Seats,
    string SeatHash,
    string? PreviousHash,
    bool Changed)
{
    /// <summary>A re-ACL pass is needed when the seat set changed and items already exist.</summary>
    public bool RequiresReAcl => Changed && PreviousHash != null;
}

public sealed class SeatService
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.entitlement");

    private readonly AppConfig _config;
    private readonly IIdentityStore _identity;
    private readonly IStateStore _state;

    public SeatService(AppConfig config, IIdentityStore identity, IStateStore state)
    {
        _config = config;
        _identity = identity;
        _state = state;
    }

    /// <summary>Load seat principals from SEAT_GROUP_ID or the seat-list file.</summary>
    public IReadOnlyList<SeatPrincipal> LoadSeats()
    {
        if (!string.IsNullOrWhiteSpace(_config.SeatGroupId))
            return new[] { new SeatPrincipal(SeatPrincipalKind.Group, _config.SeatGroupId.Trim()) };

        var path = _config.SeatListPath;
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Seat list not found at '{path}'. Provide config/seats.json, SEAT_LIST_PATH, or SEAT_GROUP_ID.",
                path);
        return ParseSeatFile(File.ReadAllText(path));
    }

    /// <summary>Parse a seat file: plain array of UPNs/object IDs, or {"users":[...],"groups":[...]}.</summary>
    public static IReadOnlyList<SeatPrincipal> ParseSeatFile(string json)
    {
        var seats = new List<SeatPrincipal>();
        using var doc = JsonDocument.Parse(json);
        switch (doc.RootElement.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var element in doc.RootElement.EnumerateArray())
                    AddUser(seats, element.GetString());
                break;
            case JsonValueKind.Object:
                if (doc.RootElement.TryGetProperty("users", out var users) &&
                    users.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in users.EnumerateArray())
                        AddUser(seats, element.GetString());
                }
                if (doc.RootElement.TryGetProperty("groups", out var groups) &&
                    groups.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in groups.EnumerateArray())
                    {
                        var value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            seats.Add(new SeatPrincipal(SeatPrincipalKind.Group, value.Trim()));
                    }
                }
                break;
            default:
                throw new FormatException("Seat file must be a JSON array or an object with users/groups arrays");
        }
        return seats
            .DistinctBy(s => (s.Kind, Value: s.Value.ToLowerInvariant()))
            .ToList();
    }

    private static void AddUser(List<SeatPrincipal> seats, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var trimmed = value.Trim();
        seats.Add(Guid.TryParse(trimmed, out _)
            ? new SeatPrincipal(SeatPrincipalKind.UserObjectId, trimmed)
            : new SeatPrincipal(SeatPrincipalKind.UserUpn, trimmed));
    }

    /// <summary>
    /// Refresh the seat store from the seat source. Detects seat-set changes via
    /// the stored hash; the caller runs the re-ACL pass when RequiresReAcl.
    /// The new hash is only committed via <see cref="CommitSeatHash"/> AFTER a
    /// successful re-ACL pass, so an interrupted pass re-triggers next run.
    /// </summary>
    public SeatSyncResult SyncSeats()
    {
        var seats = LoadSeats();
        if (seats.Count == 0)
            throw new EntitlementViolationException(
                "Seat source yielded zero principals — refusing to sync an empty seat list.");

        _identity.ReplaceSeats(seats);
        var hash = SeatAclBuilder.ComputeSeatHash(seats);
        var previous = _state.GetValue(StateKeys.SeatListHash);
        var changed = !string.Equals(previous, hash, StringComparison.Ordinal);
        if (changed)
            Logger.Info($"Seat list changed: {seats.Count} seat principal(s), hash {hash[..12]}... (was {(previous == null ? "unset" : previous[..12] + "...")})");

        Metrics.Set("altrata_seat_count", seats.Count);
        return new SeatSyncResult(seats, hash, previous, changed);
    }

    /// <summary>Persist the seat hash after items are consistent with it.</summary>
    public void CommitSeatHash(string hash) => _state.SetValue(StateKeys.SeatListHash, hash);
}
