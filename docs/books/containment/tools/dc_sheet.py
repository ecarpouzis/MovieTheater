"""Stack the COPYRIGHT-PAGE BAND of several DC trades into one contact sheet (PLAN.md §14.8, §14.9).

`python dc_sheet.py <outPath> <itemId>[:<pageIdx>] ... [--band=0.30,0.95] [--width=880]`

DC digital trades print "Originally published in single magazine form in ..." on the copyright page —
index 3 for the Zone-Empire era rips, 3-5 for the 2021+ Son of Ultron ones — always in the LOWER half of
an otherwise black page. Cropping to that band and stacking four books makes one look settle four books;
a full page at readable resolution costs four times as much and says no more.

A page index may be negative (-1 = last page). Read-only: extracts to a temp dir, touches nothing.
"""
import io
import os
import subprocess
import sqlite3
import sys
import tempfile
import zipfile

from PIL import Image, ImageDraw

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif")

out = sys.argv[1]
specs, band, xband, width = [], (0.30, 0.95), (0.0, 1.0), 880
for a in sys.argv[2:]:
    if a.startswith("--band="):
        band = tuple(float(x) for x in a.split("=", 1)[1].split(","))
    elif a.startswith("--xband="):
        xband = tuple(float(x) for x in a.split("=", 1)[1].split(","))
    elif a.startswith("--width="):
        width = int(a.split("=", 1)[1])
    else:
        iid, _, p = a.partition(":")
        specs.append((int(iid), int(p) if p else 3))

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


panels = []
for iid, idx in specs:
    try:
        data, fn = page_bytes(iid, idx)
        im = Image.open(io.BytesIO(data)).convert("RGB")
    except Exception as e:                                   # noqa: BLE001 - a bad page is not fatal
        print(f"[{iid}] p{idx}: FAILED {e}")
        continue
    w, h = im.size
    if w > h * 0.9:                                          # a wraparound spread: keep the right half
        im = im.crop((w // 2, 0, w, h))
        w, h = im.size
    im = im.crop((int(w * xband[0]), int(h * band[0]), int(w * xband[1]), int(h * band[1])))
    im = im.resize((width, max(1, int(im.height * width / im.width))), Image.LANCZOS)
    lab = Image.new("RGB", (width, 22), (255, 255, 255))
    ImageDraw.Draw(lab).text((6, 5), f"[{iid}] p{idx}  {fn[:96]}", fill=(0, 0, 0))
    panels += [lab, im]
    print(f"[{iid}] p{idx} band {im.size}  {fn[:70]}")

if not panels:
    raise SystemExit("nothing extracted")
sheet = Image.new("RGB", (width, sum(p.height for p in panels)), "white")
y = 0
for p in panels:
    sheet.paste(p, (0, y))
    y += p.height
sheet.save(out, quality=88)
print(f"-> {out}  {sheet.size}")
