#!/usr/bin/env python3
"""Tenant capacity gate.

TENANT_GOVERNANCE.md allocates one Microsoft 365 tenant's Graph-connector quota
across five connectors that deploy independently. Its own opening sentence is
the problem this script exists to solve:

    "Each connector deploys and runs independently, so nothing in code
     coordinates them -- this document is that coordination."

A coordination document that nothing checks is a coordination document that
drifts. The TOGAF assessment recorded exactly that as concern 5: the capacity
plan was never converted into a CI-checked artifact. Grepping .github/ for
TENANT_GOVERNANCE, shard budgets or capacity returned nothing.

WHAT IS CHECKED, and why each one can actually fail:

  1. The plan's own arithmetic. The per-connector rows must sum to the stated
     totals. A row edited without updating the total is the commonest way a
     table like this goes quietly wrong.
  2. The Microsoft-published hard limits, read from the plan's own limits table
     rather than hardcoded here: total connections <= 30, total items <= 50M,
     and every connector's item budget <= (its shards x 5M per connection).
     That last one is the constraint an author is most likely to miss, because
     it couples two columns that sit side by side.
  3. Coverage. Every connector in the repository must appear in the allocation
     table. Adding a sixth connector without allocating it capacity is the
     precise failure the plan says to re-run itself for.
  4. The shipped env samples against the budget. Shards are configured only
     through GRAPH_CONNECTION_SHARDS, so a connector's .env.local.example is
     the one artifact in the repository that expresses an intended shard count.
     An operator copies that file. Shipping an example that provisions more
     connections than the plan allows is a loaded gun, whether or not the line
     is currently commented out.
  5. The plan's own 80% review trigger, as a warning. The plan says approaching
     40M of the 50M tenant quota triggers a scope review; it currently sits at
     39M, so this is close enough to be worth saying out loud on every run.

WHAT IS DELIBERATELY NOT CHECKED. Actual runtime shard counts and actual
indexed-item counts live in the tenant, not the repository. This gate cannot
see them and does not pretend to -- it checks that the PLAN is internally
sound, that it covers every connector, and that what the repository ships to
operators agrees with it. Live consumption against the 50M ceiling is a
/metrics and alerting concern, and the plan already says so.
"""

from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

PLAN = "TENANT_GOVERNANCE.md"
CONNECTOR_DIR_RX = re.compile(r"^[A-Z][A-Za-z0-9]*Connector$")


# --------------------------------------------------------------------------- #
# Parsing the plan
# --------------------------------------------------------------------------- #

def _num(text: str) -> int | None:
    """'12 / 30' -> 12 ; '16M' -> 16000000 ; '5,000,000' -> 5000000."""
    t = text.strip().replace("**", "").split("/")[0].strip()
    m = re.fullmatch(r"([\d,\.]+)\s*([MK]?)", t, re.I)
    if not m:
        return None
    value = float(m.group(1).replace(",", ""))
    mult = {"": 1, "K": 1_000, "M": 1_000_000}[m.group(2).upper()]
    return int(value * mult)


def parse_limits(text: str) -> dict[str, int]:
    """The Microsoft-published limits table. Read, not hardcoded."""
    limits: dict[str, int] = {}
    for row in re.finditer(r"^\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]*?)\s*\|\s*$", text, re.M):
        name, value = row.group(1).strip().lower(), row.group(2).strip()
        n = _num(value)
        if n is None:
            continue
        if name == "connections":
            limits["connections_per_tenant"] = n
        elif name == "items":
            limits["items_per_connection"] = n
        elif name == "indexed items":
            limits["items_per_tenant"] = n
    return limits


def parse_allocation(text: str) -> tuple[dict[str, dict], dict]:
    """The allocation table: per-connector shards and item budget, plus the stated Total row."""
    rows: dict[str, dict] = {}
    stated: dict = {}
    for row in re.finditer(r"^\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]*?)\s*\|\s*$", text, re.M):
        c1, c2, c3 = (row.group(i).strip() for i in (1, 2, 3))
        label = c1.replace("**", "").strip()
        if label.lower().startswith("total"):
            stated = {"connections": _num(c2), "items": _num(c3)}
            continue
        # "HadoopConnector (BDH)" -> HadoopConnector
        m = re.match(r"([A-Za-z0-9]+Connector)", label)
        if not m:
            continue
        shards, items = _num(c2), _num(c3)
        if shards is None or items is None:
            continue
        rows[m.group(1)] = {"shards": shards, "items": items}
    return rows, stated


# --------------------------------------------------------------------------- #
# The one code<->plan link: the shipped env sample
# --------------------------------------------------------------------------- #

def sample_shard_count(repo: Path, connector: str) -> tuple[int | None, bool, str]:
    """
    Shard count expressed in the connector's shipped .env.local.example.

    Returns (count, active, raw). `active` is False when the line is commented
    out -- still reported, because it is what an operator uncomments.
    """
    sample = repo / connector / "env" / ".env.local.example"
    if not sample.is_file():
        return None, False, "no env/.env.local.example"
    # Only an ASSIGNMENT counts. These samples also DESCRIBE the variable in
    # prose ("GRAPH_CONNECTION_SHARDS (optional, default off): ..."), and a
    # first-mention match reads that line, finds no value, and -- in the first
    # version of this script -- reported "ok". A check that cannot parse its
    # input must never pass; that is the whole complaint against the grep gate
    # this repository just replaced.
    assign = re.compile(r"^#?\s*GRAPH_CONNECTION_SHARDS\s*=")
    for line in sample.read_text(encoding="utf-8", errors="replace").splitlines():
        s = line.strip()
        if not assign.match(s):
            continue
        active = not s.startswith("#")
        value = s.split("=", 1)[1].strip() if "=" in s else ""
        value = value.strip("'\"")
        try:
            parsed = json.loads(value)
        except Exception:
            return None, active, f"unparseable value: {value[:60]}"
        if isinstance(parsed, dict):
            return len(parsed), active, ", ".join(parsed)
        return None, active, f"not a shard map: {value[:60]}"
    return None, False, "GRAPH_CONNECTION_SHARDS not mentioned"


# --------------------------------------------------------------------------- #

class Report:
    def __init__(self, annotate: bool):
        self.failed = False
        self.annotate = annotate

    def ok(self, msg): print(f"   ok      {msg}")

    def warn(self, title, msg):
        print(f"   WARN    {msg}")
        if self.annotate:
            print(f"::warning title={title}::{msg}")

    def error(self, title, msg):
        print(f"   FAIL    {msg}")
        if self.annotate:
            print(f"::error title={title}::{msg}")
        self.failed = True


def run(repo: Path, annotate: bool) -> int:
    rep = Report(annotate)
    plan_path = repo / PLAN
    if not plan_path.is_file():
        print(f"{PLAN} not found — the capacity plan is the source of truth for this gate.")
        return 1
    text = plan_path.read_text(encoding="utf-8")

    limits = parse_limits(text)
    alloc, stated = parse_allocation(text)

    print("== Capacity plan parsed ==")
    for k, v in sorted(limits.items()):
        print(f"   limit   {k} = {v:,}")
    for c, a in sorted(alloc.items()):
        print(f"   alloc   {c:22} {a['shards']} shard(s), {a['items']:,} items")
    if not alloc:
        rep.error("Tenant capacity", f"no connector rows parsed from {PLAN} — the allocation table shape changed.")
        return 1

    # 1. the plan's own arithmetic
    print("\n== The plan agrees with itself ==")
    sum_conn = sum(a["shards"] for a in alloc.values())
    sum_items = sum(a["items"] for a in alloc.values())
    for label, got, want in (("connections", sum_conn, stated.get("connections")),
                             ("item budget", sum_items, stated.get("items"))):
        if want is None:
            rep.error("Tenant capacity", f"the Total row does not state a {label}.")
        elif got != want:
            rep.error("Tenant capacity",
                      f"per-connector {label} sums to {got:,} but the Total row says {want:,}. "
                      f"A row was edited without updating the total.")
        else:
            rep.ok(f"{label} rows sum to the stated total ({got:,})")

    # 2. the hard limits, from the plan's own table
    print("\n== Within the Microsoft-published limits ==")
    cap_conn = limits.get("connections_per_tenant")
    cap_item_conn = limits.get("items_per_connection")
    cap_item_tenant = limits.get("items_per_tenant")
    if cap_conn and sum_conn > cap_conn:
        rep.error("Tenant capacity", f"{sum_conn} connections allocated, tenant cap is {cap_conn}.")
    elif cap_conn:
        rep.ok(f"{sum_conn}/{cap_conn} connections")
    if cap_item_tenant and sum_items > cap_item_tenant:
        rep.error("Tenant capacity", f"{sum_items:,} items allocated, tenant cap is {cap_item_tenant:,}.")
    elif cap_item_tenant:
        rep.ok(f"{sum_items:,}/{cap_item_tenant:,} items")
    if cap_item_conn:
        for c, a in sorted(alloc.items()):
            ceiling = a["shards"] * cap_item_conn
            if a["items"] > ceiling:
                rep.error("Tenant capacity",
                          f"{c} budgets {a['items']:,} items across {a['shards']} shard(s) = "
                          f"{a['items'] / a['shards']:,.0f} per connection, over the {cap_item_conn:,} cap. "
                          f"It needs at least {-(-a['items'] // cap_item_conn)} shards.")
            else:
                rep.ok(f"{c:22} {a['items'] / a['shards']:,.0f} items/connection")
    # the plan's own review trigger
    if cap_item_tenant and sum_items >= 0.8 * cap_item_tenant:
        rep.warn("Tenant capacity review",
                 f"allocated items {sum_items:,} is {sum_items / cap_item_tenant:.0%} of the "
                 f"{cap_item_tenant:,} tenant quota. The plan triggers a scope review at 80%.")

    # 3. coverage
    print("\n== Every connector is allocated ==")
    present = sorted(p.name for p in repo.iterdir()
                     if p.is_dir() and CONNECTOR_DIR_RX.match(p.name) and (p / "src").is_dir())
    for c in present:
        if c not in alloc:
            rep.error("Tenant capacity",
                      f"{c} exists in the repository but has no row in {PLAN}. "
                      f"The plan says to re-run it whenever a connector is added.")
        else:
            rep.ok(c)
    for c in sorted(alloc):
        if c not in present:
            rep.error("Tenant capacity",
                      f"{PLAN} allocates capacity to {c}, which is not in the repository. "
                      f"Stale allocation holds tenant quota nothing can use.")

    # 4. shipped samples vs budget
    print("\n== Shipped env samples fit the budget ==")
    for c in present:
        if c not in alloc:
            continue
        count, active, detail = sample_shard_count(repo, c)
        budget = alloc[c]["shards"]
        if count is None:
            if "not mentioned" in detail or "no env/" in detail:
                rep.ok(f"{c:22} sample declares no shard map ({detail})")
            else:
                # present but unreadable: report it, never pass it
                rep.error("Tenant capacity",
                          f"{c} has a GRAPH_CONNECTION_SHARDS line in its env sample that this gate "
                          f"could not read ({detail}). Either the sample is malformed or the gate needs "
                          f"teaching — both are worth a failed build rather than a silent pass.")
            continue
        state = "ACTIVE" if active else "commented"
        if count > budget:
            rep.error("Tenant capacity",
                      f"{c} ships an env sample with {count} shards ({state}) but is allocated "
                      f"{budget}. An operator who uncomments it provisions {count - budget} "
                      f"connection(s) the tenant plan does not account for. Shards: {detail}")
        else:
            rep.ok(f"{c:22} sample uses {count} of {budget} allocated shard(s) ({state})")

    print()
    if rep.failed:
        print("Tenant capacity FAILED — see the annotations above.")
        return 1
    print("Tenant capacity plan is internally consistent and matches what the repository ships.")
    return 0


def self_test() -> int:
    """Prove the gate can fail. A gate nobody has seen fail is a decoration."""
    import tempfile
    failures = []

    def check(name, cond, detail=""):
        print(f"   {'ok      ' if cond else 'FAIL    '}{name} {detail if not cond else ''}")
        if not cond:
            failures.append(name)

    GOOD = """# Plan
| Limit | Value | Scope |
|---|---|---|
| Connections | 30 | per tenant |
| Items | 5,000,000 | per connection |
| Indexed items | 50,000,000 | per tenant |

| Connector | Connections (shards) | Item budget | Rationale |
|---|---|---|---|
| AlphaConnector | 2 | 6M | x |
| BetaConnector | 1 | 3M | y |
| **Total** | **3 / 30** | **9M / 50M** | z |
"""
    limits = parse_limits(GOOD)
    alloc, stated = parse_allocation(GOOD)
    check("limits parsed", limits.get("connections_per_tenant") == 30
          and limits.get("items_per_connection") == 5_000_000
          and limits.get("items_per_tenant") == 50_000_000, str(limits))
    check("allocation parsed", alloc == {"AlphaConnector": {"shards": 2, "items": 6_000_000},
                                         "BetaConnector": {"shards": 1, "items": 3_000_000}}, str(alloc))
    check("Total row parsed", stated == {"connections": 3, "items": 9_000_000}, str(stated))
    check("'12 / 30' takes the left number", _num("12 / 30") == 12)
    check("'16M' expands", _num("16M") == 16_000_000)
    check("'5,000,000' parses", _num("5,000,000") == 5_000_000)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "AlphaConnector" / "src").mkdir(parents=True)
        (root / "BetaConnector" / "src").mkdir(parents=True)

        # a) the good plan passes
        (root / PLAN).write_text(GOOD)
        check("a sound plan passes", run(root, False) == 0)

        # b) a row edited without the total
        (root / PLAN).write_text(GOOD.replace("| AlphaConnector | 2 | 6M | x |",
                                              "| AlphaConnector | 3 | 6M | x |"))
        check("arithmetic drift fails", run(root, False) == 1)

        # c) items over the per-connection cap
        (root / PLAN).write_text(GOOD.replace("| BetaConnector | 1 | 3M | y |",
                                              "| BetaConnector | 1 | 9M | y |")
                                     .replace("**9M / 50M**", "**15M / 50M**"))
        check("per-connection cap breach fails", run(root, False) == 1)

        # d) an unallocated connector
        (root / "GammaConnector" / "src").mkdir(parents=True)
        (root / PLAN).write_text(GOOD)
        check("unallocated connector fails", run(root, False) == 1)
        import shutil; shutil.rmtree(root / "GammaConnector")

        # e) a sample that exceeds the budget -- even commented out
        (root / PLAN).write_text(GOOD)
        env = root / "BetaConnector" / "env"; env.mkdir(parents=True)
        (env / ".env.local.example").write_text(
            '# GRAPH_CONNECTION_SHARDS={"a":["X"],"b":["Y"]}\n')
        check("over-budget env sample fails", run(root, False) == 1)

        # f) ... and a within-budget one does not
        (env / ".env.local.example").write_text('# GRAPH_CONNECTION_SHARDS={"a":["X"]}\n')
        check("within-budget env sample passes", run(root, False) == 0)

    print()
    if failures:
        print(f"Self-test FAILED: {', '.join(failures)}")
        return 1
    print("Self-test passed — the gate fails on drift, breach, omission and over-budget samples.")
    return 0


def main() -> int:
    args = sys.argv[1:]
    if "--self-test" in args:
        return self_test()
    repo = Path(args[args.index("--repo") + 1]) if "--repo" in args else Path(".")
    return run(repo.resolve(), "--annotate" in args)


if __name__ == "__main__":
    sys.exit(main())
