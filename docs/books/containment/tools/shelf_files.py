"""Print, for each named shelf, every collected edition's item id, page count, size and full folder.

The plain facts a judgement starts from, for many shelves at once. Read-only.

    python shelf_files.py 1287 2918 ...            [--all]  include the loose issues too
"""
import collections
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
PREFIX = "\\\\Library\\Public\\5 - Comics\\"

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

want_all = "--all" in sys.argv
sids = [int(a) for a in sys.argv[1:] if not a.startswith("--")]
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
for sid in sids:
    name = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,)).fetchone()
    rows = con.execute("""
        SELECT i.Id, i.Path, i.FileName, i.PageCount, i.FileSize, cd.IsCollection, cd.IssueNo, cd.FormatRaw
        FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0
        ORDER BY i.FileName""", (sid,)).fetchall()
    cols = [r for r in rows if r[5]]
    print(f"\n== S{sid}  {name[0] if name else '?'}   {len(cols)} collected / {len(rows)} files")
    folders = sorted({os.path.dirname(r[1] or "") for r in rows})
    for f in folders:
        print(f"   dir: {f[len(PREFIX):] if f.startswith(PREFIX) else f}")
    for iid, path, fn, pc, fs, iscol, ino, fmt in (rows if want_all else cols):
        print(f"   [{iid:>7}] {'COL' if iscol else '   '} {str(pc):>4}pp {round((fs or 0)/1048576,1):>6}MB "
              f"#{str(ino):>6} {str(fmt)[:10]:<10} {fn[:80]}")
