"""Print the DIRECTORY names each archive on a shelf uses, verbatim.

Many manga rips do not put a `cNNN` token on the page; they put every chapter in its own FOLDER inside
the archive — `Navigation 10\\...`, `Chapter 041\\...`, `[Vol.03] Ch.017\\...`. That folder name is the
book stating its own contents just as strongly as a page token, and `chapters_from_archive.py` cannot
see it.

This tool transcribes; it does not infer. It prints the distinct intermediate folder names of every
collected edition in a series, in archive order, so the shelf's convention can be READ before anything
is decided.

    python chapter_dirs.py <seriesId> [--items=1,2] [--max=14]
"""
import collections
import os
import subprocess
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

sid, items, mx = None, None, 14
for a in sys.argv[1:]:
    if a.startswith("--items="):
        items = [int(x) for x in a.split("=", 1)[1].split(",")]
    elif a.startswith("--max="):
        mx = int(a.split("=", 1)[1])
    else:
        sid = int(a)

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
if items:
    rows = [con.execute("SELECT Id, Path, FileName FROM Item WHERE Id = ?", (i,)).fetchone() for i in items]
else:
    rows = con.execute("""
        SELECT i.Id, i.Path, i.FileName FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0 AND cd.IsCollection = 1
        ORDER BY i.FileName""", (sid,)).fetchall()

for iid, path, fn in rows:
    out = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                         timeout=300, errors="ignore").stdout
    dirs, loose, total = collections.Counter(), 0, 0
    for line in out.splitlines():
        parts = line.split(None, 5)
        if len(parts) < 6 or not parts[5].lower().endswith(IMG):
            continue
        total += 1
        d = os.path.dirname(parts[5].replace("/", "\\"))
        segs = [s for s in d.split("\\") if s]
        if len(segs) >= 2:
            dirs[segs[-1]] += 1
        elif len(segs) == 1:
            dirs[f"<root: {segs[0]}>"] += 1
        else:
            loose += 1
    print(f"\n[{iid}] {fn[:78]}   {total} images")
    if loose:
        print(f"      <no folder> x{loose}")
    keys = sorted(dirs)
    for k in keys[:mx]:
        print(f"      {k}  x{dirs[k]}")
    if len(keys) > mx:
        print(f"      ... {len(keys) - mx} more folders, last: {keys[-1]}")
