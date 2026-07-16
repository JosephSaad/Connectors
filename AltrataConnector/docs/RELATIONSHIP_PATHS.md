# RELATIONSHIP_PATHS — path materialization

Opt-in (`RELATIONSHIP_PATHS=true`, default off = behavior unchanged).
Precomputes bounded per-person relationship-path summaries from the
`RelationshipPath`, `BoardMembership` and `Organization` feed datasets and
materializes them onto `PersonProfile` externalItems as searchable / refinable
properties — so Copilot can answer "who has the most second-degree
connections to <org>" without the raw graph ever leaving the connector.

## What lands on a person item

| Property | Type | Meaning |
|---|---|---|
| `firstDegreeCount` | int64 | distinct directly-connected persons (direct edges) |
| `secondDegreeCount` | int64 | distinct persons reachable via exactly 2 hops (neighbours-of-neighbours, excluding self and first-degree) |
| `pathCount` | int64 | total RelationshipPath records touching the person |
| `topConnectedOrgs` | stringCollection | orgs of the person's direct neighbours, ranked by frequency then name, capped at `RELATIONSHIP_TOP_ORGS` (default 3) |
| `strongestPathSummary` | string | one sentence for the highest-strength path, e.g. `"2 paths via 1 intermediary to Acme"` |

**Bounded by design** — a person item never carries the raw adjacency:
just three counts, a capped org list and one sentence. Persons with no paths
get no path properties at all (the item stays clean).

## How the index is built

1. At the start of a crawl, the index is **rebuilt from scratch** from the
   current feed contents (a deterministic snapshot): a checksum-valid delivery's
   `RelationshipPath` rows become edges (`intermediary_count == 0` ⇒ a direct
   first-degree edge), `BoardMembership` rows become person→org memberships
   (org ids resolved to names via the `Organization` dataset), and delta
   **tombstones are skipped** so a rebuild drops them.
2. The edges + membership rows are persisted to the identity store
   (`altrata_id_path_edges` / `altrata_id_path_orgs` in SQL Server;
   `path_edges` / `path_person_orgs` in SQLite) — deterministic, reconcilable
   and inspectable.
3. Every `PersonProfile` tombstone in the crawl, and every person whose item is
   withdrawn, is **removed from the index** (`RemovePersonFromPathIndex`) — a
   withdrawn person drops from everyone's degree counts and top-orgs.
4. The in-memory view is loaded from the persisted index and joined onto person
   items at transform time.

Reconciliation and the delta-tombstone logic are unchanged: the path datasets
still ingest as their own externalItems and reconcile normally; the index is
derived state layered on top. `altrata_path_index_edges` on `/metrics` reports
the edge count after each rebuild.

> A **full crawl** refreshes summaries for every person. An **incremental
> crawl** rebuilds the index from all on-disk deliveries (so the index stays
> complete) but only re-transforms persons in the new deliveries — run a full
> crawl to refresh summaries on older person items after the graph shifts.

## Scoring / summary rules

* Degrees use only **direct** edges (intermediaries 0). Longer paths still
  count toward `pathCount` and can be the `strongestPathSummary`.
* `topConnectedOrgs`: neighbours' org memberships, `count desc, name asc`,
  capped — deterministic and bounded.
* `strongestPathSummary`: the max-strength path (ties → fewer intermediaries,
  then other-endpoint id); target label is that endpoint's first org, else its
  name, else its id.

## Seat invariant

Path properties are **metadata only** — they never touch the ACL. Person items
stay seat-only; `SeatAclBuilder.AssertNeverEveryone` is re-asserted after the
path join (defence in depth), and the batched ingest path asserts again before
any request. A `RELATIONSHIP_PATHS` item can never be visible to "everyone".

## Knobs

| Env var | Default | Meaning |
|---|---|---|
| `RELATIONSHIP_PATHS` | `false` | enable materialization |
| `RELATIONSHIP_TOP_ORGS` | `3` (1-10) | cap on the `topConnectedOrgs` list |
