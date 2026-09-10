"""Containers on one shelf whose judged ranges OVERLAP - the shape a conflated shelf makes.

`overclaim_check.py` asks whether a container holds more than its own range. This asks the question one
level up: whether the shelf's containers can all be true at once. Two trades of the SAME run never claim
the same issue (except the rare deliberate case, Green Lantern Vol. 05/07 sharing #38); two trades of
DIFFERENT runs that both number from #1 always do.

Conan (S4086) is the worst of them: six Dark Horse Conan series - the 2004 run, Cimmeria, Road of Kings,
the 2012 Barbarian and the 2014 Avenger - have their trades filed on one shelf, and `Conan Vol. 11 - Road
of Kings` claims #1-6 while `Vol. 13 - Queen of the Black Coast` and `Vol. 17 - Shadows Over Kush` claim
the same six. The only issues on the shelf are Conan (2004) #1-50, so all three were holding the wrong
comic's issues.

`python overlap_check.py [--all]`   --all drops the conflated-flag filter and looks at every shelf.
"""
import collections
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
ALL = "--all" in sys.argv
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

flagged = {r[0] for r in con.execute(
    "SELECT DISTINCT SeriesId FROM ContainmentFlag WHERE Flag = 'conflated-series'")}

rows = list(con.execute("""
 SELECT n.SeriesId, n.ItemId, s.IssueStart, s.IssueEnd, n.ContainsCount, i.PageCount, i.FileName
 FROM CollectionNode n
 JOIN CollectedEditionSpan s ON s.ItemId = n.ItemId AND s.Source = 3 AND s.IssueStart IS NOT NULL
 JOIN Item i ON i.Id = n.ItemId
 WHERE n.TrackRole = 1 AND n.ContainsCount > 0"""))

by_shelf = collections.defaultdict(list)
for sid, iid, a, b, kids, pages, fn in rows:
    by_shelf[sid].append((iid, a, b, kids, pages, fn))

hits = []
for sid, books in by_shelf.items():
    if not ALL and sid not in flagged:
        continue
    books.sort(key=lambda r: (r[1], r[2]))
    over = set()
    for i in range(len(books)):
        for j in range(i + 1, len(books)):
            a1, b1, a2, b2 = books[i][1], books[i][2], books[j][1], books[j][2]
            if b1 < a2 or b2 < a1:
                continue
            # One range wholly inside another is not a conflict, it is nesting: a volume sits inside the
            # omnibus that reprints it, and container-in-container is what the tree is for. Only a PARTIAL
            # overlap - two books each claiming issues the other does not - cannot both be true of one run.
            if (a1 <= a2 and b1 >= b2) or (a2 <= a1 and b2 >= b1):
                continue
            over.add(books[i][0])
            over.add(books[j][0])
    if over:
        hits.append((sid, [b for b in books if b[0] in over]))

hits.sort(key=lambda h: -sum(b[3] for b in h[1]))
print(f"{len(hits)} shelf/shelves where judged ranges overlap, "
      f"{sum(len(h[1]) for h in hits)} containers, {sum(b[3] for h in hits for b in h[1])} files held\n")
for sid, books in hits:
    nm = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,)).fetchone()
    print(f"S{sid}  {(nm[0] if nm else '?')[:50]}")
    for iid, a, b, kids, pages, fn in books:
        print(f"    {iid:<7} #{int(a)}-{int(b):<5} {pages:>4}pp  holds {kids:>3}  {fn[:56]}")
    print()
