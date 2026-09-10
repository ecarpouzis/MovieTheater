"""The shelves where a wrong range costs FILES, ranked, and split into two disjoint halves.

A shelf earns a place when all three are true:
  * it holds collected editions AND loose single issues — so a range decides whether a file is redundant;
  * no range on it has been judged;
  * a PROVIDER is currently asserting one — so the shelf is not silent, it is saying something unchecked.

That is the population where the de-duplication would act on an unexamined claim. Everything else is
either silent (wasteful, not dangerous) or already judged.

`python risk_queue.py [--half a|b]` — the halves are assigned by alternating rank so both are the same
size and the same difficulty, and they never overlap.
"""
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
half = None
for a in sys.argv[1:]:
    if a.startswith("--half"):
        half = a.split("=", 1)[1] if "=" in a else "a"

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
 SELECT i.SeriesId,
        sum(cd.IsCollection) AS cols, sum(1-cd.IsCollection) AS iss,
        sum(CASE WHEN s.Source = 3 AND s.IssueStart IS NOT NULL THEN 1 ELSE 0 END) AS judged,
        sum(CASE WHEN n.TrackRole = 1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END) AS prov,
        coalesce(x.DisplayNameOverride, x.Name)
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 LEFT JOIN CollectedEditionSpan s ON s.ItemId = i.Id AND s.Source = 3
 LEFT JOIN CollectionNode n ON n.ItemId = i.Id
 LEFT JOIN Series x ON x.Id = i.SeriesId
 WHERE coalesce(i.IsExcluded, 0) = 0 AND i.SeriesId IS NOT NULL
 GROUP BY i.SeriesId HAVING cols > 0 AND iss > 0 AND judged = 0 AND prov > 0""").fetchall()

rows.sort(key=lambda r: -(r[4] * 3 + r[1]))
if half:
    rows = [r for n, r in enumerate(rows) if (n % 2 == 0) == (half == "a")]

print(f"{len(rows)} shelves" + (f" (half {half})" if half else "") +
      f"   {sum(r[4] for r in rows)} provider-asserted containers, {sum(r[1] for r in rows)} collected editions")
print(f"\n  {'series':<9} {'iss':>5} {'col':>4} {'prov':>5}  name")
for r in rows:
    print(f"  S{r[0]:<8} {int(r[2]):>5} {int(r[1]):>4} {int(r[4]):>5}  {(r[5] or '?')[:56]}")
