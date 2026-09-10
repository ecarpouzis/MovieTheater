"""Contact sheet, but it only pulls the pages it needs out of the archive.

`contact_sheet.py` extracts a whole .cbr to a temp dir to reach page 4 of it. Over the share that is
200 MB a book, and a shelf of thirty books is six gigabytes moved to look at thirty contents pages.
This lists the archive first and extracts only the named entries, so a page costs a page.

    python sheet2.py --items=1,2,3 --pages=3,4 --cols=2 --out=x.jpg [--crop=0,0,1,1] [--width=900]

`--pages` are indexes into the archive's sorted image entries; negative counts from the end. Read-only.
"""
import io
import os
import re
import subprocess
import sqlite3
import sys
import tempfile

from PIL import Image, ImageDraw, ImageFont

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

items, pages, crop, cols, out, width = [], [3], (0.0, 0.0, 1.0, 1.0), 2, "sheet.jpg", 900
for a in sys.argv[1:]:
    k, _, v = a.partition("=")
    if k == "--items":
        items = [int(x) for x in v.split(",")]
    elif k in ("--pages", "--page"):
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
    FONT = ImageFont.truetype("arialbd.ttf", 26)
except OSError:
    FONT = ImageFont.load_default()


def names_of(path):
    lst = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                         timeout=300, errors="ignore").stdout
    got = []
    for line in lst.splitlines():
        parts = line.split(None, 5)
        if len(parts) >= 6 and parts[5].lower().endswith(IMG):
            got.append(parts[5])
    return sorted(got)


panels = []
for iid in items:
    row = con.execute("SELECT Path, FileName FROM Item WHERE Id = ?", (iid,)).fetchone()
    if not row:
        print(f"[{iid}] not in the catalog", flush=True)
        continue
    path, fn = row
    names = names_of(path)
    if not names:
        print(f"[{iid}] no image entries", flush=True)
        continue
    want = []
    for p in pages:
        j = p if p >= 0 else len(names) + p
        if 0 <= j < len(names):
            want.append((p, names[j]))
    with tempfile.TemporaryDirectory() as tmp:
        args = [SEVENZIP, "e", path, f"-o{tmp}", "-y"] + [n for _, n in want]
        subprocess.run(args, capture_output=True, timeout=900)
        for p, n in want:
            f = os.path.join(tmp, os.path.basename(n.replace("/", "\\")))
            if not os.path.exists(f):
                print(f"[{iid}] p{p} could not be extracted ({n})", flush=True)
                continue
            try:
                im = Image.open(f).convert("RGB")
            except Exception as e:                              # noqa: BLE001 - a bad page is not fatal
                print(f"[{iid}] p{p} unreadable ({e})", flush=True)
                continue
            w, h = im.size
            im = im.crop((int(crop[0] * w), int(crop[1] * h), int(crop[2] * w), int(crop[3] * h)))
            im = im.resize((width, max(1, int(im.height * width / im.width))), Image.LANCZOS)
            bar = Image.new("RGB", (width, 32), (255, 235, 120))
            ImageDraw.Draw(bar).text((5, 3), f"{iid} p{p}  {fn[:56]}", fill=(0, 0, 0), font=FONT)
            panel = Image.new("RGB", (width, im.height + 32), (255, 255, 255))
            panel.paste(bar, (0, 0))
            panel.paste(im, (0, 32))
            panels.append(panel)
            print(f"[{iid}] p{p} {os.path.basename(n)[:60]}", flush=True)

if not panels:
    raise SystemExit("nothing to tile")
rows_n = (len(panels) + cols - 1) // cols
ph = max(p.height for p in panels)
sheet = Image.new("RGB", (cols * width + (cols - 1) * 6, rows_n * ph + (rows_n - 1) * 6), (80, 80, 80))
for n, p in enumerate(panels):
    sheet.paste(p, ((n % cols) * (width + 6), (n // cols) * (ph + 6)))
sheet.save(out, quality=88)
print(f"-> {out}  {sheet.size[0]}x{sheet.size[1]}  {len(panels)} panels")
