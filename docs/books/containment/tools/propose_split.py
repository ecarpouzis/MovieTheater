"""What runs does this shelf actually hold? Group it by the evidence, and emit a split decision.

`python propose_split.py <seriesId> [--write out.jsonl]`

The library states the answer twice. Every file lives in a folder someone chose, and every file carries a
title the ripper wrote; on a conflated shelf the two agree and both name the run. Fairy Tail (S6398) is ten
works under one id, and its folders say so — `Manga\\Fairy Tail (2006)`, `...\\Fairy Tail - 100 Years Quest
(2018)`, `...\\Fairy Tail - Blue Mistral (2015)` — while the filenames say the same thing again.

So: group by the title prefix the filenames carry, show the folders each group lives in and the numbers it
uses, and let a person read it before anything is written. This PROPOSES. `books-series-split` applies, and
only from a file someone has looked at.
"""
import collections
import json
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
PREFIX = "\\\\Library\\Public\\5 - Comics\\"
RX_EXT = re.compile(r"\.(cbz|cbr|cb7|cbt|pdf|epub)$", re.I)
# where the title stops and the numbering starts: "... v01", "... 016 -", "... Vol. 3", "... #12"
RX_STOP = re.compile(
    r"\s+(?:"
    r"v\d{1,3}\b"                       # v01
    r"|vol(?:ume)?\.?\s*\d{1,3}\b"      # Vol. 3
    r"|book\s*\d{1,3}\b"                # Book 02
    r"|#\s*\d+"                         # #12
    r"|\d{1,4}(?:\.\d+)?\s*(?:-|\(|$)"  # 016 - , 545.5
    r")", re.I)

# Two different axes, because conflation has two different shapes.
#   BY TITLE  — the runs are different WORKS that share a Series id. Fairy Tail vs Fairy Tail - Blue
#               Mistral vs Fairy Tail Zero: the filenames say so outright.
#   BY FOLDER — the runs are the SAME title relaunched, and only the folder distinguishes them.
#               `_Suicide Squad Suicide Squad v1 (1987)` and ` Suicide Squad v5 (2016)` are two
#               runs each numbering from 1, and every file in both is titled "Suicide Squad".
# Choosing the wrong axis produces one group where there should be three, so the axis is a decision.
args = [a for a in sys.argv[1:] if not a.startswith("--")]
by_folder = any(a.startswith("--by-folder") for a in sys.argv[1:])
out_path = None
for a in sys.argv[1:]:
    if a.startswith("--write"):
        out_path = a.split("=", 1)[1] if "=" in a else "split.jsonl"

sid = int(args[0])
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
name = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,)).fetchone()
rows = con.execute("""
    SELECT i.Id, i.Path, i.FileName, cd.IsCollection, cd.IssueNo, cd.ParsedSeriesKey, i.PageCount
    FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
    WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0
    ORDER BY i.FileName""", (sid,)).fetchall()


def title_of(fn):
    stem = RX_EXT.sub("", fn or "")
    m = RX_STOP.search(stem)
    t = (stem[:m.start()] if m else stem).strip(" -_,")
    # a trailing "(2011)" is the run's year, not part of its name
    return re.sub(r"\s*\((?:19|20)\d{2}\)\s*$", "", t).strip()


RX_RUN = re.compile(r"(?:^|[\/])(?P<run>[^\/]*?v(?:ol)?\.?\s*\d{1,3}[^\/]*?\((?:19|20)\d{2}\)[^\/]*)", re.I)
RX_LEAD = re.compile(r"^\s*\d{1,3}[\s._-]+")


def run_of(path):
    """The folder component that names a RUN: one carrying both a vN marker and a (YYYY)."""
    m = None
    for part in (path or "").replace("/", "\\").split("\\")[:-1]:   # folders only; the FILE name also
                                                                        # carries a (YYYY) and would win
        if re.search(r"v(?:ol)?\.?\s*\d{1,3}", part, re.I) and re.search(r"\((?:19|20)\d{2}\)", part):
            m = part
    return RX_LEAD.sub("", m).strip() if m else None


groups = collections.defaultdict(list)
for iid, path, fn, iscol, ino, key, pc in rows:
    g = (run_of(path) if by_folder else None) or title_of(fn)
    groups[g].append((iid, path, fn, iscol, ino, key, pc))

print(f"S{sid}  {name[0] if name else '?'}   {len(rows)} files, {len(groups)} title groups")
print(f"current ParsedSeriesKey(s): "
      f"{', '.join(sorted({(r[5] or '(null)') for r in rows}))}\n")

proposal = []
for title, items in sorted(groups.items(), key=lambda kv: -len(kv[1])):
    folders = collections.Counter(os.path.dirname(i[1] or "") for i in items)
    cols = sum(1 for i in items if i[3])
    nums = sorted({float(i[4]) for i in items if i[4] and re.match(r"^\d+(\.\d+)?$", str(i[4]))})
    span = f"#{nums[0]:g}-{nums[-1]:g}" if nums else "-"
    print(f"  [{len(items):>4} files, {cols:>3} collections]  {title!r}   numbers {span}")
    for f, n in folders.most_common(3):
        short = f[len(PREFIX):] if f.startswith(PREFIX) else f
        print(f"           {n:>4}  {short[:92]}")
    for iid, path, fn, iscol, ino, key, pc in items:
        proposal.append({"itemId": iid, "key": title})

if out_path:
    with open(out_path, "w", encoding="utf-8") as fh:
        for p in proposal:
            fh.write(json.dumps(p, ensure_ascii=False) + "\n")
    print(f"\n{len(proposal)} decision lines -> {out_path}")
    print("READ THE GROUPS ABOVE before applying. books-series-split takes this file.")
