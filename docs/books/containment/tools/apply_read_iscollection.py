"""Set or clear IsCollection from a hand-written list of item ids. Derives nothing.

`unflag_floppies.py` swept ONE direction with a rule — files marked as collections that are really single
issues. Reading the population afterwards confirmed those clears (the whole 36-59pp band is magazine-format
runs and double-sized floppies, not a trade among them) and explained where the bad flag came from: these
page-rips carry the ComicInfo of the TRADE they were cut out of, so `FormatRaw` says 'TPB' about a file
that is one issue.

The opposite direction had never been swept at all, and reading turned up real trades marked as issues:
`Knights Of The Dinner Table - Bundle Of Trouble 1`-`9` (98-100pp, each collecting about six issues),
`Grimm Fairy Tales - Tales From Wonderland Vol 1/2/3 TPB`, and three `DCP Archive Edition ... compilation`
files. A trade flagged as an issue can never nest what it holds, so its contents stay loose forever.

This tool takes ids that a person read and states what they are. There is no pattern in it.

`python apply_read_iscollection.py <list.csv> [--apply]`   CSV columns: ItemId,IsCollection
"""
import csv
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
args = [a for a in sys.argv[1:] if not a.startswith("--")]
APPLY = "--apply" in sys.argv
sheet = args[0] if args else os.path.join(HERE, "iscollection-read.csv")
if not os.path.exists(sheet):
    sheet = os.path.join(HERE, os.path.basename(sheet))

con = sqlite3.connect(HOT)
now = {r[0]: (r[1], r[2], r[3]) for r in con.execute(
    "SELECT i.Id, i.FileName, i.PageCount, cd.IsCollection FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id")}
# A SHELF line states the verdict for every file on one shelf, which is how a 142-issue magazine run is
# recorded without transcribing 142 ids. It is only legitimate when the shelf was actually read as a run.
on_shelf = {}
for iid, sid in con.execute("SELECT Id, SeriesId FROM Item WHERE SeriesId IS NOT NULL"):
    on_shelf.setdefault(sid, []).append(iid)

writes, skipped = [], 0
for r in csv.DictReader(open(sheet, encoding="utf-8")):
    want = int(r["IsCollection"])
    ids = [int(r["ItemId"])] if r.get("ItemId") else on_shelf.get(int(r["SeriesId"]), [])
    for iid in ids:
        if iid not in now:
            print(f"   item {iid} not found")
            continue
        fn, pages, was = now[iid]
        if was == want:
            skipped += 1
            continue
        writes.append((iid, fn, pages, was, want))

print(f"{len(writes)} row(s) to change, {skipped} already as stated")
for iid, fn, pages, was, want in writes:
    print(f"   {iid:<8} {str(pages):>4}pp  IsCollection {was} -> {want}   {fn[:62]}")

if not APPLY:
    print("\n(dry run - re-run with --apply)")
    raise SystemExit
con.executemany("UPDATE ComicDetail SET IsCollection = ? WHERE ItemId = ?", [(w, i) for i, _, _, _, w in writes])
con.commit()
print(f"\napplied: {len(writes)} row(s)")
