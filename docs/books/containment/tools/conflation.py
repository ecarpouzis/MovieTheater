"""Does this shelf hold ONE numbering, or several runs that each restart at #1?

`python conflation.py --list=<file>|<sid>...`

A range is only meaningful in one coordinate. If a shelf carries two runs that both number from #1, a
range written for either one captures the other's files, so PLAN.md §4.4's cross-run collision has to be
detected BEFORE anything is written. Four independent signals, each printed so it can be argued with:

  runs   distinct run folders — a folder named "... v2 (2001)" or "... (2010) (IDW)" is a publisher's
         statement that this is a separate run; the count is of distinct (vN, year) pairs seen
  dup    issue numbers used by more than one file of >=10pp (a 1pp variant cover is not a second issue)
  ovl    pairs of collected editions whose provider claims OVERLAP without one containing the other
  yrs    the span of cover years across the issue files

None of them is sufficient alone: dup is also what variant covers and annuals look like, and a single
long run legitimately spans decades. They are printed together so the shelf can be judged, not scored.
"""
import collections
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
PREFIX = "\\\\Library\\Public\\5 - Comics\\"
RXN = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")
RX_RUN = re.compile(r"(?i)\bv(\d{1,2})\b")
RX_YEAR = re.compile(r"\((\d{4})\)")


def num(s):
    m = RXN.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


ids = []
for a in sys.argv[1:]:
    if a.startswith("--list="):
        ids += [int(x) for x in open(os.path.join(HERE, a.split("=", 1)[1])).read().split()]
    elif not a.startswith("--"):
        ids.append(int(a))

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
print(f"{'series':<9} {'col':>4} {'iss':>5} {'runs':>4} {'dup':>4} {'ovl':>4} {'years':>11}  name / the run folders")
for sid in ids:
    name = con.execute("SELECT coalesce(DisplayNameOverride,Name) FROM Series WHERE Id=?", (sid,)).fetchone()
    rows = con.execute("""SELECT i.Id, i.Path, i.PageCount, cd.IsCollection, cd.IssueNo, cd.Year, cd.VolumeNo
                          FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
                          WHERE i.SeriesId=? AND coalesce(i.IsExcluded,0)=0""", (sid,)).fetchall()
    iss = [r for r in rows if not r[3]]
    cols = [r for r in rows if r[3]]
    real = [(num(r[4]), r[0]) for r in iss if num(r[4]) is not None and (r[2] or 0) >= 10]
    dup = sum(1 for v in collections.Counter(x[0] for x in real).values() if v > 1)
    years = [r[5] for r in rows if r[5]]
    folders = set()
    for r in rows:
        d = os.path.dirname(r[1] or "")
        d = d[len(PREFIX):] if d.startswith(PREFIX) else d
        for part in d.split("\\"):
            if RX_RUN.search(part) or RX_YEAR.search(part):
                folders.add(part)
                break
    spans = collections.defaultdict(list)
    for iid, a, b in con.execute("""SELECT s.ItemId, s.IssueStart, s.IssueEnd FROM CollectedEditionSpan s
                                    JOIN Item i ON i.Id=s.ItemId
                                    WHERE i.SeriesId=? AND s.IssueStart IS NOT NULL AND s.Source<>3""", (sid,)):
        spans[iid].append((a, b))
    best = {k: (min(x[0] for x in v), max(x[1] for x in v)) for k, v in spans.items()}
    ovl = 0
    keys = sorted(best)
    for i, x in enumerate(keys):
        for y in keys[i + 1:]:
            a1, b1 = best[x]
            a2, b2 = best[y]
            if a1 <= b2 and a2 <= b1 and not (a1 <= a2 and b2 <= b1) and not (a2 <= a1 and b1 <= b2):
                ovl += 1
    yr = f"{min(years)}-{max(years)}" if years else "-"
    print(f"S{sid:<8} {len(cols):>4} {len(iss):>5} {len(folders):>4} {dup:>4} {ovl:>4} {yr:>11}  "
          f"{(name[0] if name else '?')[:34]}")
    for f in sorted(folders)[:6]:
        print(f"{'':>36}   {f[:78]}")
