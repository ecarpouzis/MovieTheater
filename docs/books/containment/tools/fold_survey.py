"""Which title folders hold more than one run, and how badly the parser scattered them.

The point is the SCATTER, not the count: a run whose files sit on one Series needs nothing done, while
`Green Lantern v3 (1990)` spread over seven shelves cannot nest a single issue under its own trades,
because containment only looks inside a Series. Ranked by how many shelves a run is spread across.

Read-only. `python fold_survey.py [--limit N]`
"""
import collections
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fold_by_folder as F  # noqa: E402

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEP = chr(92)
limit = 40
for a in sys.argv[1:]:
    if a.startswith("--limit"):
        limit = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""SELECT i.Path, i.SeriesId, cd.IsCollection FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
                      WHERE i.Path LIKE '%5 - Comics%' AND coalesce(i.IsExcluded,0)=0""").fetchall()

byfolder = collections.defaultdict(lambda: collections.defaultdict(lambda: [set(), 0, 0]))
for path, sid, iscol in rows:
    parts = path.split("5 - Comics" + SEP, 1)[-1].split(SEP)
    if len(parts) < 3:
        continue
    title = parts[1]
    run = next((x for x in parts[2:] if F.run_of(x)), None)
    if not run:
        continue
    e = byfolder[title][run]
    if sid:
        e[0].add(sid)
    e[1] += 1
    e[2] += 0 if iscol else 1

work = []
for title, runs in byfolder.items():
    runs = {k: v for k, v in runs.items() if v[2] >= 1 or v[1] >= 3}
    if len(runs) < 2:
        continue
    scatter = sum(max(0, len(v[0]) - 1) for v in runs.values())
    work.append((title, len(runs), sum(v[1] for v in runs.values()), scatter,
                 max(len(v[0]) for v in runs.values())))
work.sort(key=lambda w: -(w[3] * 10 + w[2] / 50))
print(f"{len(work)} title folders hold 2+ runs; "
      f"{sum(w[3] for w in work)} run/shelf splits to heal, {sum(w[2] for w in work)} files in scope\n")
print(f"{'files':>6} {'runs':>5} {'scatter':>8} {'worst':>6}  title folder")
for title, nruns, nfiles, scatter, worst in work[:limit]:
    print(f"{nfiles:>6} {nruns:>5} {scatter:>8} {worst:>6}  {title[:56]}")
