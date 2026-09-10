"""Stack the evidence band of several books into ONE contact sheet (PLAN.md §14.9).

`python sheet.py <outPath> <itemId>[:<pageIdx>] ... [--crop=left|right|full|lowerleft] [--cols=2] [--cell=760]`

A page index may be negative (-1 = the last page, which is where Marvel prints its indicia).
Default page is 1 — for the digital-Marvel rip houses that is the wraparound cover whose BACK half
carries "Collecting <Series> #a-b". `--crop=left` keeps only that back half.

Read-only: it extracts to a temp dir and never touches the archive or the database.
"""
import io
import os
import re
import subprocess
import sqlite3
import sys
import tempfile
import zipfile

from PIL import Image

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif")

out = sys.argv[1]
specs, crop, cols, cell = [], "left", 2, 760
for a in sys.argv[2:]:
    if a.startswith("--crop="):
        crop = a.split("=", 1)[1]
    elif a.startswith("--cols="):
        cols = int(a.split("=", 1)[1])
    elif a.startswith("--cell="):
        cell = int(a.split("=", 1)[1])
    else:
        iid, _, p = a.partition(":")
        specs.append((int(iid), int(p) if p else 1))

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)


def page_bytes(item, idx):
    path, fn = con.execute("SELECT Path, FileName FROM Item WHERE Id = ?", (item,)).fetchone()
    if path.lower().endswith((".cbz", ".zip")):
        with zipfile.ZipFile(path) as z:
            names = sorted(n for n in z.namelist() if n.lower().endswith(IMG))
            if not names:
                return None, fn
            return z.read(names[idx if idx >= 0 else len(names) + idx]), fn
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([SEVENZIP, "x", path, f"-o{tmp}", "-y"], capture_output=True, timeout=900)
        found = []
        for root, _, files in os.walk(tmp):
            found += [os.path.join(root, f) for f in files if f.lower().endswith(IMG)]
        found.sort()
        if not found:
            return None, fn
        with open(found[idx if idx >= 0 else len(found) + idx], "rb") as fh:
            return fh.read(), fn


tiles = []
for iid, idx in specs:
    try:
        data, fn = page_bytes(iid, idx)
        im = Image.open(io.BytesIO(data)).convert("RGB")
    except Exception as e:                                       # noqa: BLE001 - a bad page is not fatal
        print(f"[{iid}] p{idx}: FAILED {e}")
        continue
    w, h = im.size
    if crop == "left" and w > h * 0.9:            # only split a genuine wraparound
        im = im.crop((0, 0, w // 2, h))
    elif crop == "right" and w > h * 0.9:
        im = im.crop((w // 2, 0, w, h))
    elif crop == "lowerleft":
        im = im.crop((0, int(h * 0.45), w // 2 if w > h * 0.9 else w, h))
    im.thumbnail((cell, cell * 3))
    tiles.append((iid, idx, fn, im))
    print(f"[{iid}] p{idx} {im.size}  {fn[:70]}")

if not tiles:
    raise SystemExit("nothing extracted")
rows = (len(tiles) + cols - 1) // cols
cw = max(t[3].width for t in tiles)
ch = max(t[3].height for t in tiles)
sheet = Image.new("RGB", (cw * cols, ch * rows), "white")
for n, (iid, idx, fn, im) in enumerate(tiles):
    sheet.paste(im, ((n % cols) * cw, (n // cols) * ch))
sheet.save(out, quality=90)
print(f"-> {out}  {sheet.size}")
