"""Transcribe a read table of ranges into decision-file S lines, with the titles read from the catalog.

The judgement is mine; the TRANSCRIPTION should not be, because a hand-typed 135 that should be 136 is
exactly the kind of error nobody catches (same reason make_manga_decision.py exists). Input is one
`<itemId> <start> <end>` per line, plus `--conf` and `--why` for the evidence sentence; `{lo}`/`{hi}`/
`{n}` in --why are filled per line. Lines beginning `u ` or `#` pass straight through.

    python emit_dec.py ranges.txt --conf=0.95 --why="the volume's own printed contents page lists
        chapters {lo}-{hi}" > spec.txt
"""
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
conf, why = "0.95", "read from the book"
src = None
for a in sys.argv[1:]:
    if a.startswith("--conf="):
        conf = a.split("=", 1)[1]
    elif a.startswith("--why="):
        why = a.split("=", 1)[1]
    else:
        src = a

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
names = {r[0]: r[1] for r in con.execute("SELECT Id, FileName FROM Item")}


def title(iid):
    return re.sub(r"\.(cbz|cbr|cb7|cbt|pdf|epub)$", "", names.get(iid, ""), flags=re.I).split(" (")[0]


for line in open(src, encoding="utf-8"):
    s = line.rstrip("\n")
    if not s.strip() or s.lstrip().startswith("#") or re.match(r"^\s*[uUNF]\s", s):
        print(s)
        continue
    parts = s.split()
    iid, lo, hi = int(parts[0]), parts[1], parts[2]
    n = "" if len(parts) < 4 else parts[3]
    print(f"S {iid} {lo} {hi} {conf} | {title(iid)} | "
          + why.format(lo=lo, hi=hi, n=n))
