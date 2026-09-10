"""Stack one page from each of several books into a single contact sheet, so one look settles many.

PLAN.md 14.9: a full page at readable resolution costs four times as much as a cropped band and says no
more. So: pull ONE page index out of each item, crop the band the contents/indicia sits in, scale every
panel to the same width, label it with its item id, and tile.

    python contact_sheet.py --items=1,2,3 --page=4 --crop=0,0.45,1,1 --cols=2 --out=x.jpg

--page may be a comma list, in which case every listed page of every item is tiled (item-major order).
--crop is left,top,right,bottom as fractions of the page. Read-only: it never writes near the library.
"""
import io
import os
import subprocess
import sqlite3
import sys
import tempfile
import zipfile

from PIL import Image, ImageDraw, ImageFont

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif")

items, pages, crop, cols, out, width = [], [4], (0.0, 0.0, 1.0, 1.0), 2, "sheet.jpg", 940
for a in sys.argv[1:]:
    k, _, v = a.partition("=")
    if k == "--items":
        items = [int(x) for x in v.split(",")]
    elif k == "--page" or k == "--pages":
        pages = [int(x) for x in v.split(",")]
    elif k == "--crop":
        crop = tuple(float(x) for x in v.split(","))
    elif k == "--cols":
        cols = int(v)
    elif k == "--width":
        width = int(v)
    elif k == "--out":
        out = v

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
try:
    FONT = ImageFont.truetype("arialbd.ttf", 30)
except OSError:
    FONT = ImageFont.load_default()


def page_bytes(path, idxs):
    """The bytes of the requested page indexes, by position in the archive's sorted image entries."""
    got = {}
    if path.lower().endswith((".cbz", ".zip")):
        try:
            with zipfile.ZipFile(path) as z:
                names = sorted(n for n in z.namelist() if n.lower().endswith(IMG))
                for i in idxs:
                    j = i if i >= 0 else len(names) + i
                    if 0 <= j < len(names):
                        got[i] = z.read(names[j])
        except (OSError, zipfile.BadZipFile):
            return {}
    else:
        with tempfile.TemporaryDirectory() as tmp:
            subprocess.run([SEVENZIP, "x", path, f"-o{tmp}", "-y"], capture_output=True, timeout=900)
            found = []
            for root, _, files in os.walk(tmp):
                for f in files:
                    if f.lower().endswith(IMG):
                        found.append(os.path.join(root, f))
            found.sort()
            for i in idxs:
                j = i if i >= 0 else len(found) + i
                if 0 <= j < len(found):
                    got[i] = open(found[j], "rb").read()
    return got


panels = []
for iid in items:
    row = con.execute("SELECT Path, FileName FROM Item WHERE Id = ?", (iid,)).fetchone()
    if not row:
        print(f"[{iid}] not in the catalog", flush=True)
        continue
    path, fn = row
    got = page_bytes(path, pages)
    for i in pages:
        if i not in got:
            print(f"[{iid}] p{i} missing", flush=True)
            continue
        try:
            im = Image.open(io.BytesIO(got[i])).convert("RGB")
        except Exception as e:                                  # noqa: BLE001 - a bad page is not fatal
            print(f"[{iid}] p{i} unreadable ({e})", flush=True)
            continue
        w, h = im.size
        im = im.crop((int(crop[0] * w), int(crop[1] * h), int(crop[2] * w), int(crop[3] * h)))
        im = im.resize((width, max(1, int(im.height * width / im.width))), Image.LANCZOS)
        bar = Image.new("RGB", (width, 38), (255, 235, 120))
        ImageDraw.Draw(bar).text((6, 3), f"{iid}  p{i}  {fn[:52]}", fill=(0, 0, 0), font=FONT)
        panel = Image.new("RGB", (width, im.height + 38), (255, 255, 255))
        panel.paste(bar, (0, 0))
        panel.paste(im, (0, 38))
        panels.append(panel)
        print(f"[{iid}] p{i} {fn[:60]}", flush=True)

if not panels:
    raise SystemExit("nothing to tile")
rows = (len(panels) + cols - 1) // cols
ph = max(p.height for p in panels)
sheet = Image.new("RGB", (cols * width + (cols - 1) * 8, rows * ph + (rows - 1) * 8), (90, 90, 90))
for n, p in enumerate(panels):
    sheet.paste(p, ((n % cols) * (width + 8), (n // cols) * (ph + 8)))
sheet.save(out, quality=90)
print(f"-> {out}  {sheet.size[0]}x{sheet.size[1]}  {len(panels)} panels")
