# Usage controls — query and volume metering with enforceable ceilings

Altrata is a **licensed, per-lookup-billable** data source. The Feature Catalog
requires "usage controls with query and volume metering" for it.

Before WP-AL-4 the connector had two of the three pieces and neither could
refuse anything:

| Mechanism | What it does | Can it decline? |
|---|---|---|
| `IStateStore.IncrementBillableLookups` → `altrata_api_billable_lookups_total` | Counts every billable lookup, durably | **No** — a post-hoc tally |
| `ALTRATA_API_CALLS_PER_MINUTE` (`RateLimiter`) | Smooths the call rate against the vendor throttle | **No** — it only makes the caller *wait* |
| `ALTRATA_MAX_LOOKUPS_PER_DAY` / `_PER_WINDOW` | Ceiling on total volume | **Yes** — refuses, fail-closed |

A runaway or abusive workload was therefore billable without bound: the limiter
would patiently pace it, and the counter would faithfully record the size of the
invoice. The ceiling is the part that says no.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `ALTRATA_MAX_LOOKUPS_PER_DAY` | unset (`0`) | Max billable lookups per **calendar UTC day**. Tumbling: resets at 00:00 UTC. |
| `ALTRATA_MAX_LOOKUPS_PER_WINDOW` | unset (`0`) | Max billable lookups in the trailing **rolling window**. |
| `ALTRATA_USAGE_WINDOW_HOURS` | `24` (range 1–168) | Length of that rolling window. Inert unless `_PER_WINDOW` is set. |

Both ceilings may be set; a lookup must clear **both**. Negative values and an
out-of-range window are rejected by `AppConfig.Load`, so a typo fails
`validate-config` / startup rather than mid-crawl.

**Both unset = no ceiling = the pre-WP-AL-4 behaviour, byte-identical.** No
ledger key is written and no state is touched — verified by test
(`UnsetCeilingIsByteIdenticalToTheOldBehaviour`).

Why two ceilings? The calendar-day cap is what a contractual "N lookups per day"
allowance actually reads like. The rolling cap is what stops a burst that parks
itself either side of midnight from spending two days' allowance in ten minutes.

## Where the check sits — the order is load-bearing

Inside `AltrataApiClient.LookupPersonAsync`:

```
1. purpose veto        (PURPOSE_ALLOWLIST)  → deny: audit + metric + PurposeDeniedException
2. USAGE CEILING       (this)               → deny: audit + metric + UsageBudgetExceededException
3. rate limiter        (waits, never refuses)
4. OAuth token + HTTP lookup
5. billable counter + audit "allow"
```

**After the purpose veto** — a disallowed purpose must never even *consume*
budget. If it did, anyone who can invoke the connector could exhaust the day's
ceiling with calls that were never going to be permitted: a denial-of-service on
the legitimate workload, mounted entirely out of refused requests. Pinned by
`PurposeVetoPrecedesTheBudgetCheck`.

**Before everything else** — a refusal must cost nothing: no token fetch, no
request enqueued, no bill. Pinned by
`CeilingRefusesFailClosedWithZeroBillableAndZeroHttp`, which asserts the scripted
HTTP handler received no request at all.

A refusal is modelled exactly on the existing purpose deny: a PII-safe audit
entry (`Decision="deny"`, `Billable=false`), the dedicated metric
`altrata_usage_denied_total`, and a typed `UsageBudgetExceededException` naming
the knob the operator has to change.

## Reserve / release

The ceiling is charged by **reserving before** the call and **releasing** if the
call never became billable (breaker open, 5xx, timeout, graceful stop).

Reserving up front is what makes the check a real ceiling. A "read the count,
then increment after success" design would let *N* concurrent callers all observe
`used < limit` and all proceed, overshooting by up to *N*−1 billable calls. The
reservation is a single atomic read-modify-write
(`IStateStore.MutateValue`) covering both windows at once, so there is never a
moment where the daily counter has been charged but the rolling one has refused.

A process that dies between reserve and release leaves the reservation consumed.
That is deliberate: it errs toward the ceiling, never past it, and self-heals at
the next window rollover.

## Scope of the ceiling — read this before setting a number

The counters live in the state store's key/value facility, so a ceiling is
scoped to **one state store**, i.e. per `(backend, connector id)`.

**Connection sharding does *not* multiply it today.** The only caller of the
billable lookup is `ingest-item`, which runs on the base runtime
(`Runtime.Create` → `CONNECTOR_ID`); it is **not** shard-aware, so shard state
stores are never used for enrichment. If a shard-aware caller of
`LookupPersonAsync` is ever added, each shard would carry its own counter and the
real fleet ceiling would silently become *N* × the configured value. Whoever adds
that caller must revisit this — the note is repeated at the top of
`Altrata/UsageBudget.cs`.

**Host count *does* multiply it on the file backend.** This is the default and
the trap. Each host keeps its own `data/{CONNECTOR_ID}_state.json`, so *M* hosts
running the same `CONNECTOR_ID` enforce *M* × the configured ceiling in
aggregate. Either divide the number by the host count, or:

**Use `USE_SQL_SERVER=true` for a genuinely fleet-wide ceiling.** `dbo.altrata_kv`
is shared, and `SqlStateStore.MutateValue` reserves inside a transaction under
`UPDLOCK, HOLDLOCK`, so concurrent nodes serialise against one counter. `HOLDLOCK`
range-locks the not-yet-existing key too, so the first-ever write cannot race.

## Window mechanics

The daily counter is a plain integer keyed to the UTC calendar day; a different
day resets it.

The rolling window is approximated with **60 fixed buckets** of `window / 60`,
summed over the trailing window. The leading partial bucket is counted in full,
so the rolling check is marginally **conservative** — it can refuse very slightly
early, never late. Changing `ALTRATA_USAGE_WINDOW_HOURS` resets the rolling
counter, because the stored buckets measure a different span.

Both live in a single JSON document under the state key `usage_budget`, keyed
**by time only, never by subject** — no altrata id, no name, nothing personal
reaches the ledger (`UsageStateIsPiiSafe`).

An unreadable ledger starts a fresh window rather than failing open into
unbounded spend or wedging the connector (`CorruptLedgerFailsIntoAFreshEnforceableWindowNotIntoNoCeiling`).

## Observability

* `altrata_usage_denied_total` — refusals (distinct from `altrata_purpose_denied_total`).
* `altrata_api_billable_lookups_total` — unchanged lifetime billable counter.
* Every refusal is a `WARNING` naming the reason (`daily-ceiling` /
  `rolling-Nh-ceiling`) and the used/limit figures — never a subject value.
* `UsageMeter.Peek` reads consumption without charging, for reporting.

## Operator runbook

**"Lookups are being refused."** Check the log line: it names which ceiling and
the used/limit. Then either raise the knob, wait for the rollover (00:00 UTC for
the daily cap; the window length for the rolling one), or — if this is a runaway
rather than legitimate demand — find the caller. The audit log has the actor and
purpose on every denied entry.

**"The ceiling seems to be higher than I set."** You are almost certainly on the
file backend with more than one host. See the scope section above.
