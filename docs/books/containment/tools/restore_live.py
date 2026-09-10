"""Restore the LIVE books.db from a backup directory, through SQLite's online backup API — the reverse of
backup_live.py, and safe with the BooksHost service holding the file (the API copies page by page inside the
destination's own locking; readers see either the old or the new database, never a torn one).

    python restore_live.py <backup-dir> [--legs] [--apply]

Dry run by default: prints what would be restored, the row counts of Series / Item / SeriesKeyLink on both sides,
and refuses if the backup directory has no books.db. `--legs` also restores books-legs.db. `--apply` does it.
Written 2026-09-10 when a `books-resolve --series` half-committed (FOREIGN KEY constraint failed at commit after
the items had already been re-pointed) and the wave's own pre-apply backup was the only clean state.
"""
import os
import sqlite3
import sys
import time

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
LEGS = r"F:\Work\MovieTheater\data\books\v2\books-legs.db"

args = [a for a in sys.argv[1:] if not a.startswith("--")]
if not args:
    raise SystemExit(__doc__)
src_dir = args[0]
apply = "--apply" in sys.argv
want_legs = "--legs" in sys.argv


def counts(path):
    c = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    out = {}
    for t in ("Series", "Item", "SeriesKeyLink", "CollectionNode"):
        try:
            out[t] = c.execute(f"SELECT count(*) FROM {t}").fetchone()[0]
        except sqlite3.Error:
            out[t] = None
    c.close()
    return out


def restore(src, dst):
    s = sqlite3.connect(f"file:{src}?mode=ro", uri=True)
    d = sqlite3.connect(dst)
    t0 = time.time()
    s.backup(d, pages=4096)          # page-wise; the destination is locked per step, never torn
    d.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    d.close()
    s.close()
    return time.time() - t0


for name, dst, wanted in (("books.db", HOT, True), ("books-legs.db", LEGS, want_legs)):
    src = os.path.join(src_dir, name)
    if not wanted:
        continue
    if not os.path.exists(src):
        raise SystemExit(f"no {name} in {src_dir}")
    print(f"{name}: backup {os.path.getsize(src):,} bytes  ->  live {os.path.getsize(dst):,} bytes")
    print(f"   backup counts: {counts(src)}")
    print(f"   live counts:   {counts(dst)}")
    if apply:
        secs = restore(src, dst)
        print(f"   RESTORED in {secs:.1f}s; live counts now: {counts(dst)}")
if not apply:
    print("\n(dry run — nothing restored. Re-run with --apply.)")
