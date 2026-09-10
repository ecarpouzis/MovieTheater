"""Build a run of contact sheets over a whole shelf, so a series can be read in a few looks.

    python sheets.py --series=2918 --page=6 --crop=0,0.42,1,1 --per=6 --out=<dir> [--prefix=bleach]
    python sheets.py --items=1,2,3 ...            (an explicit list instead of a shelf)

Chunked and observable per the standing rule: it prints each sheet as it lands and each sheet is usable
on its own, so a kill costs at most the sheet in flight. Read-only.
"""
import os
import subprocess
import sqlite3
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"

series, items, page, crop, per, out, prefix, width = None, None, "6", "0,0.42,1,1", 6, ".", "sheet", "900"
cols = "2"
for a in sys.argv[1:]:
    k, _, v = a.partition("=")
    if k == "--series":
        series = int(v)
    elif k == "--items":
        items = [int(x) for x in v.split(",")]
    elif k == "--page" or k == "--pages":
        page = v
    elif k == "--crop":
        crop = v
    elif k == "--per":
        per = int(v)
    elif k == "--out":
        out = v
    elif k == "--prefix":
        prefix = v
    elif k == "--width":
        width = v
    elif k == "--cols":
        cols = v

if items is None:
    con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
    items = [r[0] for r in con.execute(
        """SELECT i.Id FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
           WHERE i.SeriesId = ? AND coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
           ORDER BY i.FileName""", (series,))]
os.makedirs(out, exist_ok=True)
print(f"{len(items)} items -> {(len(items) + per - 1) // per} sheets", flush=True)
for n in range(0, len(items), per):
    chunk = items[n:n + per]
    dst = os.path.join(out, f"{prefix}_{n // per:02d}.jpg")
    subprocess.run([sys.executable, os.path.join(HERE, "contact_sheet.py"),
                    "--items=" + ",".join(str(i) for i in chunk), "--pages=" + page,
                    "--crop=" + crop, "--cols=" + cols, "--width=" + width, "--out=" + dst])
    print(f"   sheet {n // per:02d}: {len(chunk)} items -> {dst}", flush=True)
