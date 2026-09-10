"""Route each shelf in a risk-queue half to the treatment it actually needs, before reading any of them.

Three outcomes, and they need different work:
  SPLIT   the shelf holds more than one run folder, or its issue numbers collide — ranges written now
          would land in the wrong run, so it must be split first.
  READ    one run, no collisions: the trades can be opened and judged.
  THIN    the "issues" are 1-page variant-cover scans or similar. Page count settles what a file is
          before any provider does (PLAN §1.6), and a 1pp file is not an issue anything can contain.

Read-only. Prints the routing so the reading can be batched instead of discovered one shelf at a time.
"""
import collections
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEP = chr(92)
RX_VOL = re.compile(r"\bv(?:ol(?:ume)?)?\.?\s*\d{1,3}\b", re.I)
RX_YEAR = re.compile(r"\((?:19|20)\d{2}\)")
RX_LEAD = re.compile(r"^\s*[#_]*\d{1,3}[\s._-]+")
RX_NUM = re.compile(r"^\d+(\.\d+)?$")

half = "a"
for a in sys.argv[1:]:
    if a.startswith("--half"):
        half = a.split("=", 1)[1]

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
 SELECT i.SeriesId, sum(cd.IsCollection), sum(1-cd.IsCollection),
        sum(CASE WHEN s.Source=3 AND s.IssueStart IS NOT NULL THEN 1 ELSE 0 END),
        sum(CASE WHEN n.TrackRole=1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END),
        coalesce(x.DisplayNameOverride, x.Name)
 FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
 LEFT JOIN CollectedEditionSpan s ON s.ItemId=i.Id AND s.Source=3
 LEFT JOIN CollectionNode n ON n.ItemId=i.Id
 LEFT JOIN Series x ON x.Id=i.SeriesId
 WHERE coalesce(i.IsExcluded,0)=0 AND i.SeriesId IS NOT NULL
 GROUP BY i.SeriesId HAVING sum(cd.IsCollection)>0 AND sum(1-cd.IsCollection)>0
   AND sum(CASE WHEN s.Source=3 AND s.IssueStart IS NOT NULL THEN 1 ELSE 0 END)=0
   AND sum(CASE WHEN n.TrackRole=1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END)>0""").fetchall()
rows.sort(key=lambda r: -(r[4] * 3 + r[1]))
rows = [r for n, r in enumerate(rows) if (n % 2 == 0) == (half == "a")]


def run_folder(path):
    parts = [p for p in (path or "").replace("/", SEP).split(SEP) if p]
    found = None
    for part in parts[:-1]:
        if RX_VOL.search(part) and RX_YEAR.search(part):
            found = part
    return RX_LEAD.sub("", found).strip() if found else None


buckets = collections.defaultdict(list)
for sid, cols, iss, judged, prov, name in rows:
    items = con.execute("""SELECT i.Path, cd.IsCollection, cd.IssueNo, i.PageCount
        FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
        WHERE i.SeriesId=? AND coalesce(i.IsExcluded,0)=0""", (sid,)).fetchall()
    runs = {run_folder(p) for p, c, n, pc in items} - {None}
    nums = [float(n) for p, c, n, pc in items if not c and n and RX_NUM.match(str(n))]
    dupes = sum(v for v in collections.Counter(nums).values() if v > 1)
    thin = sum(1 for p, c, n, pc in items if not c and (pc or 0) <= 2)
    real_iss = int(iss) - thin
    why = ("SPLIT" if len(runs) > 1 or dupes > max(3, 0.2 * len(nums))
           else "THIN" if real_iss <= 0
           else "READ")
    buckets[why].append((sid, int(cols), int(iss), thin, int(prov), len(runs), dupes, name))

print(f"half {half}: {len(rows)} shelves")
for why in ("SPLIT", "READ", "THIN"):
    b = buckets[why]
    print(f"\n== {why}: {len(b)} shelves, {sum(x[4] for x in b)} provider-asserted containers ==")
    for sid, cols, iss, thin, prov, runs, dupes, name in b[:18]:
        extra = f"runs={runs} dupes={dupes}" if why == "SPLIT" else (f"{thin} of {iss} issues are <=2pp" if why == "THIN" else "")
        print(f"  S{sid:<8} {iss:>4} iss {cols:>3} col {prov:>3} prov  {extra:<26} {(name or '?')[:40]}")
    if len(b) > 18:
        print(f"  ... and {len(b)-18} more")
