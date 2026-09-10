"""What chapters does this volume actually contain? Ask the file.

Most digital manga rips name every page with the chapter it belongs to:

    Fairy Tail - 100 Years Quest - c001 (v01) - p002 [ToC] [dig] [Kodansha Comics] [LuCaZ] {HQ}.jpg

So a volume states its own contents, exactly, in the archive's own table of entries — no provider, no
inference, no OCR. This is the manga answer to PLAN.md §4.2: a Viz/Kodansha "Vol. 3" is not chapter 3,
and where the chapter range cannot be determined the rule is to skip. It can be determined here.

`python chapters_from_archive.py <seriesId> [--apply-check]` — lists what each collected edition in the
series holds. Read-only: it opens archives and prints, and writes nothing anywhere.
"""
import collections
import re
import subprocess
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"

# c001, c001x1, c1.5, ch.012 — the chapter token digital rippers put on every page
RX_CHAP = re.compile(r"(?:^|[^a-z0-9])c(?:h(?:apter)?)?\.?\s?(\d{1,4})(?:\.(\d+))?(?:x\d+)?(?![0-9])", re.I)


def chapters(path):
    """The distinct chapter numbers named by the entries of one archive, in order."""
    try:
        out = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                             timeout=180, errors="ignore").stdout
    except (OSError, subprocess.SubprocessError):
        return None
    found, pages = set(), 0
    for line in out.splitlines():
        # 7z -ba columns: date time attr size compressed name
        parts = line.split(None, 5)
        if len(parts) < 6:
            continue
        name = parts[5]
        if not name.lower().endswith((".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")):
            continue
        pages += 1
        m = RX_CHAP.search(name)
        if m:
            found.add(float(f"{m.group(1)}.{m.group(2)}") if m.group(2) else float(m.group(1)))
    return sorted(found), pages


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
sid = int(sys.argv[1])
rows = con.execute("""
    SELECT i.Id, i.Path, i.FileName, i.PageCount, cd.IsCollection
    FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
    WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0 AND cd.IsCollection = 1
    ORDER BY i.FileName""", (sid,)).fetchall()

print(f"{len(rows)} collected editions in S{sid}\n")
ok = miss = 0
for iid, path, fn, pc, iscol in rows:
    res = chapters(path)
    if res is None:
        print(f"  [{iid:>6}] {fn[:64]:<64} UNREADABLE")
        miss += 1
        continue
    ch, pages = res
    if not ch:
        print(f"  [{iid:>6}] {fn[:64]:<64} no chapter token in {pages} pages")
        miss += 1
        continue
    lo, hi = ch[0], ch[-1]
    gaps = [n for n in range(int(lo), int(hi) + 1) if float(n) not in ch]
    tag = f"c{lo:g}-{hi:g}" + (f"  GAPS {gaps}" if gaps else "")
    print(f"  [{iid:>6}] {fn[:64]:<64} {len(ch):>3} chapters  {tag}")
    ok += 1
print(f"\n{ok} volumes state their own chapters; {miss} do not")
