"""Read-only peek into archives on the share: page (image) count + the ComicInfo fields that say what a book
collects. Uses 7z (list + extract-to-stdout); never writes anywhere.
usage: python peek.py <relative-or-absolute path> [...]     (paths relative to the comics root)
"""
import re, subprocess, sys

ROOT = "\\\\Library\\Public\\5 - Comics\\"
SZ = r"C:\Program Files\7-Zip\7z.exe"
IMG = re.compile(r"\.(jpe?g|png|webp|gif|bmp)$", re.I)


def peek(p):
    full = p if p.startswith("\\\\") else ROOT + p
    ls = subprocess.run([SZ, "l", "-slt", "-ba", full], capture_output=True, text=True, encoding="utf-8", errors="replace")
    names = [l[7:] for l in ls.stdout.splitlines() if l.startswith("Path = ")]
    pages = sum(1 for n in names if IMG.search(n))
    ci = next((n for n in names if n.lower().endswith("comicinfo.xml")), None)
    fields = {}
    if ci:
        x = subprocess.run([SZ, "e", "-so", full, ci], capture_output=True).stdout.decode("utf-8", "replace")
        for k in ("Series", "Number", "Volume", "Title", "Year", "Summary", "Notes", "Web", "PageCount", "Format"):
            m = re.search(rf"<{k}>(.*?)</{k}>", x, re.S)
            if m: fields[k] = re.sub(r"\s+", " ", m.group(1))[:600]
    print(f"== {p}\n   pages={pages} " + " ".join(f"{k}={v!r}" for k, v in fields.items()))


for a in sys.argv[1:]:
    peek(a)
