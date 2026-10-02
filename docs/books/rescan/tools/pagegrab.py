"""Extract a few pages of an archive on the share (read-only) into an output dir, downsized for reading.
Pages are picked by POSITION in sorted image order (0-based; negatives count from the end).
usage: python pagegrab.py <out dir> <archive rel/abs path> <pos> [<pos> ...]
"""
import os, re, subprocess, sys, tempfile, shutil
from PIL import Image

ROOT = "\\\\Library\\Public\\5 - Comics\\"
SZ = r"C:\Program Files\7-Zip\7z.exe"
IMG = re.compile(r"\.(jpe?g|png|webp|gif|bmp)$", re.I)

out, arc, *pos = sys.argv[1:]
full = arc if arc.startswith("\\\\") else ROOT + arc
ls = subprocess.run([SZ, "l", "-slt", "-ba", full], capture_output=True, text=True, encoding="utf-8", errors="replace")
names = sorted(l[7:] for l in ls.stdout.splitlines() if l.startswith("Path = ") and IMG.search(l))
want = [names[int(p)] for p in pos if -len(names) <= int(p) < len(names)]
os.makedirs(out, exist_ok=True)
tmp = tempfile.mkdtemp()
try:
    subprocess.run([SZ, "e", "-y", f"-o{tmp}", full] + want, capture_output=True)
    for i, n in enumerate(want):
        src = os.path.join(tmp, os.path.basename(n))
        if not os.path.exists(src): print("missing", n); continue
        im = Image.open(src).convert("RGB"); im.thumbnail((1000, 1500))
        dst = os.path.join(out, f"{re.sub(r'[^A-Za-z0-9]+', '_', os.path.basename(arc))[:60]}_{pos[i]}.jpg")
        im.save(dst, quality=72); print(dst)
finally:
    shutil.rmtree(tmp, ignore_errors=True)
print("pages", len(names))
