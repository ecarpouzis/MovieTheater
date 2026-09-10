"""Put Series.Franchise back on the evidence, after the batch-stamp bug polluted it.

WHAT WENT WRONG, end to end. `books-series-split` used to stamp one franchise on every run it created in
a batch. Those wrong values then LOOKED like evidence to build_titles.py, whose repair spreads a franchise
across a title when every run of it that has one agrees — so `Ka-Zar`, a Marvel title whose runs all had
NULL, acquired 'Batman' from two split-created siblings and passed it to the three originals.

THE RESTORE, from a snapshot that predates every split (both 2026-09-08 backups hold 19,475 Series and no
id above 94600, so nothing in them was written by a split):

  pre-existing run   Franchise is restored to its backup value, exactly. That reverts the spread.
  split-created run  Franchise comes from the shelf the run was actually split OUT of — the FIRST
                     SeriesIdAtSplit recorded for its items in series-split-undo.csv — read from the same
                     backup. When that shelf had none, the run gets none. Inheriting nothing is the
                     correct answer; inventing one is what caused this.

Dry-run by default. `python restore_franchise.py [--apply]`
"""
import collections
import csv
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SNAP = r"F:\Work\MovieTheater\data\books\v2\backup-20260908-005336\books.db"
UNDO = [r"F:\Work\MovieTheater\docs\books\containment\reports\series-split-undo.csv",
        r"F:\Work\MovieTheater\series-split-undo.csv",
        r"F:\Work\MovieTheater\src\MovieTheater.BooksHost\series-split-undo.csv"]
APPLY = "--apply" in sys.argv

con = sqlite3.connect(HOT)
snap = sqlite3.connect(f"file:{SNAP}?mode=ro", uri=True)
was = {r[0]: (r[1] or "") for r in snap.execute("SELECT Id, Franchise FROM Series")}
now = {r[0]: (r[1] or "") for r in con.execute("SELECT Id, Franchise FROM Series")}

first_parent = {}
for path in UNDO:
    if not os.path.exists(path):
        continue
    with open(path, encoding="utf-8") as fh:
        for row in csv.DictReader(fh):
            try:
                first_parent.setdefault(int(row["ItemId"]), int(row["SeriesIdAtSplit"]))
            except (KeyError, ValueError):
                continue
item_series = {r[0]: r[1] for r in con.execute("SELECT Id, SeriesId FROM Item WHERE SeriesId IS NOT NULL")}

votes = collections.defaultdict(collections.Counter)
for item, parent in first_parent.items():
    sid = item_series.get(item)
    if sid is not None:
        votes[sid][was.get(parent, "")] += 1

reverted, inherited, cleared = [], [], []
for sid, cur in now.items():
    if sid in was:
        if cur != was[sid]:
            reverted.append((sid, cur, was[sid]))
        continue
    want = votes[sid].most_common(1)[0][0] if votes.get(sid) else ""
    if cur != want:
        (inherited if want else cleared).append((sid, cur, want))

print(f"pre-existing runs whose franchise was CHANGED since the snapshot : {len(reverted)}  (reverting)")
for sid, cur, w in reverted[:8]:
    print(f"   S{sid:<7} {cur!r:<22} -> {(repr(w) if w else 'NULL')}")
print(f"split-created runs taking their own parent's franchise           : {len(inherited)}")
for sid, cur, w in inherited[:8]:
    print(f"   S{sid:<7} {cur!r:<22} -> {w!r}")
print(f"split-created runs whose parent had none (cleared)               : {len(cleared)}")
for sid, cur, w in cleared[:8]:
    print(f"   S{sid:<7} {cur!r:<22} -> NULL")

if not APPLY:
    print("\n(dry run - re-run with --apply)")
    raise SystemExit
con.executemany("UPDATE Series SET Franchise = ? WHERE Id = ?",
                [(w or None, s) for s, _, w in reverted + inherited + cleared])
con.commit()
print(f"\napplied: {len(reverted) + len(inherited) + len(cleared)} rows put back on the evidence")
