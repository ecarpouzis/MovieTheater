"""R0 step 4: per-column census of the frozen v1 database (resumable, one JSON per table).

For every column: rows, non-null count, null %, distinct count, constant flag, min/max/sum length
(bytes), min/max value (truncated), 3 sample values. Skips tables whose JSON already exists
(delete the JSON or pass --table X to redo one). Prints {table, done, remaining} per table.
"""
from __future__ import annotations
import argparse, time
from common import frozen_db, ro, tables, is_fts_shadow, CENSUS, dump_json

OUT = CENSUS / "columns"


def trunc(v):
    if v is None:
        return None
    s = str(v)
    return s[:60] + "..." if len(s) > 60 else s


def census_table(con, t):
    cols = [(r[1], r[2], r[5]) for r in con.execute(f'PRAGMA table_info("{t}")')]
    n = con.execute(f'SELECT count(*) FROM "{t}"').fetchone()[0]
    res = {"table": t, "rows": n, "columns": []}
    for name, decl, pk in cols:
        q = (f'SELECT count("{name}"), count(DISTINCT "{name}"), min(length("{name}")), max(length("{name}")), '
             f'sum(length("{name}")), min("{name}"), max("{name}") FROM "{t}"')
        nn, nd, lmin, lmax, lsum, vmin, vmax = con.execute(q).fetchone()
        samples = [r[0] for r in con.execute(f'SELECT "{name}" FROM "{t}" WHERE "{name}" IS NOT NULL LIMIT 3')]
        res["columns"].append({
            "name": name, "decl": decl, "pk": bool(pk), "non_null": nn,
            "null_pct": round(100.0 * (n - nn) / n, 2) if n else 0.0,
            "distinct": nd, "constant": (n >= 100 and nd <= 1), "always_null": (n > 0 and nn == 0),
            "len_min": lmin, "len_max": lmax, "bytes": lsum or 0, "min": trunc(vmin), "max": trunc(vmax),
            "samples": [trunc(s) for s in samples],
        })
    res["bytes_total"] = sum(c["bytes"] for c in res["columns"])
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--table")
    a = ap.parse_args()
    con = ro(frozen_db())
    con.execute("PRAGMA mmap_size=1073741824")
    todo = [a.table] if a.table else [t for t in tables(con) if not is_fts_shadow(t) and t != "ComicFts"]
    pending = [t for t in todo if a.table or not (OUT / f"{t}.json").exists()]
    print(f"{len(todo)} tables, {len(pending)} pending", flush=True)
    for i, t in enumerate(pending, 1):
        t0 = time.time()
        res = census_table(con, t)
        dump_json(OUT / f"{t}.json", res)
        print({"table": t, "rows": res["rows"], "bytes": res["bytes_total"], "secs": round(time.time() - t0, 1),
               "done": i, "remaining": len(pending) - i}, flush=True)
    fts = con.execute("SELECT count(*) FROM ComicFts").fetchone()[0]
    dump_json(OUT / "ComicFts.json", {"table": "ComicFts", "rows": fts, "virtual": True, "columns": [{"name": "body"}]})
    print("column census complete")


if __name__ == "__main__":
    main()
