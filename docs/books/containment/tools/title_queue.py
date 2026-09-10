"""The remaining work, ordered by TITLE rather than by shelf.

A title is a unit of work; a run is a fragment of one. Reading one Green Lantern run teaches where DC
prints its indicia and how much front matter its trades carry, and the other twelve runs then cost a page
each instead of a discovery each. Ordering by shelf throws that away and re-learns the same conventions
250 times.

Ranked by what is actually at stake: containers a provider is asserting on a shelf that also holds loose
issues (a wrong range there hides real files), then unjudged collected editions.

`python title_queue.py [--limit N] [--untitled]`   --untitled lists standalone shelves instead.
"""
import collections
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
limit, untitled = 20, "--untitled" in sys.argv
for a in sys.argv[1:]:
    if a.startswith("--limit"):
        limit = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
 SELECT s.Id, s.TitleId, coalesce(t.Name, coalesce(s.DisplayNameOverride, s.Name)) AS title,
        coalesce(s.DisplayNameOverride, s.Name) AS sname,
        sum(cd.IsCollection) AS cols, sum(1-cd.IsCollection) AS iss,
        sum(CASE WHEN sp.Source=3 AND sp.IssueStart IS NOT NULL THEN 1 ELSE 0 END) AS judged,
        sum(CASE WHEN n.TrackRole=1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END) AS prov
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 JOIN Series s ON s.Id = i.SeriesId
 LEFT JOIN SeriesTitle t ON t.Id = s.TitleId
 LEFT JOIN CollectedEditionSpan sp ON sp.ItemId = i.Id AND sp.Source = 3
 LEFT JOIN CollectionNode n ON n.ItemId = i.Id
 WHERE coalesce(i.IsExcluded,0) = 0
 GROUP BY s.Id HAVING cols > 0""").fetchall()

titles = collections.defaultdict(list)
for sid, tid, title, sname, cols, iss, judged, prov in rows:
    titles[(tid, title)].append((sid, sname, int(cols), int(iss), int(judged), int(prov)))

work = []
for (tid, title), runs in titles.items():
    unjudged = [r for r in runs if r[4] == 0]
    if not unjudged:
        continue
    if untitled != (tid is None):
        continue
    work.append((tid, title, runs, unjudged,
                 sum(r[5] for r in unjudged), sum(r[2] for r in unjudged)))
work.sort(key=lambda w: -(w[4] * 3 + w[5]))

kind = "standalone shelves" if untitled else "TITLES"
print(f"{len(work)} {kind} with unjudged collected editions   "
      f"({sum(w[4] for w in work)} provider-asserted containers, {sum(w[5] for w in work)} editions)\n")
for tid, title, runs, unjudged, prov, cols in work[:limit]:
    done = len(runs) - len(unjudged)
    print(f"  {title[:38]:<38} {len(unjudged):>2}/{len(runs)} runs to do  {cols:>4} editions  {prov:>3} asserted"
          + (f"   ({done} run(s) already judged - read those first)" if done else ""))
    for sid, sname, c, i, j, p in sorted(unjudged, key=lambda r: -(r[5] * 3 + r[2]))[:4]:
        print(f"      S{sid:<7} {c:>3} col {i:>4} iss {p:>3} asserted  {sname[:42]}")
