"""Print the archive's own entry names for one or more items — the rip's page naming, verbatim.

    python entries.py <itemId> [<itemId> ...] [--head=12] [--all]

Read-only: it lists the archive and prints. The entry names are the strongest chapter evidence a manga
rip carries (PLAN.md §14), so look at them before extracting a single page.
"""
import re
import subprocess
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

head, show_all, items = 12, False, []
for a in sys.argv[1:]:
    if a.startswith("--head="):
        head = int(a.split("=", 1)[1])
    elif a == "--all":
        show_all = True
    else:
        items.append(int(a))

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
for iid in items:
    row = con.execute("SELECT Path, FileName, PageCount FROM Item WHERE Id = ?", (iid,)).fetchone()
    if not row:
        print(f"[{iid}] not in the catalog")
        continue
    path, fn, pc = row
    out = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                         timeout=300, errors="ignore").stdout
    names = []
    for line in out.splitlines():
        parts = line.split(None, 5)
        if len(parts) >= 6 and parts[5].lower().endswith(IMG):
            names.append(parts[5])
    names.sort()
    print(f"\n[{iid}] {fn}   {len(names)} images (catalog says {pc}pp)")
    show = names if show_all else (names[:head] + (["   ..."] if len(names) > head * 2 else [])
                                   + names[-head:] if len(names) > head else names)
    for n in show:
        print(f"   {n}")
