"""Read a FOLDER, whatever Series its files were filed under. `python read_folder.py "<path fragment>"`

The shelf is the folder. A Series id is a conclusion someone else drew about the folder, and on a
conflated or split run it is the wrong unit to look at — Witchfinder is one folder tree across four
Series ids, and Bone's volumes and issues turned out to sit in different ones. So look at the folder.
"""
import collections
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
PREFIX = "\\\\Library\\Public\\5 - Comics\\"

frag = sys.argv[1]
limit = int(sys.argv[2]) if len(sys.argv) > 2 else 400

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
    SELECT i.Id, i.Path, i.FileName, i.PageCount, i.FileSize, cd.IsCollection, cd.IssueNo,
           i.SeriesId, coalesce(s.DisplayNameOverride, s.Name), cd.FormatRaw
    FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
    LEFT JOIN Series s ON s.Id = i.SeriesId
    WHERE i.Path LIKE ? AND coalesce(i.IsExcluded, 0) = 0
    ORDER BY i.Path, i.FileName""", (f"%{frag}%",)).fetchall()

by_folder = collections.defaultdict(list)
for r in rows:
    by_folder[os.path.dirname(r[1] or "")].append(r)

series = collections.Counter((r[7], r[8]) for r in rows)
print(f"{len(rows)} files, {len(by_folder)} folders, {len(series)} Series ids")
for (sid, name), n in series.most_common():
    print(f"    S{str(sid):<8} {n:>5} files   {name}")

printed = 0
for folder in sorted(by_folder):
    short = folder[len(PREFIX):] if folder.startswith(PREFIX) else folder
    print(f"\n  [{len(by_folder[folder]):>4}] {short}")
    for iid, path, fn, pc, fs, iscol, ino, sid, name, fmt in by_folder[folder]:
        if printed >= limit:
            print(f"      ... {len(rows) - printed} more not printed")
            break
        print(f"      [{iid:>7}] S{str(sid):<7} {'COL' if iscol else '   '} #{str(ino):>6} "
              f"{str(pc):>4}pp {round((fs or 0) / 1048576, 1):>6}MB {fn[:70]}")
        printed += 1
    if printed >= limit:
        break
