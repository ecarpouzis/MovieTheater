"""Read one shelf the way a person would: folders, filenames, page counts, sizes, and what collides.

`python read_shelf.py <seriesId> [--dupes] [--limit N]`

No provider, no reduction. Every file printed with the name it has on disk, grouped by the folder it
lives in, because in this library the folder is the strongest statement anyone has made about what a
file IS. `--dupes` narrows to the issue numbers that appear more than once, which is what the numbering
census flags and what has to be judged.
"""
import collections
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
RX_NUM = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")
PREFIX = "\\\\Library\\Public\\5 - Comics\\"


def num(s):
    m = RX_NUM.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


args = [a for a in sys.argv[1:] if not a.startswith("--")]
flags = {a for a in sys.argv[1:] if a.startswith("--")}
limit = 400
for f in flags:
    if f.startswith("--limit"):
        limit = int(f.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
for sid in (int(a) for a in args):
    name = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,)).fetchone()
    rows = con.execute("""
        SELECT i.Id, i.Path, i.FileName, i.PageCount, i.FileSize, cd.IsCollection, cd.IssueNo,
               cd.Year, cd.FormatRaw, cd.IssueSource
        FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0
        ORDER BY i.Path, i.FileName""", (sid,)).fetchall()

    counts = collections.Counter(num(r[6]) for r in rows if not r[5] and num(r[6]) is not None)
    dupe_nums = {k for k, v in counts.items() if v > 1}
    show = rows
    if "--dupes" in flags:
        show = [r for r in rows if not r[5] and num(r[6]) in dupe_nums]

    print(f"\n{'=' * 112}")
    print(f"== S{sid}  {name[0] if name else '?'}   {len(rows)} files, "
          f"{sum(1 for r in rows if r[5])} collected editions, "
          f"{len(dupe_nums)} issue numbers used more than once")
    by_folder = collections.defaultdict(list)
    for r in show:
        by_folder[os.path.dirname(r[1] or "")].append(r)

    printed = 0
    for folder in sorted(by_folder, key=lambda f: -len(by_folder[f])):
        short = folder[len(PREFIX):] if folder.startswith(PREFIX) else folder
        print(f"\n  [{len(by_folder[folder]):>4} files] {short}")
        for iid, path, fn, pc, fs, iscol, ino, year, fmt, isrc in by_folder[folder]:
            if printed >= limit:
                print(f"      ... {len(show) - printed} more files not printed")
                break
            mark = "COL" if iscol else "   "
            print(f"      [{iid:>7}] {mark} #{str(ino):>7} {str(pc):>4}pp {round((fs or 0) / 1048576, 1):>6}MB "
                  f"src={isrc} {fn[:74]}")
            printed += 1
        if printed >= limit:
            break
