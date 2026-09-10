"""The smallest view that still lets a shelf be judged: the runs, the ladder's collisions, the books.

`python brief.py <sid> [sid...]`

For shelves whose answer turns on whether one numbering or several are in play, the full worksheet is
more than is needed. This prints the run folders (a publisher's own statement that a file belongs to a
separate run), how many issue numbers name more than one file of >=10pp, the ladder's runs of present
numbers, and one line per collected edition — id, pages, the winning claim and how many real issue files
it covers. Everything a `conflated-series` refusal has to name, and nothing else.
"""
import collections
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
PREFIX = "\\\\Library\\Public\\5 - Comics\\"
SRC = {0: "LOCG", 1: "GCD", 2: "CV"}
RXN = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")
RX_RUN = re.compile(r"(?i)\bv\d{1,2}\b|\(\d{4}\)")


def num(s):
    m = RXN.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
for sid in (int(a) for a in sys.argv[1:] if not a.startswith("--")):
    name = con.execute("SELECT coalesce(DisplayNameOverride,Name) FROM Series WHERE Id=?", (sid,)).fetchone()
    rows = con.execute("""SELECT i.Id, i.Path, i.FileName, i.PageCount, cd.IsCollection, cd.IssueNo, cd.VolumeNo
                          FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
                          WHERE i.SeriesId=? AND coalesce(i.IsExcluded,0)=0""", (sid,)).fetchall()
    spans = collections.defaultdict(dict)
    for iid, src, a, b, t in con.execute("""SELECT s.ItemId, s.Source, s.IssueStart, s.IssueEnd, s.EditionTitle
                                            FROM CollectedEditionSpan s JOIN Item i ON i.Id=s.ItemId
                                            WHERE i.SeriesId=? AND s.Source<>3 AND s.IssueStart IS NOT NULL""", (sid,)):
        spans[iid][src] = (a, b, t)
    cols = [r for r in rows if r[4]]
    iss = [r for r in rows if not r[4]]
    real = [(num(r[5]), r[0]) for r in iss if num(r[5]) is not None and (r[3] or 0) >= 10]
    cnt = collections.Counter(x[0] for x in real)
    dup = sorted(k for k, v in cnt.items() if v > 1)
    have = sorted(set(x[0] for x in real))
    runs, s = [], None
    for i, v in enumerate(have):
        if s is None:
            s = v
        if i + 1 == len(have) or have[i + 1] != v + 1:
            runs.append((s, v))
            s = None
    folders = collections.Counter()
    for r in rows:
        d = os.path.dirname(r[1] or "")
        d = d[len(PREFIX):] if d.startswith(PREFIX) else d
        folders[d] += 1
    print(f"\n== S{sid} {name[0] if name else '?'} — {len(cols)} editions, {len(iss)} issue files "
          f"({len(real)} >=10pp)")
    print(f"   ladder: " + ", ".join(f"{a:g}-{b:g}" if a != b else f"{a:g}" for a, b in runs[:24]))
    if dup:
        print(f"   numbers naming more than one file ({len(dup)}): {[f'{d:g}' for d in dup[:24]]}")
    for f, n in folders.most_common(9):
        print(f"   [{n:>4}] {f[-92:]}")
    for iid, path, fn, pc, _, ino, vol in sorted(cols, key=lambda r: (r[6] or 0, r[2])):
        cl = ""
        for src in sorted(spans.get(iid, {})):
            a, b, t = spans[iid][src]
            hit = len([x for x in real if a <= x[0] <= b])
            cl += f"  {SRC[src]}#{a:g}-{b:g}({hit}f)"
        print(f"    [{iid:>7}] vol={str(vol):>3} {str(pc):>5}pp {fn[:62]:<62}{cl}")
