"""Triage the risk queue: for each shelf, what the providers claim, what agrees, and what is at stake.

`python triage_b.py <sid> [sid...]`  or  `python triage_b.py --half=b`

Per shelf it prints one block: how many collected editions have two independent providers saying the
SAME range (the only cheap settle), how many have providers contradicting each other, whether the issue
ladder repeats a number (a run restarting from #1 — the conflation signal), and how many REAL issue
files (>=10pp, so a 1pp variant cover is not counted) fall inside a claim. That last number is the only
thing that turns a wrong range into lost files.
"""
import collections
import os
import re
import sqlite3
import subprocess
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
RX = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")
SRC = {0: "LOCG", 1: "GCD", 2: "CV", 3: "CUR"}


def num(s):
    m = RX.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


def shelves(argv):
    half = None
    ids = []
    for a in argv:
        if a.startswith("--half"):
            half = a.split("=", 1)[1] if "=" in a else "a"
        elif not a.startswith("--"):
            ids.append(int(a))
    if ids:
        return ids
    out = subprocess.run([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), "risk_queue.py"),
                          f"--half={half or 'a'}"], capture_output=True, text=True).stdout
    return [int(m.group(1)) for m in re.finditer(r"^  S(\d+)", out, re.M)]


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
print(f"{'series':<9} {'col':>4} {'iss':>5} {'agree':>5} {'confl':>5} {'lone':>5} {'none':>5} "
      f"{'dupN':>5} {'atrisk':>6}  name")
tot = collections.Counter()
for sid in shelves(sys.argv[1:]):
    name = con.execute("SELECT coalesce(DisplayNameOverride,Name) FROM Series WHERE Id=?", (sid,)).fetchone()
    rows = con.execute("""SELECT i.Id, i.PageCount, cd.IsCollection, cd.IssueNo, cd.VolumeNo, i.FileName
                          FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
                          WHERE i.SeriesId=? AND coalesce(i.IsExcluded,0)=0""", (sid,)).fetchall()
    spans = collections.defaultdict(dict)
    for iid, src, a, b in con.execute("""SELECT s.ItemId, s.Source, s.IssueStart, s.IssueEnd
                                         FROM CollectedEditionSpan s JOIN Item i ON i.Id=s.ItemId
                                         WHERE i.SeriesId=? AND s.IssueStart IS NOT NULL""", (sid,)):
        spans[iid][src] = (a, b)
    cols = [r for r in rows if r[2]]
    iss = [r for r in rows if not r[2]]
    real = [(num(r[3]), r[0], r[1]) for r in iss if num(r[3]) is not None and (r[1] or 0) >= 10]
    cnt = collections.Counter(x[0] for x in real)
    dupN = sum(1 for v in cnt.values() if v > 1)

    agree = conflict = lone = nothing = 0
    atrisk = set()
    for c in cols:
        sp = {k: v for k, v in spans.get(c[0], {}).items() if k != 3}
        if not sp:
            nothing += 1
        elif len(set(sp.values())) == 1 and len(sp) > 1:
            agree += 1
        elif len(sp) == 1:
            lone += 1
        else:
            conflict += 1
        for a, b in sp.values():
            atrisk |= {x[1] for x in real if a <= x[0] <= b}
    tot["col"] += len(cols); tot["agree"] += agree; tot["conflict"] += conflict
    tot["lone"] += lone; tot["none"] += nothing; tot["atrisk"] += len(atrisk)
    print(f"S{sid:<8} {len(cols):>4} {len(iss):>5} {agree:>5} {conflict:>5} {lone:>5} {nothing:>5} "
          f"{dupN:>5} {len(atrisk):>6}  {(name[0] if name else '?')[:44]}")
print(f"\nTOTAL cols={tot['col']} agree={tot['agree']} conflict={tot['conflict']} "
      f"lone={tot['lone']} none={tot['none']} at-risk-files={tot['atrisk']}")
