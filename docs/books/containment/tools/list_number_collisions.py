"""Files that claim the SAME issue number as a sibling on the same shelf, listed so they can be READ.

A collision is where a wrong issue number shows up. It has four causes and only reading tells them apart:

  a duplicate rip       two copies of the same comic. Both numbers are right; nothing to do.
  a reading-order prefix taken as the number. `01 Batman 022 - September 2013` stored as #1 collides with
                        the real #1. Item 17706 is stored as #7 because the parser took its `[07]` prefix
                        while the title says `Justice League of America 12`.
  a conflated shelf     two RUNS sharing one number space — `Green Lantern v2 026` and `v3 026` both on
                        S64823. The numbers are right; the shelf is wrong, and the fix is a split.
  an annual or special  `Batman Annual 25` beside `Batman 25`. A separate numbering that the flat IssueNo
                        column cannot express; usually the honest answer is to leave the annual blank.

The output groups a shelf's colliding files together with their page counts, because page count separates a
duplicate rip (same size) from a different comic (different size) at no cost.

`python list_number_collisions.py [--slice k/n] [--limit N] [--skip N] [--min N]`
  --min   only shelves with at least this many colliding files (default 2)
"""
import collections
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
k, n, limit, skip, min_files = 0, 1, 100000, 0, 2
for a in sys.argv[1:]:
    if a.startswith("--slice"):
        k, n = (int(x) for x in a.split("=", 1)[1].split("/"))
    elif a.startswith("--limit"):
        limit = int(a.split("=", 1)[1])
    elif a.startswith("--skip"):
        skip = int(a.split("=", 1)[1])
    elif a.startswith("--min"):
        min_files = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""SELECT i.SeriesId, coalesce(s.DisplayNameOverride, s.Name), cd.IssueNo,
                             i.Id, i.FileName, coalesce(i.PageCount, 0)
                      FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                      LEFT JOIN Series s ON s.Id = i.SeriesId
                      WHERE cd.IsCollection = 0 AND coalesce(i.IsExcluded, 0) = 0
                        AND cd.IssueNo IS NOT NULL AND trim(cd.IssueNo) <> ''
                        AND i.SeriesId IS NOT NULL""").fetchall()

groups = collections.defaultdict(list)
for sid, name, no, iid, fn, pages in rows:
    groups[(sid, name or "?", str(no))].append((iid, fn, pages))
groups = {g: v for g, v in groups.items() if len(v) > 1}

by_shelf = collections.defaultdict(list)
for (sid, name, no), v in groups.items():
    by_shelf[(sid, name)].append((no, v))
by_shelf = {s: v for s, v in by_shelf.items() if sum(len(x[1]) for x in v) >= min_files}

shelves = sorted(by_shelf, key=lambda s: (-sum(len(x[1]) for x in by_shelf[s]), s[1].lower()))
mine = [s for idx, s in enumerate(shelves) if idx % n == k]
total = sum(len(x[1]) for s in mine for x in by_shelf[s])
print(f"# slice {k}/{n}: {len(mine)} shelves, {total} colliding file(s)")

seen, shown = 0, 0
for sid, name in mine:
    header = False
    for no, v in sorted(by_shelf[(sid, name)], key=lambda t: (len(t[0]), t[0])):
        if seen + len(v) <= skip:
            seen += len(v)
            continue
        if not header:
            print(f"\n## S{sid} {name}")
            header = True
        print(f"  #{no}")
        for iid, fn, pages in sorted(v, key=lambda r: r[1].lower()):
            print(f"{iid}|{pages}pp|{fn}")
        seen += len(v)
        shown += len(v)
        if shown >= limit:
            print(f"\n# stopped after {seen} of {total}; resume with --skip={seen}")
            raise SystemExit
print(f"\n# end of slice ({seen} file(s))")
