"""After a wave: did the shelf count move by exactly the merges the wave declared? (PLAN §8, the lead's check.)

    python merge_count_check.py <pre-wave backup dir name> <declared sid> [<declared sid> ...]

Compares the file-holding comic shelves of the pre-wave backup (data/books/v2/<backup>/books.db — wave_land prints
its BACKUP DIR) with the live db. Every shelf that disappeared is printed with where SeriesMerge sent it; one whose id
is not among the declared sids (either side of a merge-with) is flagged. Lessons it caught: a merge only takes effect
through a SHARED CV (wave 21's S98212 was cv=-), a target REFUSED in the same wave has its links cleared (wave 31),
and a reader's S whose cv is stored on a refused/empty shelf merges undeclared (waves 17 and 19). Read-only.
"""
import sqlite3
import sys

ROOT = r"F:\Work\MovieTheater\data\books\v2"
Q = """SELECT s.Id, s.Name FROM Series s WHERE EXISTS (SELECT 1 FROM Item i WHERE i.SeriesId = s.Id AND i.Kind = 0
       AND coalesce(i.IsExcluded,0) = 0)"""

backup, declared = sys.argv[1], {int(x) for x in sys.argv[2:]}
pre = sqlite3.connect(fr"file:{ROOT}\{backup}\books.db?mode=ro", uri=True)
cur = sqlite3.connect(fr"file:{ROOT}\books.db?mode=ro", uri=True)
a, b = dict(pre.execute(Q)), dict(cur.execute(Q))
gone, new = sorted(set(a) - set(b)), sorted(set(b) - set(a))
print(f"shelves {len(a):,} -> {len(b):,}   gone {len(gone)}   new {len(new)}")
for s in gone:
    m = cur.execute("SELECT NewSeriesId FROM SeriesMerge WHERE OldSeriesId = ? ORDER BY rowid DESC LIMIT 1", (s,)).fetchone()
    tgt = m[0] if m else None
    ok = s in declared or tgt in declared
    print(f"  gone S{s} {a[s]!r} -> merged into {('S' + str(tgt)) if tgt else '?'}" + ("" if ok else "   <-- NOT DECLARED"))
for s in new:
    print(f"  new  S{s} {b[s]!r}")
