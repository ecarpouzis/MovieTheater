"""R0 step 6d: query census - parse the captured EF Core SQL log, dedupe, EXPLAIN QUERY PLAN each
statement on the census copy, flag full scans of big tables / automatic indexes / temp b-trees, and
diff the indexes actually used against the ones defined.

Input : data/books/census/ef-sql.log (console output of the spare-port run, capture-sql.ps1)
Output: data/books/census/queries.json, data/books/census/queries.md
"""
from __future__ import annotations
import json, re, sqlite3
from collections import Counter, defaultdict
from common import CENSUS, census_db, ro, dump_json

LOG = CENSUS / "ef-sql.log"
EXEC_RE = re.compile(r"Executed DbCommand \((\d+)ms\)")
NEWLOG_RE = re.compile(r"^\s*(info|warn|fail|dbug|trce|crit):")
PARAM_RE = re.compile(r"@__\w+|@p\d+|@\w+")
BIG = 50_000


def parse_log():
    lines = LOG.read_text(encoding="utf-8", errors="replace").splitlines()
    stmts = []
    i = 0
    while i < len(lines):
        m = EXEC_RE.search(lines[i])
        if not m:
            i += 1
            continue
        ms = int(m.group(1))
        j = i + 1
        sql = []
        while j < len(lines) and not NEWLOG_RE.match(lines[j]) and not EXEC_RE.search(lines[j]):
            sql.append(lines[j].strip())
            j += 1
        text = " ".join(s for s in sql if s)
        if text:
            stmts.append((ms, text))
        i = j
    return stmts


def normalize(sql: str) -> str:
    s = PARAM_RE.sub("?", sql)
    s = re.sub(r"\s+", " ", s).strip()
    return s


def main():
    stmts = parse_log()
    agg = {}
    for ms, sql in stmts:
        n = normalize(sql)
        a = agg.setdefault(n, {"count": 0, "max_ms": 0, "total_ms": 0})
        a["count"] += 1
        a["max_ms"] = max(a["max_ms"], ms)
        a["total_ms"] += ms
    con = ro(census_db())
    rows = {r[0]: con.execute(f'SELECT count(*) FROM "{r[0]}"').fetchone()[0]
            for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    defined = {r[0]: r[1] for r in con.execute(
        "SELECT name, tbl_name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'")}
    used = Counter()
    results = []
    VALID = ("SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "PRAGMA", "WITH", "VACUUM", "ANALYZE", "REINDEX")
    fragments = []
    for n, a in sorted(agg.items(), key=lambda kv: -kv[1]["total_ms"]):
        kind = n.split(" ", 1)[0].upper()
        if kind not in VALID:
            # concurrent warm-up threads interleave multi-line console output; these are torn statements, not SQL
            fragments.append({"sql": n[:200], "count": a["count"]})
            continue
        try:
            plan = [r[3] for r in con.execute("EXPLAIN QUERY PLAN " + n)]
            err = None
        except sqlite3.Error as e:
            plan, err = [], str(e)
        flags = []
        for line in plan:
            m = re.match(r"SCAN (\w+)", line)
            if m and rows.get(m.group(1), 0) >= BIG and "USING" not in line:
                flags.append(f"SCAN {m.group(1)} ({rows[m.group(1)]:,} rows)")
            if "AUTOMATIC" in line:
                flags.append("AUTOMATIC INDEX (missing index)")
            if "TEMP B-TREE" in line:
                flags.append("TEMP B-TREE")
            for im in re.finditer(r"USING (?:COVERING )?INDEX (\w+)", line):
                used[im.group(1)] += a["count"]
        results.append({"sql": n, "kind": kind, "count": a["count"], "max_ms": a["max_ms"], "total_ms": a["total_ms"],
                        "plan": plan, "flags": sorted(set(flags)), "error": err})
    unused = sorted(k for k in defined if k not in used)
    out = {"statements_captured": len(stmts), "distinct": len(results), "results": results, "fragments": fragments,
           "indexes_defined": len(defined), "indexes_used": dict(used), "indexes_unused": unused,
           "big_tables": {t: c for t, c in rows.items() if c >= BIG}}
    dump_json(CENSUS / "queries.json", out)
    write_md(out, defined)
    print(f"captured {len(stmts)} statements, {len(results)} distinct, {len(fragments)} interleaved fragments set aside; flagged {sum(1 for r in results if r['flags'])}; "
          f"indexes used {len(used)}/{len(defined)}; unused: {len(unused)}")


def write_md(out, defined):
    L = ["# Books v1 query census (hot SQL captured from the live binary on a spare port)", ""]
    L.append(f"Captured {out['statements_captured']} statements, {out['distinct']} distinct after parameter normalization. "
             f"Plans from `EXPLAIN QUERY PLAN` on the census copy. Big-table threshold {BIG:,} rows. "
             f"{len(out['fragments'])} torn statements (interleaved console lines from the concurrent warm-up) were set aside.")
    flagged = [r for r in out["results"] if r["flags"]]
    L += ["", f"## Flagged statements ({len(flagged)})", ""]
    for r in flagged:
        L.append(f"- **{', '.join(r['flags'])}** — {r['kind']} ×{r['count']} max {r['max_ms']} ms\n  `{r['sql'][:400]}`")
        for p in r["plan"]:
            L.append(f"    - {p}")
    L += ["", f"## Index usage: {len(out['indexes_used'])} of {out['indexes_defined']} named indexes used", ""]
    L.append("Unused by the captured hot set (candidates to drop, or indexes that only serve admin/offline paths): " +
             ", ".join(f"`{i}` ({defined[i]})" for i in out["indexes_unused"]))
    L += ["", "## All statements by total time", "", "| kind | count | max ms | total ms | sql |", "|---|---:|---:|---:|---|"]
    for r in out["results"][:150]:
        L.append(f"| {r['kind']} | {r['count']} | {r['max_ms']} | {r['total_ms']} | `{r['sql'][:160].replace('|', '¦')}` |")
    (CENSUS / "queries.md").write_text("\n".join(L) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
