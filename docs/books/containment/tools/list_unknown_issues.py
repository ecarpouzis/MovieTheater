"""List the files whose issue number is missing, grouped by shelf, so they can be READ.

This tool decides nothing and infers nothing. It prints `itemId|shelf|filename` and stops. The answer
comes from reading the title — `Blood and Water 4of5` is #4, `Knights of the Dinner Table 137` is #137,
`Superboy 108 HD (Oct 1963)` is #108 — and most of this population has no number at all because most of
it is one-shots and graphic novels, which is the correct answer for them.

Files are grouped by SHELF and printed in title order, because a run read together is much safer than a
file read alone: seeing `Knights of the Dinner Table 117` beside `137` confirms the coordinate is an
issue, and seeing `Riki-Oh_v05` beside `v04` and `v06` confirms it is a volume.

`python list_unknown_issues.py [--slice k/n] [--limit N] [--collections]`
  --slice        take shelf k of every n, so several readers can work without colliding
  --collections  list collected editions with no judged range instead
"""
import collections
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
k, n, limit, skip, mode, min_files = 0, 1, 100000, 0, "issues", 1
for a in sys.argv[1:]:
    if a.startswith("--slice"):
        k, n = (int(x) for x in a.split("=", 1)[1].split("/"))
    elif a.startswith("--limit"):
        limit = int(a.split("=", 1)[1])
    elif a.startswith("--skip"):
        skip = int(a.split("=", 1)[1])
    elif a.startswith("--min"):
        min_files = int(a.split("=", 1)[1])
    elif a == "--collections":
        mode = "collections"

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
if mode == "issues":
    sql = """SELECT i.Id, i.SeriesId, coalesce(s.DisplayNameOverride, s.Name), i.FileName
             FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
             LEFT JOIN Series s ON s.Id = i.SeriesId
             WHERE cd.IsCollection = 0 AND coalesce(i.IsExcluded, 0) = 0
               AND (cd.IssueNo IS NULL OR trim(cd.IssueNo) = '')"""
else:
    sql = """SELECT i.Id, i.SeriesId, coalesce(s.DisplayNameOverride, s.Name), i.FileName
             FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
             LEFT JOIN Series s ON s.Id = i.SeriesId
             LEFT JOIN CollectedEditionSpan sp ON sp.ItemId = i.Id AND sp.Source = 3
             WHERE cd.IsCollection = 1 AND coalesce(i.IsExcluded, 0) = 0 AND sp.ItemId IS NULL"""

by_shelf = collections.defaultdict(list)
for iid, sid, name, fn in con.execute(sql):
    by_shelf[(sid or 0, name or "(no shelf)")].append((iid, fn))

# Ordered by how many numberless files a shelf holds, biggest first. 7,654 of the 10,970 sit ALONE on
# their shelf and are one-shots or graphic novels — correctly numberless, and reading them yields nothing.
# The readable numbers live on shelves that hold a run: Onepunch-man (94), Knights of the Dinner Table
# (81), What If v3 (69). `--min=2` drops the singletons entirely.
by_shelf = {k2: v for k2, v in by_shelf.items() if len(v) >= min_files}
shelves = sorted(by_shelf, key=lambda t: (-len(by_shelf[t]), t[1].lower(), t[0]))
mine = [s for idx, s in enumerate(shelves) if idx % n == k]
total = sum(len(by_shelf[s]) for s in mine)
print(f"# slice {k}/{n}: {len(mine)} shelves, {total} file(s) with no "
      f"{'issue number' if mode == 'issues' else 'judged range'}")
seen, shown = 0, 0
for sid, name in mine:
    rows = sorted(by_shelf[(sid, name)], key=lambda r: r[1].lower())
    header = False
    for iid, fn in rows:
        seen += 1
        if seen <= skip:
            continue
        if not header:
            print(f"\n## S{sid} {name}  ({len(rows)})")
            header = True
        print(f"{iid}|{fn}")
        shown += 1
        if shown >= limit:
            print(f"\n# stopped after {seen} of {total}; resume with --skip={seen}")
            raise SystemExit
print(f"\n# end of slice ({seen} file(s))")
