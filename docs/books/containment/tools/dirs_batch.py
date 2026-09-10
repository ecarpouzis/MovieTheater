"""Run the chapter-folder transcription over a list of shelves and write it to a file, resumably.

Chunked and observable per the standing rule: `--limit` shelves a run, a cursor in
`dirs_batch_state.json`, one flush per shelf, and it appends to `dirs_batch.txt` so an interrupted run
costs at most the shelf it was on. It transcribes archive folder names and NOTHING else - no ranges,
no decisions.

    python dirs_batch.py --shelves=1287,2918 [--limit=40]
    python dirs_batch.py --manga --limit=40
"""
import collections
import json
import os
import re
import subprocess
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")
STATE = os.path.join(HERE, "dirs_batch_state.json")
OUT = os.path.join(HERE, "dirs_batch.txt")
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

shelves, limit, manga = None, 40, False
for a in sys.argv[1:]:
    if a.startswith("--shelves="):
        shelves = [int(x) for x in a.split("=", 1)[1].split(",")]
    elif a.startswith("--limit="):
        limit = int(a.split("=", 1)[1])
    elif a == "--manga":
        manga = True

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
have = {int(m.group(1)) for f in os.listdir(DEC) if (m := re.match(r"^S(\d+)\.txt$", f))}
state = json.load(open(STATE)) if os.path.exists(STATE) else {"done": []}
done = set(state["done"])

if shelves is None:
    rows = con.execute("""
        SELECT i.SeriesId, sum(cd.IsCollection) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE coalesce(i.IsExcluded,0) = 0 AND i.Path LIKE '%\\Manga\\%' AND i.SeriesId IS NOT NULL
        GROUP BY i.SeriesId HAVING sum(cd.IsCollection) > 0
        ORDER BY sum(cd.IsCollection) DESC""").fetchall()
    shelves = [r[0] for r in rows if r[0] not in have]

todo = [s for s in shelves if s not in done][:limit]
out = open(OUT, "a", encoding="utf-8")
for n, sid in enumerate(todo, 1):
    name = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?",
                       (sid,)).fetchone()
    name = name[0] if name else "?"
    rows = con.execute("""
        SELECT i.Id, i.Path, i.FileName, i.PageCount FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0 AND cd.IsCollection = 1
        ORDER BY i.FileName""", (sid,)).fetchall()
    out.write(f"\n\n===== S{sid}  {name}   {len(rows)} collected editions\n")
    for iid, path, fn, pc in rows:
        try:
            lst = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                                 timeout=300, errors="ignore").stdout
        except (OSError, subprocess.SubprocessError):
            out.write(f"  [{iid}] {fn[:76]}   UNREADABLE\n")
            continue
        dirs, loose, total = collections.Counter(), 0, 0
        for line in lst.splitlines():
            parts = line.split(None, 5)
            if len(parts) < 6 or not parts[5].lower().endswith(IMG):
                continue
            total += 1
            segs = [s for s in os.path.dirname(parts[5].replace("/", "\\")).split("\\") if s]
            if len(segs) >= 2:
                dirs[segs[-1]] += 1
            elif len(segs) == 1:
                dirs[f"<root {segs[0]}>"] += 1
            else:
                loose += 1
        out.write(f"  [{iid}] {fn[:76]}   {total} images\n")
        if loose:
            out.write(f"        <no folder> x{loose}\n")
        for k in sorted(dirs):
            out.write(f"        {k}  x{dirs[k]}\n")
    done.add(sid)
    state["done"] = sorted(done)
    json.dump(state, open(STATE, "w"), indent=1)
    out.flush()
    print(f"{n}/{len(todo)}  S{sid} {name[:40]} ({len(rows)} editions)", flush=True)
out.close()
print(f"\n{{ processed: {len(todo)}, doneTotal: {len(done)}, remaining: "
      f"{len([s for s in shelves if s not in done])} }}")
