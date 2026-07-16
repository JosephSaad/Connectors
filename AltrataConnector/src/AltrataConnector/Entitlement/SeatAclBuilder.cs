// Entitlement/SeatAclBuilder.cs
// -----------------------------
// THE signature invariant of this connector: Altrata results may only ever be
// visible to principals covered by the Altrata license (the seat list).
//
//   * The ACL of every externalItem is built EXCLUSIVELY from seat principals.
//   * "everyone" / "everyoneExceptGuests" grants are structurally impossible —
//     BuildAcl throws EntitlementViolationException if they would ever appear,
//     and an empty seat list REFUSES ingestion instead of falling open.
//
// See docs/ENTITLEMENT.md.

using System.Security.Cryptography;
using System.Text;
using AltrataConnector.Graph;
using AltrataConnector.Identity;

namespace AltrataConnector.Entitlement;

public sealed class EntitlementViolationException : Exception
{
    public EntitlementViolationException(string message) : base(message) { }
}

public static class SeatAclBuilder
{
    /// <summary>ACL types that must never be emitted by this connector.</summary>
    internal static readonly string[] ForbiddenAclTypes = { "everyone", "everyoneExceptGuests" };

    /// <summary>
    /// Build the ACL for an externalItem from the licensed seats.
    /// Throws EntitlementViolationException when the seat list is empty —
    /// ingestion must fail closed, never fall open.
    /// </summary>
    public static IReadOnlyList<AclEntry> BuildAcl(IReadOnlyCollection<SeatPrincipal> seats)
    {
        if (seats.Count == 0)
            throw new EntitlementViolationException(
                "Seat list is empty — refusing to build an ACL. Altrata items are only " +
                "visible to licensed seats; ingestion cannot proceed without at least one seat.");

        var entries = new List<AclEntry>(seats.Count);
        foreach (var seat in seats.OrderBy(s => s.Kind).ThenBy(s => s.Value, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(seat.Kind switch
            {
                SeatPrincipalKind.Group => new AclEntry { Type = "group", Value = seat.Value },
                _ => new AclEntry { Type = "user", Value = seat.Value },
            });
        }

        AssertNeverEveryone(entries);
        return entries;
    }

    /// <summary>Defence in depth: reject any ACL containing an everyone-grant.</summary>
    public static void AssertNeverEveryone(IEnumerable<AclEntry> acl)
    {
        foreach (var entry in acl)
        {
            if (ForbiddenAclTypes.Contains(entry.Type, StringComparer.OrdinalIgnoreCase))
                throw new EntitlementViolationException(
                    $"ACL entry of type '{entry.Type}' is forbidden: Altrata items must " +
                    "only be visible to licensed seat principals, never to everyone.");
        }
    }

    /// <summary>
    /// Stable hash of a seat set (order-independent). Stored per ingested item
    /// and in the state store; a changed hash triggers the re-ACL pass.
    /// </summary>
    public static string ComputeSeatHash(IReadOnlyCollection<SeatPrincipal> seats)
    {
        var canonical = string.Join("\n",
            seats.Select(s => $"{s.Kind}:{s.Value.ToLowerInvariant()}")
                 .OrderBy(s => s, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
