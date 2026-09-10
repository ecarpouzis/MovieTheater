"""Write a decision file for a shelf without hand-typing a refusal for every edition.

`python mkdec.py <sid> <spec.txt>` where spec.txt is the decision file as far as it has been REASONED —
header comments, N/F lines, and whatever S lines the evidence settled — plus one line

    DEFAULT <the reason every remaining edition is refused>

Every collected edition on the shelf that no S or u line already names gets a `u` with that reason, in
item order, appended under a marker. The point is that the coverage guarantee (pass2 refuses a series
unless every edition is decided exactly once) is met by a reason someone wrote, not by silence.
"""
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.abspath(os.path.join(HERE, os.pardir, "decisions"))

sid = int(sys.argv[1])
spec = open(sys.argv[2], encoding="utf-8").read().splitlines()

default = None
body, decided = [], set()
for line in spec:
    m = re.match(r"^DEFAULT\s+(.*)$", line)
    if m:
        default = m.group(1).strip()
        continue
    body.append(line)
    m = re.match(r"^([Su])\s+(\d+)\b", line)
    if m:
        decided.add(int(m.group(2)))

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
cols = con.execute("""SELECT i.Id, i.FileName, i.PageCount FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
                      WHERE i.SeriesId=? AND cd.IsCollection=1 AND coalesce(i.IsExcluded,0)=0
                      ORDER BY i.Id""", (sid,)).fetchall()
missing = [c for c in cols if c[0] not in decided]
extra = decided - {c[0] for c in cols}
if extra:
    raise SystemExit(f"S{sid}: decisions for items not on this shelf: {sorted(extra)}")
if missing and not default:
    raise SystemExit(f"S{sid}: {len(missing)} editions undecided and no DEFAULT line given")

out = list(body)
if missing:
    out += ["", f"# the remaining {len(missing)} editions: {default}"]
    out += [f"u {c[0]} {default}" for c in missing]
path = os.path.join(DEC, f"S{sid}.txt")
open(path, "w", encoding="utf-8").write("\n".join(out).rstrip() + "\n")
print(f"S{sid}: {len(cols)} editions — {len(decided)} reasoned, {len(missing)} defaulted -> {path}")
