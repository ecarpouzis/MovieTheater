"""List the manga / newspaper-strip shelves that still have no decision file.

Read-only. Groups by the top folder of Item.Path so strip shelves and manga shelves can be told apart,
and reports for each shelf: collected editions, loose issues, whether a decision file already exists.
"""
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

have = {int(m.group(1)) for f in os.listdir(DEC) if (m := re.match(r"^S(\d+)\.txt$", f))}

pat = None
limit = 400
for a in sys.argv[1:]:
    if a.startswith("--like="):
        pat = a.split("=", 1)[1]
    elif a.startswith("--limit="):
        limit = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
sql = """
 SELECT i.SeriesId, coalesce(s.DisplayNameOverride, s.Name),
        sum(cd.IsCollection), sum(1-cd.IsCollection),
        min(i.Path)
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 JOIN Series s ON s.Id = i.SeriesId
 WHERE coalesce(i.IsExcluded,0) = 0
 GROUP BY i.SeriesId HAVING sum(cd.IsCollection) > 0
"""
rows = con.execute(sql).fetchall()

out = []
for sid, name, cols, iss, path in rows:
    if sid in have:
        continue
    if pat and pat.lower() not in (path or "").lower() and pat.lower() not in (name or "").lower():
        continue
    # the folder chain minus the file name
    segs = (path or "").split("\\")[:-1]
    top = "\\".join(segs[3:5]) if len(segs) > 5 else "\\".join(segs)
    out.append((int(cols), int(iss), sid, name, top))

out.sort(key=lambda r: -r[0])
print(f"{len(out)} shelves with no decision file  ({sum(r[0] for r in out)} collected editions)\n")
for cols, iss, sid, name, top in out[:limit]:
    print(f"  S{sid:<8} {cols:>4} col {iss:>5} iss   {name[:44]:<44} {top[:70]}")
