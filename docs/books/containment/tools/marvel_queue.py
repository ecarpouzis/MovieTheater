"""Marvel-only worklist: shelves whose files live under \\5 - Comics\\Marvel\\ (incl. the
#MARVEL CURRENT ONGOING TITLES tree), ranked by risk = a shelf holding BOTH collected editions
and loose issue files, then by provider-asserted containers.

Read-only. `python marvel_queue.py [--limit N]`
"""
import collections
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
DEC = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "decisions")
done = {int(re.search(r"S(\d+)", f).group(1)) for f in os.listdir(DEC) if re.match(r"^S\d+\.txt$", f)}

limit = 60
for a in sys.argv[1:]:
    if a.startswith("--limit"):
        limit = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
 SELECT s.Id, coalesce(s.DisplayNameOverride, s.Name) AS sname,
        sum(cd.IsCollection) AS cols, sum(1-cd.IsCollection) AS iss,
        sum(CASE WHEN sp.Source=3 AND sp.IssueStart IS NOT NULL THEN 1 ELSE 0 END) AS judged,
        sum(CASE WHEN n.TrackRole=1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END) AS prov,
        sum(CASE WHEN i.Path LIKE '%\\5 - Comics\\Marvel\\%' ESCAPE '~' THEN 1 ELSE 0 END) AS marvel
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 JOIN Series s ON s.Id = i.SeriesId
 LEFT JOIN CollectedEditionSpan sp ON sp.ItemId = i.Id AND sp.Source = 3
 LEFT JOIN CollectionNode n ON n.ItemId = i.Id
 WHERE coalesce(i.IsExcluded,0) = 0
 GROUP BY s.Id HAVING cols > 0 AND marvel > 0""").fetchall()

work = []
for sid, sname, cols, iss, judged, prov, marvel in rows:
    if sid in done or judged:
        continue
    work.append((sid, sname, int(cols), int(iss), int(prov), int(marvel)))

# risk: shelves with BOTH editions and loose issues first, weighted by asserted containers
work.sort(key=lambda w: -((1 if w[3] else 0) * 1000 + w[4] * 30 + min(w[3], 200) + w[2]))
print(f"{len(work)} Marvel shelves to do  ({sum(w[2] for w in work)} editions, "
      f"{sum(w[4] for w in work)} asserted)\n")
for sid, sname, c, i, p, m in work[:limit]:
    print(f"  S{sid:<7} {c:>3} col {i:>5} iss {p:>3} asserted   {sname[:60]}")
