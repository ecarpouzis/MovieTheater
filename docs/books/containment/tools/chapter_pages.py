"""How many pages of chapter N does this book hold, and what are they called?

For the one-chapter boundary overlaps: two adjacent volumes both name chapter N on some page, and the
question is which of them actually CONTAINS it. A volume holding twenty pages of it contains it; one
holding a single page is carrying a recap, a title card or a stray. Read-only.

    python chapter_pages.py <itemId> <chapter> [<itemId> <chapter> ...]
"""
import re
import subprocess
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")
RX_CHAP = re.compile(r"(?:^|[^a-z0-9])c(?:h(?:apter)?)?\.?\s?(\d{1,4})(?:\.(\d+))?(x\d+)?(?![0-9a-z])", re.I)

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
args = sys.argv[1:]
for i in range(0, len(args), 2):
    iid, want = int(args[i]), float(args[i + 1])
    path, fn = con.execute("SELECT Path, FileName FROM Item WHERE Id = ?", (iid,)).fetchone()
    out = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                         timeout=300, errors="ignore").stdout
    hits, total = [], 0
    for line in out.splitlines():
        parts = line.split(None, 5)
        if len(parts) < 6 or not parts[5].lower().endswith(IMG):
            continue
        total += 1
        m = RX_CHAP.search(parts[5])
        if m and not m.group(3):
            n = float(f"{m.group(1)}.{m.group(2)}") if m.group(2) else float(m.group(1))
            if n == want:
                hits.append(parts[5])
    print(f"[{iid}] {fn[:62]:<62} c{want:g}: {len(hits)} of {total} pages")
    for h in hits[:4]:
        print(f"      {h[-88:]}")
    if len(hits) > 4:
        print(f"      ... and {len(hits) - 4} more, last: {hits[-1][-88:]}")
