"""Every pre-existing live comic item must still be on the shelf it was on before the rescan (read-only).
The rescan's decisions only re-key NEW items; a pre-existing item that changed shelf is a defect to explain.
usage: python shelf_stability.py --backup data/books/v2/backup-20261002-041138/books.db --first-new-id 245531
"""
import argparse, collections, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--backup", required=True)
ap.add_argument("--first-new-id", type=int, required=True)
a = ap.parse_args()
bak = sqlite3.connect(f"file:{a.backup}?mode=ro", uri=True)
live = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
before = dict(bak.execute("select Id, SeriesId from Item where RootId=1"))
moved = [(i, before.get(i), s) for i, s in live.execute(
    "select Id, SeriesId from Item where RootId=1 and IsExcluded=0 and Id < ?", (a.first_new_id,)) if before.get(i) != s]
print("pre-existing live items whose shelf changed:", len(moved))
pairs = collections.Counter((o, n) for _, o, n in moved)
for (o, n), k in pairs.most_common(40):
    on = bak.execute("select Name from Series where Id=?", (o,)).fetchone() if o else None
    nn = live.execute("select Name from Series where Id=?", (n,)).fetchone() if n else None
    print(f"  {k:4} S{o} {on} -> S{n} {nn}")
