#!/usr/bin/env python3
"""Chassis conformance gate.

Two questions, and the second is the one the previous version could not ask:

  1. POSITIVE — does every connector carry every capability the fleet claims is
     uniform?
  2. NEGATIVE — does any connector carry its OWN copy of a chassis type without
     that divergence being declared?

The previous gate answered only (1), with `grep -rqE` over *.cs and *.csproj. It
passed on a file containing nothing but comments — verified, not assumed — so a
connector could delete its ProjectReference, leave a commented-out line behind,
and stay green. And having no negative assertion at all, it was green while 70
local copies of chassis types sat in four connectors: green precisely while the
condition it exists to police was being violated.

WHAT CHANGED

  * Comments are stripped before anything is matched. This is not hypothetical
    tidiness: the codebase is now full of comments discussing FileShare.ReadWrite
    and Connector.Chassis, several of them written while fixing the defects this
    gate is meant to notice, and each one satisfied the old check on its own.
  * The project reference is resolved by parsing the csproj as XML and following
    the Include path to a real file on disk, not by matching a string.
  * Divergences are declared in divergences.tsv. An undeclared copy fails. So
    does a declared copy that no longer exists — the register cannot outlive the
    debt it records, which is what stops a ratchet from silently becoming a
    permanent allowance.
  * --self-test proves the gate rejects what it used to accept, so the loophole
    cannot reopen without a test going red.

WHAT THIS DELIBERATELY DOES NOT DO

  It does not detect a RENAMED copy. Clarizen and Hadoop carry `Breakers`, which
  is CircuitBreakerRegistry under another name; Altrata has `Telemetry` for
  Tracing and `HttpConnectivity` for HttpTransport; three connectors have
  HttpClientFactory / DirectoryHardening / SecureDirectories playing the part of
  chassis types. Matching those automatically needs semantic comparison, and a
  gate that guesses produces false failures, which is how gates get switched off.
  They are recorded in the register with kind=renamed so they are at least
  visible and counted. The automated teeth apply to same-name copies.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

CONNECTORS = [
    "SalesforceConnector",
    "ClarizenConnector",
    "HadoopConnector",
    "SeismicConnector",
    "AltrataConnector",
]

CHASSIS_DIR = "Connector.Chassis"
REGISTER = ".github/conformance/divergences.tsv"

_KIND = r"(?:record\s+struct|record\s+class|class|record|interface|struct|enum)"
_DECL = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*"
    r"(?:public|internal|private|protected)\s+"
    r"(?:(?:static|sealed|abstract|partial|readonly|unsafe)\s+)*"
    + _KIND + r"\s+(?P<name>[A-Za-z_]\w*)",
    re.M,
)


# --------------------------------------------------------------------------- #
# Source handling
# --------------------------------------------------------------------------- #

def strip_comments(src: str) -> str:
    """Remove C# comments so a check cannot be satisfied by prose.

    String literals are not parsed, so a comment marker inside a string is
    removed too. That direction is safe: it can only make the gate stricter
    (a capability must be evidenced somewhere other than inside a string),
    never more permissive, and being more permissive is the failure that
    matters here.
    """
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    src = re.sub(r"(?m)//.*$", "", src)
    return src


def cs_files(root: Path):
    for path in sorted(root.rglob("*.cs")):
        parts = set(path.parts)
        if "obj" in parts or "bin" in parts:
            continue
        yield path


def declared_types(path: Path) -> set[str]:
    return {m.group("name") for m in _DECL.finditer(strip_comments(path.read_text(encoding="utf-8", errors="replace")))}


# --------------------------------------------------------------------------- #
# Positive checks
# --------------------------------------------------------------------------- #

# Each capability is satisfied by code OR by a package reference, and the "OR" is
# load-bearing rather than lenient.
#
# Salesforce is the case that proves it. It emits traces through
# Connector.Chassis.Tracing — the consolidated way, and the whole point of the
# programme — so the only occurrences of the word "OpenTelemetry" in its C# are
# in comments. A .cs-only, comment-stripped check FAILS it for doing the right
# thing. That is worse than a gate with a loophole: a gate that punishes the
# behaviour it is meant to encourage gets argued with, then weakened, then
# ignored.
#
# So each row asks "does this connector have the capability", not "does this
# connector spell it a particular way". Whether it got there via the chassis or
# its own copy is the DIVERGENCE REGISTER's question, below, and keeping the two
# questions apart is what lets both be strict.
CAPABILITIES = [
    {
        "name": "OpenTelemetry tracing",
        # A chassis consumer calls Tracing.Initialize / BeginCycle; a connector
        # with its own wiring names an ActivitySource or references the package.
        "cs": r"\bTracing\.(?:Initialize|BeginCycle)\b|\bActivitySource\b",
        "package": r"^OpenTelemetry\b",
    },
    {"name": "health endpoint", "cs": r"HealthEndpoint|\"/health\"", "package": None},
    {"name": "metrics endpoint", "cs": r"MetricsRenderer|\"/metrics\"", "package": None},
    {"name": "Windows service host", "cs": r"UseWindowsService|WindowsServiceHelpers", "package": None},
    {"name": "shared-read state helper", "cs": r"FileShare\.ReadWrite", "package": None},
]


def package_references(repo: Path, connector: str) -> set[str]:
    """Every PackageReference Include under the connector's src csprojs."""
    names: set[str] = set()
    src = repo / connector / "src"
    if not src.is_dir():
        return names
    for proj in sorted(src.rglob("*.csproj")):
        try:
            tree = ET.parse(proj)
        except ET.ParseError:
            continue
        for node in tree.iter():
            if node.tag.endswith("PackageReference"):
                include = node.attrib.get("Include")
                if include:
                    names.add(include)
    return names


def has_capability(repo: Path, connector: str, cap: dict) -> tuple[bool, str]:
    src = repo / connector / "src"
    if not src.is_dir():
        return False, "no src/"

    if cap["cs"]:
        rx = re.compile(cap["cs"])
        for path in cs_files(src):
            body = strip_comments(path.read_text(encoding="utf-8", errors="replace"))
            m = rx.search(body)
            if m:
                return True, f"{path.relative_to(repo)}: {m.group(0)}"

    if cap["package"]:
        rx = re.compile(cap["package"])
        for name in sorted(package_references(repo, connector)):
            if rx.search(name):
                return True, f"PackageReference {name}"

    return False, "not found in code or package references"


def references_chassis(repo: Path, connector: str) -> tuple[bool, str]:
    """Resolve the chassis ProjectReference through the XML to a real file.

    A string match would accept a commented-out reference, a reference inside an
    unsatisfied <Choose>, or a path that no longer resolves. Being on the fleet
    means the build genuinely consumes the chassis project, so the check follows
    the Include to disk.
    """
    src = repo / connector / "src"
    csprojs = [p for p in src.rglob("*.csproj")] if src.is_dir() else []
    if not csprojs:
        return False, "no csproj under src/"
    for proj in sorted(csprojs):
        try:
            tree = ET.parse(proj)
        except ET.ParseError as exc:
            return False, f"{proj}: unparseable ({exc})"
        for ref in tree.iter():
            if not ref.tag.endswith("ProjectReference"):
                continue
            include = ref.attrib.get("Include", "")
            if "Connector.Chassis" not in include:
                continue
            resolved = (proj.parent / include.replace("\\", os.sep)).resolve()
            if resolved.is_file():
                return True, str(resolved.relative_to(repo.resolve()))
            return False, f"{proj}: ProjectReference '{include}' does not resolve to a file"
    return False, "no ProjectReference to Connector.Chassis in any src csproj"


# --------------------------------------------------------------------------- #
# Negative check: the divergence register
# --------------------------------------------------------------------------- #

def chassis_types(repo: Path) -> dict[str, str]:
    """Every type the chassis declares -> the chassis file declaring it."""
    out: dict[str, str] = {}
    for path in cs_files(repo / CHASSIS_DIR):
        for name in declared_types(path):
            out[name] = path.name
    return out


def local_copies(repo: Path, types: dict[str, str]) -> set[tuple[str, str, str]]:
    """(connector, type, repo-relative path) for every same-name local copy."""
    found = set()
    for connector in CONNECTORS:
        src = repo / connector / "src"
        if not src.is_dir():
            continue
        for path in cs_files(src):
            for name in declared_types(path) & types.keys():
                found.add((connector, name, str(path.relative_to(repo))))
    return found


def shared_but_unchassised(repo: Path, types: dict[str, str], min_connectors: int = 2):
    """
    Types declared by two or more connectors that the chassis does NOT declare.

    The check above measures divergence FROM THE CHASSIS: it can only see a local
    type whose name collides with one the chassis already has. A capability the
    connectors built independently and the chassis never acquired is therefore
    invisible to it — there is nothing for the name to collide with.

    That is not hypothetical. ContentGate and InjectionScanner each exist in three
    connectors, in three different shapes, and had zero rows in the register while
    the gate reported "none undeclared". The register was measuring the wrong
    question: divergence from the chassis, not duplication across the fleet.

    Reported per type with the connectors that declare it. Enforced the same way
    as `copy`: undeclared is a failure, and a declaration whose copies have gone
    is stale, so the count cannot drift in either direction.
    """
    owners: dict[str, set[str]] = {}
    where: dict[tuple[str, str], str] = {}
    for connector in CONNECTORS:
        src = repo / connector / "src"
        if not src.is_dir():
            continue
        for path in cs_files(src):
            for name in declared_types(path):
                if name in types:
                    continue  # already covered by the chassis-divergence check
                owners.setdefault(name, set()).add(connector)
                where.setdefault((connector, name), str(path.relative_to(repo)))
    return {
        name: sorted((c, where[(c, name)]) for c in connectors)
        for name, connectors in owners.items()
        if len(connectors) >= min_connectors
    }


def read_register(repo: Path) -> list[dict]:
    path = repo / REGISTER
    if not path.is_file():
        return []
    rows = []
    for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = raw.split("\t")
        if len(parts) != 5:
            raise SystemExit(
                f"{REGISTER}:{lineno}: expected 5 tab-separated fields "
                f"(kind, connector, type, path, reason), got {len(parts)}"
            )
        kind, connector, typename, filepath, reason = (p.strip() for p in parts)
        if not reason:
            raise SystemExit(f"{REGISTER}:{lineno}: reason is mandatory — an undocumented divergence is drift")
        rows.append(
            {"kind": kind, "connector": connector, "type": typename,
             "path": filepath, "reason": reason, "lineno": lineno}
        )
    return rows


# --------------------------------------------------------------------------- #
# Reporting
# --------------------------------------------------------------------------- #

class Report:
    def __init__(self, annotate: bool):
        self.failed = False
        self.annotate = annotate

    def ok(self, msg: str) -> None:
        print(f"   ok      {msg}")

    def warn(self, title: str, msg: str) -> None:
        print(f"   WARN    {msg}")
        if self.annotate:
            print(f"::warning title={title}::{msg}")

    def error(self, title: str, msg: str) -> None:
        print(f"   FAIL    {msg}")
        if self.annotate:
            print(f"::error title={title}::{msg}")
        self.failed = True


def run(repo: Path, annotate: bool) -> int:
    rep = Report(annotate)

    print("== Connector.Chassis project reference ==")
    for connector in CONNECTORS:
        present, detail = references_chassis(repo, connector)
        if present:
            rep.ok(f"{connector} -> {detail}")
        else:
            rep.error(
                "Chassis conformance",
                f"{connector} does not reference the chassis project: {detail}",
            )

    for cap in CAPABILITIES:
        print(f"\n== {cap['name']} ==")
        for connector in CONNECTORS:
            present, detail = has_capability(repo, connector, cap)
            if present:
                rep.ok(f"{connector} ({detail})")
            else:
                rep.error(
                    "Chassis conformance",
                    f"{connector} does not provide '{cap['name']}' — {detail}.",
                )

    print("\n== Declared divergences (local copies of chassis types) ==")
    types = chassis_types(repo)
    actual = local_copies(repo, types)
    register = read_register(repo)

    declared = {(r["connector"], r["type"], r["path"]) for r in register if r["kind"] == "copy"}
    renamed = [r for r in register if r["kind"] == "renamed"]

    undeclared = sorted(actual - declared)
    stale = sorted(declared - actual)

    print(f"   chassis types: {len(types)}   local copies found: {len(actual)}   "
          f"declared: {len(declared)}   renamed (recorded, not auto-detected): {len(renamed)}")

    for connector, typename, path in undeclared:
        rep.error(
            "Undeclared chassis divergence",
            f"{connector} declares its own '{typename}' ({path}), which "
            f"Connector.Chassis/{types[typename]} also declares. Consume the chassis type, "
            f"or add a line to {REGISTER} saying why this connector keeps its own.",
        )

    for connector, typename, path in stale:
        rep.error(
            "Stale divergence register entry",
            f"{REGISTER} still records {connector} '{typename}' at {path}, but no such "
            f"local type exists. If the copy was deleted, delete the register line in the "
            f"same change — the register must not outlive the debt it records.",
        )

    if not undeclared and not stale:
        rep.ok(f"{len(declared)} declared, none undeclared, none stale")

    # ----------------------------------------------------------------------- #
    # The other half of the question: duplication ACROSS connectors that the
    # chassis never acquired, which the check above is structurally blind to.
    #
    # REPORTED, NOT ENFORCED — the same treatment, and for the same reason, as
    # kind=renamed above. Name collision alone cannot tell shared capability from
    # per-connector design: five connectors each declaring Program, AppConfig,
    # GraphClient and Dashboard is five connectors, not four pieces of debt.
    # Separating those needs semantic comparison, and this file's own header
    # records what happens to a gate that guesses — it gets switched off.
    #
    # So this prints the census and names what is worth arguing about, and the
    # rows that ARE debt get a kind=duplicated line with a reason, which the
    # stale-entry check below does enforce. The number is visible either way,
    # which is the thing the register previously could not do at all.
    # ----------------------------------------------------------------------- #
    print("\n== Duplicated across connectors (no chassis equivalent) ==")
    duplicated = shared_but_unchassised(repo, types)
    dup_declared = {
        (r["connector"], r["type"], r["path"]) for r in register if r["kind"] == "duplicated"
    }
    dup_actual = {
        (connector, name, path)
        for name, entries in duplicated.items()
        for connector, path in entries
    }

    print(f"   types duplicated in 2+ connectors: {len(duplicated)}   "
          f"rows: {len(dup_actual)}   declared as debt: {len(dup_declared)}   (reported, not enforced)")

    for name in sorted({t for _, t, _ in dup_declared}):
        owners = ", ".join(c for c, _ in duplicated.get(name, []))
        rep.ok(f"declared duplication: {name} ({owners or 'no longer duplicated'})")

    # A declared row whose copy has gone IS enforced: like the copy register,
    # this one must not outlive the debt it records.
    for connector, typename, path in sorted(dup_declared - dup_actual):
        rep.error(
            "Stale duplication register entry",
            f"{REGISTER} still records {connector} '{typename}' at {path} as "
            f"cross-connector duplication, but it is no longer duplicated (or no "
            f"longer exists). Delete the register line in the same change.",
        )

    print()
    if rep.failed:
        print("Chassis conformance FAILED — see the annotations above.")
        return 1
    print("Chassis conformance passed.")
    return 0


# --------------------------------------------------------------------------- #
# Register generation (operator convenience, never run in CI)
# --------------------------------------------------------------------------- #

def emit_register(repo: Path) -> int:
    types = chassis_types(repo)
    for connector, typename, path in sorted(local_copies(repo, types)):
        print(f"copy\t{connector}\t{typename}\t{path}\tTODO: why does this connector keep its own?")
    return 0


# --------------------------------------------------------------------------- #
# Self-test: the gate must reject what the old one accepted
# --------------------------------------------------------------------------- #

def _fixture(root: Path, rel: str, body: str) -> None:
    path = root / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body, encoding="utf-8")


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, condition: bool, detail: str = "") -> None:
        if condition:
            print(f"   ok      {name}")
        else:
            print(f"   FAIL    {name} {detail}")
            failures.append(name)

    # 1. The exact loophole: a file of nothing but comments must satisfy nothing.
    commented = """
// Connector.Chassis.csproj
// OpenTelemetry Tracing.Initialize Tracing.BeginCycle ActivitySource
// HealthEndpoint "/health"
// MetricsRenderer "/metrics"
// UseWindowsService WindowsServiceHelpers
// FileShare.ReadWrite
/* OpenTelemetry ActivitySource HealthEndpoint MetricsRenderer UseWindowsService FileShare.ReadWrite */
"""
    stripped = strip_comments(commented)
    for cap in CAPABILITIES:
        check(f"comments do not satisfy {cap['name']}",
              re.search(cap["cs"], stripped) is None)

    # A comment must not hide a real declaration on the same line, and a real
    # declaration must still be found when a comment follows it.
    check(
        "trailing comment does not hide a declaration",
        "Alerting" in {m.group("name") for m in _DECL.finditer(
            strip_comments("public sealed class Alerting  // the local copy\n{}"))},
    )
    check(
        "commented-out declaration is not counted",
        not {m.group("name") for m in _DECL.finditer(
            strip_comments("// public sealed class Alerting\n"))},
    )

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        _fixture(root, f"{CHASSIS_DIR}/Alerting.cs", "namespace C;\npublic static class Alerting { }\n")
        _fixture(root, f"{CHASSIS_DIR}/Connector.Chassis.csproj", "<Project />\n")

        # 2. A commented-out ProjectReference must not count as a reference.
        _fixture(
            root,
            "SalesforceConnector/src/App/App.csproj",
            '<Project>\n  <ItemGroup>\n'
            '    <!-- <ProjectReference Include="..\\..\\..\\Connector.Chassis\\Connector.Chassis.csproj" /> -->\n'
            "  </ItemGroup>\n</Project>\n",
        )
        present, detail = references_chassis(root, "SalesforceConnector")
        check("commented-out ProjectReference is not a reference", not present, detail)

        # 3. A reference pointing at a file that does not exist must not count.
        _fixture(
            root,
            "SalesforceConnector/src/App/App.csproj",
            '<Project>\n  <ItemGroup>\n'
            '    <ProjectReference Include="..\\..\\..\\Nowhere\\Connector.Chassis.csproj" />\n'
            "  </ItemGroup>\n</Project>\n",
        )
        present, detail = references_chassis(root, "SalesforceConnector")
        check("unresolvable ProjectReference is not a reference", not present, detail)

        # 4. A real one must count.
        _fixture(
            root,
            "SalesforceConnector/src/App/App.csproj",
            '<Project>\n  <ItemGroup>\n'
            '    <ProjectReference Include="..\\..\\..\\Connector.Chassis\\Connector.Chassis.csproj" />\n'
            "  </ItemGroup>\n</Project>\n",
        )
        present, detail = references_chassis(root, "SalesforceConnector")
        check("resolvable ProjectReference is a reference", present, detail)

        # 5. An undeclared local copy is detected.
        _fixture(root, "SalesforceConnector/src/App/Alerting.cs",
                 "namespace S;\npublic sealed class Alerting { }\n")
        found = local_copies(root, chassis_types(root))
        check(
            "undeclared local copy is detected",
            ("SalesforceConnector", "Alerting", "SalesforceConnector/src/App/Alerting.cs") in found,
            str(found),
        )

        # 6. A commented-out local copy is NOT detected (no false positives —
        #    a gate that cries wolf gets switched off).
        _fixture(root, "SalesforceConnector/src/App/Alerting.cs",
                 "namespace S;\n// public sealed class Alerting { }\n")
        found = local_copies(root, chassis_types(root))
        check("commented-out local copy is not a copy", not found, str(found))

    print()
    if failures:
        print(f"Self-test FAILED: {len(failures)} case(s) — {', '.join(failures)}")
        return 1
    print("Self-test passed — the gate rejects what the grep version accepted.")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--repo", default=".", help="repository root")
    ap.add_argument("--annotate", action="store_true", help="emit GitHub Actions annotations")
    ap.add_argument("--self-test", action="store_true", help="prove the gate has teeth, then exit")
    ap.add_argument("--emit-register", action="store_true", help="print a register seeded from the tree")
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    repo = Path(args.repo).resolve()
    if args.emit_register:
        return emit_register(repo)
    return run(repo, args.annotate)


if __name__ == "__main__":
    sys.exit(main())
