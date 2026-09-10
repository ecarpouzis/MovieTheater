"""Rank the undone Marvel shelves by EXPOSURE: how many real issue files a live provider claim covers.

That is the number this pass exists to protect. A shelf where LOCG asserts #1-605 over 72 files is where
reading four pages pays; a shelf of trades with no loose issues cannot lose anything either way.

Read-only. `python marvel_exposure.py [--limit N]`
"""
import collections
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
DEC = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "decisions")
done = {int(re.search(r"S(\d+)", f).group(1)) for f in os.listdir(DEC) if re.match(r"^S\d+\.txt$", f)}
RXN = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")
limit = 40
for a in sys.argv[1:]:
    if a.startswith("--limit"):
        limit = int(a.split("=", 1)[1])


def num(s):
    m = RXN.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
    SELECT i.SeriesId, i.Id, i.Path, i.PageCount, cd.IsCollection, cd.IssueNo, i.FileName
    FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
    WHERE coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL""").fetchall()
by_sid = collections.defaultdict(list)
marvel = set()
for sid, iid, path, pc, isc, ino, fn in rows:
    by_sid[sid].append((iid, pc or 0, isc, num(ino), fn))
    if path and "\\5 - Comics\\Marvel\\" in path:
        marvel.add(sid)

spans = collections.defaultdict(list)
for iid, sid, src, a, b in con.execute("""
        SELECT s.ItemId, i.SeriesId, s.Source, s.IssueStart, s.IssueEnd
        FROM CollectedEditionSpan s JOIN Item i ON i.Id = s.ItemId
        WHERE s.Source <> 3 AND s.IssueStart IS NOT NULL"""):
    spans[sid].append((iid, src, a, b))

judged = {sid for (sid,) in con.execute(
    """SELECT DISTINCT i.SeriesId FROM CollectedEditionSpan s JOIN Item i ON i.Id = s.ItemId
       WHERE s.Source = 3 AND s.IssueStart IS NOT NULL""")}

work = []
for sid in marvel:
    if sid in done or sid in judged:
        continue
    items = by_sid[sid]
    cols = [x for x in items if x[2]]
    real = [x for x in items if not x[2] and x[3] is not None and x[1] >= 8]
    if not cols:
        continue
    worst, worst_iid, worst_span = 0, None, None
    for iid, src, a, b in spans.get(sid, ()):
        hit = sum(1 for x in real if a <= x[3] <= b)
        if hit > worst:
            worst, worst_iid, worst_span = hit, iid, (src, a, b)
    name = con.execute("SELECT coalesce(DisplayNameOverride,Name) FROM Series WHERE Id=?", (sid,)).fetchone()[0]
    work.append((worst, sid, name, len(cols), len(real), worst_iid, worst_span))

work.sort(reverse=True)
print(f"{len(work)} undone Marvel shelves; {sum(1 for w in work if w[0])} carry a claim over a real file\n")
for worst, sid, name, c, r, iid, sp in work[:limit]:
    tag = f"  worst: item {iid} src={sp[0]} #{sp[1]:g}-{sp[2]:g} covers {worst}" if iid else ""
    print(f"  S{sid:<7} {c:>3} col {r:>4} iss  {name[:44]:<44}{tag}")
