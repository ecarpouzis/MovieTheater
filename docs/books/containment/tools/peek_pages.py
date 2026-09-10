"""Pull a few pages out of a comic and downscale them so they can be LOOKED at.

`python peek_pages.py <itemId> <outDir> [--pages 0,1,2,3] [--last 3]`

PLAN.md §10.3: there is no OCR on this box and none is needed — extract a page and read it. Handles .cbz
(zip) and .cbr (rar, via 7-Zip) and writes <outDir>/<itemId>-pNNN.jpg at a size that is cheap to read.
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

item = int(sys.argv[1])
outdir = sys.argv[2]
pages, last = [0, 1, 2, 3], 0
for a in sys.argv[3:]:
    if a.startswith("--pages="):
        pages = [int(x) for x in a.split("=", 1)[1].split(",")]
    elif a.startswith("--last="):
        last = int(a.split("=", 1)[1])

os.makedirs(outdir, exist_ok=True)
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
path, fn = con.execute("SELECT Path, FileName FROM Item WHERE Id = ?", (item,)).fetchone()
print(f"[{item}] {fn}")


def save(name, data, idx):
    try:
        im = Image.open(io.BytesIO(data))
    except Exception as e:                                   # noqa: BLE001 - a bad page is not fatal
        print(f"   p{idx}: unreadable ({e})")
        return
    im.thumbnail((1500, 1500))
    dst = os.path.join(outdir, f"{item}-p{idx:03d}.jpg")
    im.convert("RGB").save(dst, quality=88)
    print(f"   p{idx:<4} {name[-58:]:<58} -> {dst}")


if path.lower().endswith((".cbz", ".zip")):
    with zipfile.ZipFile(path) as z:
        names = sorted(n for n in z.namelist() if n.lower().endswith(IMG))
        want = [(i, names[i]) for i in pages if i < len(names)]
        want += [(len(names) - k, names[len(names) - k]) for k in range(1, last + 1) if len(names) - k >= 0]
        for i, n in want:
            save(n, z.read(n), i)
else:
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([SEVENZIP, "x", path, f"-o{tmp}", "-y"], capture_output=True, timeout=600)
        found = []
        for root, _, files in os.walk(tmp):
            for f in files:
                if f.lower().endswith(IMG):
                    found.append(os.path.join(root, f))
        found.sort()
        want = [(i, found[i]) for i in pages if i < len(found)]
        want += [(len(found) - k, found[len(found) - k]) for k in range(1, last + 1) if len(found) - k >= 0]
        for i, n in want:
            with open(n, "rb") as fh:
                save(os.path.basename(n), fh.read(), i)
