"""Manga shelves that still need a decision file, ordered by how much is at stake.

Read-only. A shelf counts as done when ../decisions/S<id>.txt exists; everything else is work.
Also reports, per shelf, how many of its collected editions already carry a provider-asserted
container node, because that is where a wrong range costs files.
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

limit = 60
show_done = "--done" in sys.argv
for a in sys.argv[1:]:
    if a.startswith("--limit="):
        limit = int(a.split("=", 1)[1])

have = {int(f[1:-4]) for f in os.listdir(DEC) if re.match(r"^S\d+\.txt$", f)}
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
 SELECT i.SeriesId, coalesce(s.DisplayNameOverride, s.Name),
        sum(cd.IsCollection), sum(1-cd.IsCollection),
        sum(CASE WHEN cd.IsCollection=1 AND n.TrackRole=1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END)
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 JOIN Series s ON s.Id = i.SeriesId
 LEFT JOIN CollectionNode n ON n.ItemId = i.Id
 WHERE coalesce(i.IsExcluded,0)=0 AND i.Path LIKE '%\\Manga\\%'
 GROUP BY i.SeriesId HAVING sum(cd.IsCollection) > 0
 ORDER BY sum(cd.IsCollection) DESC""").fetchall()

todo = [r for r in rows if (r[0] in have) == show_done]
print(f"{len(todo)} manga shelves {'done' if show_done else 'to do'} "
      f"({sum(r[2] for r in todo)} collected editions, {sum(r[4] for r in todo)} provider-asserted)\n")
for sid, name, cols, iss, prov in todo[:limit]:
    print(f"  S{sid:<8} {int(cols):>4} col {int(iss):>5} iss {int(prov):>4} asserted   {name[:52]}")
