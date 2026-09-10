"""Put a lot of comic COVERS on one page, each labelled with its item id, so they can be read.

The issue number is printed on the cover — `NIGHTWING 25`, `SUPERGIRL No. 6 APR 2006` — usually in the
top-left corner box with the logo. That is the comic saying what it is, and it is what settles a file
whose stored number is wrong and whose archive names its pages `00.jpg`.

This extracts page 0 of each file, keeps the top band where the logo and number live, stamps the item id
under it, and tiles them. One sheet then answers a dozen files at a glance.

`python cover_sheet.py --ids=<csv or comma list> [--out <dir>] [--per 12] [--band 0.5]`
  --ids   a CSV with an ItemId column, or a comma-separated list of ids
"""
import csv
import os
import subprocess
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
out_dir = os.path.join(HERE, "covers")
per, band, ids = 12, 0.5, []
for a in sys.argv[1:]:
    if a.startswith("--ids="):
        v = a.split("=", 1)[1]
        if os.path.exists(v) or os.path.exists(os.path.join(HERE, v)):
            p = v if os.path.exists(v) else os.path.join(HERE, v)
            ids = [int(r["ItemId"]) for r in csv.DictReader(open(p, encoding="utf-8"))]
        else:
            ids = [int(x) for x in v.split(",") if x.strip()]
    elif a.startswith("--out"):
        out_dir = a.split("=", 1)[1]
    elif a.startswith("--per"):
        per = int(a.split("=", 1)[1])
    elif a.startswith("--band"):
        band = float(a.split("=", 1)[1])

raw = os.path.join(out_dir, "raw")
os.makedirs(raw, exist_ok=True)
peek = os.path.join(HERE, "peek_pages.py")

got = []
for iid in ids:
    f = os.path.join(raw, f"{iid}-p000.jpg")
    if not os.path.exists(f):
        subprocess.run([sys.executable, peek, str(iid), raw, "--pages=0"], capture_output=True, timeout=300)
    if os.path.exists(f):
        got.append((iid, f))
print(f"{len(got)}/{len(ids)} cover(s) extracted")

CELL_W, COLS = 330, 5
sheets = 0
for start in range(0, len(got), per):
    chunk = got[start:start + per]
    cells = []
    for iid, f in chunk:
        im = Image.open(f).convert("RGB")
        w, h = im.size
        im = im.crop((0, 0, w, int(h * band)))
        im = im.resize((CELL_W, int(im.size[1] * CELL_W / im.size[0])), Image.LANCZOS)
        lab = Image.new("RGB", (CELL_W, im.size[1] + 20), "white")
        lab.paste(im, (0, 0))
        ImageDraw.Draw(lab).text((4, im.size[1] + 4), str(iid), fill="black")
        cells.append(lab)
    rows = [cells[i:i + COLS] for i in range(0, len(cells), COLS)]
    H = sum(max(c.size[1] for c in r) + 6 for r in rows)
    sh = Image.new("RGB", (CELL_W * COLS, H), "white")
    y = 0
    for r in rows:
        x = 0
        for c in r:
            sh.paste(c, (x, y))
            x += CELL_W
        y += max(c.size[1] for c in r) + 6
    path = os.path.join(out_dir, f"sheet{sheets:02d}.jpg")
    sh.save(path, quality=86)
    print(f"  {path}  {sh.size}  ids {chunk[0][0]}..{chunk[-1][0]}")
    sheets += 1
print(f"{sheets} sheet(s) in {out_dir}")
