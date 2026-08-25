"""R0 step 2: freeze the v1 MyBooks database.

Opens the LIVE mybooks.db read-only and `VACUUM INTO`s a consistent snapshot (the service stays
up; WAL readers see a stable snapshot). Produces:
  data/books/v1/mybooks-v1-frozen-<date>.db   (never opened by the app; the migration input)
  data/books/v1/mybooks-v1-census.db          (working copy the query census may mutate)
  data/books/v1/schema-v1.sql                 (sqlite_master DDL dump)
  data/books/v1/freeze.json                   (integrity, page_count, sha256, sizes)
Idempotent: refuses to overwrite an existing frozen copy unless --force.
"""
from __future__ import annotations
import argparse, datetime as dt, hashlib, shutil, sys
from common import LIVE_DB, V1, ro, dump_json, census_db


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--force", action="store_true")
    a = ap.parse_args()
    V1.mkdir(parents=True, exist_ok=True)
    today = dt.date.today().strftime("%Y%m%d")
    frozen = V1 / f"mybooks-v1-frozen-{today}.db"
    if frozen.exists() and not a.force:
        print(f"frozen copy exists, not overwriting: {frozen}")
    else:
        if frozen.exists():
            frozen.unlink()
        print(f"VACUUM INTO {frozen} from {LIVE_DB} (read-only source connection)")
        src = ro(LIVE_DB)
        src.execute("VACUUM INTO ?", (frozen.as_posix(),))
        src.close()
        print("vacuum done")
    con = ro(frozen)
    integrity = con.execute("PRAGMA integrity_check").fetchone()[0]
    page_size = con.execute("PRAGMA page_size").fetchone()[0]
    page_count = con.execute("PRAGMA page_count").fetchone()[0]
    ntables = con.execute("SELECT count(*) FROM sqlite_master WHERE type='table'").fetchone()[0]
    nindexes = con.execute("SELECT count(*) FROM sqlite_master WHERE type='index'").fetchone()[0]
    ddl = [r[0] for r in con.execute("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type DESC, name")]
    con.close()
    (V1 / "schema-v1.sql").write_text(";\n\n".join(ddl) + ";\n", encoding="utf-8")
    cdb = census_db()
    if not cdb.exists() or a.force:
        shutil.copyfile(frozen, cdb)
        print(f"census working copy: {cdb}")
    meta = {
        "frozen": str(frozen), "source": str(LIVE_DB), "taken_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "integrity_check": integrity, "page_size": page_size, "page_count": page_count,
        "bytes": frozen.stat().st_size, "sha256": sha256(frozen), "tables": ntables, "indexes": nindexes,
        "census_copy": str(cdb),
    }
    dump_json(V1 / "freeze.json", meta)
    print({k: meta[k] for k in ("integrity_check", "page_size", "page_count", "bytes", "tables", "indexes")})
    if integrity != "ok":
        sys.exit("INTEGRITY CHECK FAILED")


if __name__ == "__main__":
    main()
