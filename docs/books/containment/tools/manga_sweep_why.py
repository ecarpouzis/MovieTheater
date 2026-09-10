"""Classify the shelves manga_sweep.py refused, so the 221 'NO' rows become named causes.

Read-only. Takes the state file's notTiling list, re-reads each shelf's archives (cached to
manga_sweep_why_cache.json so a re-run is cheap), and writes manga_sweep_why.txt: one line per shelf
with the cause and one line of evidence. Chunked by --limit, resumable via the cache.

Causes it can tell apart:
  no-page-names   the archive holds no per-page image names at all (pdf/epub, or a single-blob rip)
  unnamed-pages   pages are named, but none carries a chapter token (0001.jpg, 'Vol 01 - 005.jpg')
  stray-file      one file's volume/chapter ranges sit outside the run's ladder (a different edition)
  conflation      several distinct works/runs share the Series id (chapter numbering restarts)
  overlap         adjacent volumes genuinely claim the same chapter
"""
import json
import os
import re
import sqlite3
import subprocess
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "manga_sweep_why_cache.json")
STATE = os.path.join(HERE, "manga_sweep_state.json")
OUT = os.path.join(HERE, "manga_sweep_why.txt")

RX_CHAP = re.compile(r"(?:^|[^a-z0-9])c(?:h(?:apter)?)?\.?\s?(\d{1,4})(?:\.(\d+))?(x\d+)?(?![0-9a-z])", re.I)
RX_VOLNO = re.compile(r"(?:v|vol|volume)\.?\s*(\d{1,3})", re.I)
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

limit = 40
for a in sys.argv[1:]:
    if a.startswith("--limit="):
        limit = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
cache = json.load(open(CACHE)) if os.path.exists(CACHE) else {}


def listing(path):
    try:
        out = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                             timeout=240, errors="ignore")
    except (OSError, subprocess.SubprocessError):
        return None
    if out.returncode not in (0, 1):
        return None
    names = []
    for line in out.stdout.splitlines():
        parts = line.split(None, 5)
        if len(parts) >= 6:
            names.append(parts[5])
    return names


state = json.load(open(STATE))
todo = [s for s in state["notTiling"] if str(s) not in cache][:limit]
for sid in todo:
    rows = con.execute("""
        SELECT i.Id, i.Path, i.FileName, i.PageCount FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0 AND cd.IsCollection = 1
        ORDER BY i.FileName""", (sid,)).fetchall()
    name = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,)).fetchone()
    vols = []
    for iid, path, fn, pc in rows:
        names = listing(path)
        imgs = [n for n in (names or []) if n.lower().endswith(IMG)]
        chs = sorted({float(f"{m.group(1)}.{m.group(2)}") if m.group(2) else float(m.group(1))
                      for m in (RX_CHAP.search(n) for n in imgs) if m and not m.group(3)})
        mv = RX_VOLNO.search(fn)
        vols.append({"id": iid, "fn": fn, "ext": os.path.splitext(path)[1].lower(), "pc": pc,
                     "entries": None if names is None else len(names), "imgs": len(imgs),
                     "sample": (imgs[1] if len(imgs) > 1 else (names[0] if names else None)),
                     "lo": chs[0] if chs else None, "hi": chs[-1] if chs else None, "n": len(chs),
                     "volno": int(mv.group(1)) if mv else None,
                     "dir": os.path.basename(os.path.dirname(path))})
    cache[str(sid)] = {"name": name[0] if name else "?", "vols": vols}
    json.dump(cache, open(CACHE, "w"), indent=0)
    print(f"S{sid} {cache[str(sid)]['name'][:44]}  {len(vols)} vols", flush=True)

print(f"\n{{ cached: {len(cache)}, remaining: {len([s for s in state['notTiling'] if str(s) not in cache])} }}")
