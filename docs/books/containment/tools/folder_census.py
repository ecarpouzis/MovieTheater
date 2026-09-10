"""Census of the library's top folders, so a population (Manga, newspaper strips, ...) can be found by
the librarian's own filing rather than by guessing at titles. Read-only."""
import collections
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

depth = 2
pat = None
for a in sys.argv[1:]:
    if a.startswith("--depth="):
        depth = int(a.split("=", 1)[1])
    elif a.startswith("--under="):
        pat = a.split("=", 1)[1]

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
c = collections.Counter()
for (p,) in con.execute("SELECT Path FROM Item WHERE Path IS NOT NULL AND coalesce(IsExcluded,0)=0"):
    if pat and pat.lower() not in p.lower():
        continue
    segs = [s for s in p.split("\\") if s]
    # \\Library\Public\5 - Comics\<a>\<b>\...
    body = segs[3:3 + depth]
    if body:
        c["\\".join(body)] += 1
for k, v in c.most_common(80):
    print(f"{v:>7}  {k}")
