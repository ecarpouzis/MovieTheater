"""Risk-ranked shelves that do NOT already have a decision file.

Adds the one column risk_queue.py lacks: how many of the loose files are REAL issues rather than 1pp
variant-cover scans. A shelf whose "issues" are all cover rips cannot lose a page to a wrong range
(ContainedDuplicateJob holds anything under MinIssuePages back, PLAN.md 14.6); a shelf of 24pp floppies can.
"""
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
DEC = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "decisions")
have = {f[1:-4] for f in os.listdir(DEC) if f.startswith("S") and f.endswith(".txt")}

limit, minreal = 60, 0
for a in sys.argv[1:]:
    if a.startswith("--limit"):
        limit = int(a.split("=", 1)[1])
    elif a.startswith("--minreal"):
        minreal = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
 SELECT i.SeriesId,
        sum(cd.IsCollection) AS cols, sum(1-cd.IsCollection) AS iss,
        sum(CASE WHEN cd.IsCollection = 0 AND coalesce(i.PageCount,0) >= 8 THEN 1 ELSE 0 END) AS real_iss,
        sum(CASE WHEN s.Source = 3 AND s.IssueStart IS NOT NULL THEN 1 ELSE 0 END) AS judged,
        sum(CASE WHEN n.TrackRole = 1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END) AS prov,
        coalesce(x.DisplayNameOverride, x.Name)
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 LEFT JOIN CollectedEditionSpan s ON s.ItemId = i.Id AND s.Source = 3
 LEFT JOIN CollectionNode n ON n.ItemId = i.Id
 LEFT JOIN Series x ON x.Id = i.SeriesId
 WHERE coalesce(i.IsExcluded, 0) = 0 AND i.SeriesId IS NOT NULL
 GROUP BY i.SeriesId HAVING cols > 0 AND iss > 0 AND judged = 0 AND prov > 0""").fetchall()

rows = [r for r in rows if str(r[0]) not in have and r[3] >= minreal]
rows.sort(key=lambda r: -(r[5] * 3 + r[1]))
print(f"{len(rows)} shelves with no decision file   "
      f"{sum(r[5] for r in rows)} provider-asserted, {sum(r[1] for r in rows)} editions\n")
print(f"  {'series':<9} {'iss':>5} {'real':>5} {'col':>4} {'prov':>5}  name")
for r in rows[:limit]:
    print(f"  S{r[0]:<8} {int(r[2]):>5} {int(r[3]):>5} {int(r[1]):>4} {int(r[5]):>5}  {(r[6] or '?')[:56]}")
