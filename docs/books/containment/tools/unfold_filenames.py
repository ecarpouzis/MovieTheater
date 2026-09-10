"""Undo the folds that matched a FILE NAME instead of a folder.

THE FAULT. `Item.Path` ends in the file name, so the last path segment is a file — and a manga volume file
is named exactly like a run folder: `One Piece v001 (2003) (Digital) (AnHeroGold-Empire).cbz` carries a
volume marker and a year just as `04 Nightwing v4.1 (2016)` does. fold_by_folder fell back to the first
segment when no segment qualified, handed that file name to run_of, and made one Series per VOLUME — 103
of them for One Piece, 73 for Naruto, 67 for Bleach, 49 for Initial D.

THE REPAIR. For every comic file, recompute what the FIXED rule says its run folder is (searching only
segments above the file). Where the fixed rule finds no run folder at all, the file should never have been
folded, and its key is restored to whatever it held immediately before — read from series-split-undo.csv,
which records the previous key of every row this pass rewrote.

Only files the fold actually moved are touched: an item with no undo row naming its current key is left
alone, because nothing here knows what it was.

Dry-run by default. `python unfold_filenames.py [--apply] [--write out.jsonl]`
"""
import collections
import csv
import json
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fold_by_folder as F  # noqa: E402

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEP = chr(92)
UNDO = r"F:\Work\MovieTheater\docs\books\containment\reports\series-split-undo.csv"
APPLY = "--apply" in sys.argv
out_path = None
for a in sys.argv[1:]:
    if a.startswith("--write"):
        out_path = a.split("=", 1)[1] if "=" in a else "unfold.jsonl"

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""SELECT i.Id, i.Path, cd.ParsedSeriesKey, cd.IsCollection
                      FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                      WHERE i.Path LIKE '%5 - Comics%' AND coalesce(i.IsExcluded,0) = 0""").fetchall()

# what the FIXED rule says, per title folder: which candidate segments are real run folders
stats = collections.defaultdict(lambda: [0, 0])
title_of = {}
for iid, path, key, iscol in rows:
    parts = (path or "").split("5 - Comics" + SEP, 1)[-1].split(SEP)
    if len(parts) < 3:
        continue
    title_of[iid] = parts[1]
    cand = next((x for x in parts[2:-1] if F.run_of(x)), None)
    if cand:
        e = stats[(parts[1], cand)]
        e[0] += 1
        e[1] += 0 if iscol else 1
real = {k for k, (n, iss) in stats.items() if iss >= 1 or n >= 3}

# the key each item held immediately before the pass that gave it its current one
prev_for = {}
if os.path.exists(UNDO):
    with open(UNDO, encoding="utf-8") as fh:
        for row in csv.DictReader(fh):
            try:
                prev_for[int(row["ItemId"])] = (row["PreviousParsedSeriesKey"], row["NewParsedSeriesKey"])
            except (KeyError, ValueError):
                continue

restore, no_history = [], 0
for iid, path, key, iscol in rows:
    parts = (path or "").split("5 - Comics" + SEP, 1)[-1].split(SEP)
    if len(parts) < 3:
        continue
    fixed = next((x for x in parts[2:-1] if F.run_of(x) and (parts[1], x) in real), None)
    if fixed is not None:
        continue                                   # the fold was right about this file
    hist = prev_for.get(iid)
    if not hist:
        continue                                   # never folded
    was, now = hist
    if (key or "") != now or not was:
        continue                                   # its key is not the one the fold wrote
    restore.append({"itemId": iid, "key": was})

by_key = collections.Counter(r["key"] for r in restore)
print(f"{len(restore)} file(s) were keyed off a FILE NAME and are restored to the key they had before")
for k, n in by_key.most_common(12):
    print(f"   {n:>4}  -> {k[:60]}")
if len(by_key) > 12:
    print(f"   ... and {len(by_key) - 12} more keys")

if out_path and restore:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), out_path), "w", encoding="utf-8") as fh:
        for r in restore:
            fh.write(json.dumps(r, ensure_ascii=False) + chr(10))
    print(f"\n{len(restore)} decision lines -> {out_path}  (apply with books-series-split)")
if not APPLY:
    print("\n(this tool only WRITES the mapping; books-series-split applies it)")
