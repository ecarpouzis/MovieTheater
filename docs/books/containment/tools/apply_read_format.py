"""Write ComicDetail.Format from a hand-written sheet of shelves that were READ. Derives nothing.

WHY. `CollectionLevels.Resolve` calls a file a CONTAINER when its Format is Tpb / Hardcover / Omnibus,
whatever its size. Thousands of single-issue page-rips carry that Format because their ComicInfo is the
metadata of the trade they were cut out of. While it stands, those files are containers in the containment
ladder: they are never nested themselves, and the real trade on their shelf finds no issues to nest.
Green Lantern v4's `#26-38` had twenty matching files on its shelf and took none of them, because every one
had been ruled a volume.

The obvious fix is a page-count floor in the resolver. That is a RULE applied to the whole library, which
is exactly what this project keeps getting wrong, so it is not what this does. Instead each SHELF is read —
`Gold Digger v2 001` (20pp), `CRAZY Magazine v1 001` (44pp), `Star Trek TNG v1 001` (36pp) are numbered
single issues of a run, and reading the run together is what says so — and the verdict is written here by
hand, one shelf per line.

`ItemId,Format` writes one file. `Shelf,SeriesId,Format` writes every mis-formatted file on one shelf,
which is how a 79-issue run is recorded without transcribing 79 ids; the shelf line is only legitimate
when the shelf was actually read as a run.

An `ItemId` row may set ANY Format on any single-issue file, not only a trade Format on a small one: the
containment gap turned up one-shots, specials and Villains Month rips stored as plain single issues with
a bare number, and `ContainmentJob` keeps a file off the run's ladder by its reading tier — Annual /
Special / OneShot — so the Format is the one lever that says "this is not issue #1 of the run". A shelf
row keeps the narrow population above; it is a rule over a run and stays that way.

An optional `FormatRaw` column is written when a row fills it: the book modal shows the raw string before the
enum, so a magazine issue whose ComicInfo said "Single Issue" keeps saying so until the raw label changes too
(Eric 09-26: magazine runs read like a run, labelled as magazines). Empty = the raw label stays as it is.

`python apply_read_format.py <sheet.csv> [--apply]`
"""
import csv
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
args = [a for a in sys.argv[1:] if not a.startswith("--")]
APPLY = "--apply" in sys.argv
sheet = args[0] if args else os.path.join(HERE, "format-read.csv")
if not os.path.exists(sheet):
    sheet = os.path.join(HERE, os.path.basename(sheet))

con = sqlite3.connect(HOT)
# the population this sheet may touch: a single issue carrying a collection Format
POP = """SELECT i.Id, i.FileName, coalesce(i.PageCount,0), cd.Format
         FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
         WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 0
           AND cd.Format IN (1, 2, 3) AND coalesce(i.PageCount,0) BETWEEN 1 AND 59"""
pop = {r[0]: (r[1], r[2], r[3]) for r in con.execute(POP)}
# an ItemId row was read one file at a time: any single-issue file may take any Format
ANY = """SELECT i.Id, i.FileName, coalesce(i.PageCount,0), cd.Format
         FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
         WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 0"""
anyfile = {r[0]: (r[1], r[2], r[3]) for r in con.execute(ANY)}
by_shelf = {}
for iid, sid in con.execute(f"SELECT i.Id, i.SeriesId FROM Item i WHERE i.Id IN (SELECT Id FROM ({POP}))"):
    by_shelf.setdefault(sid, []).append(iid)

writes, refused = [], []
for r in csv.DictReader(open(sheet, encoding="utf-8")):
    fmt = int(r["Format"])
    raw = (r.get("FormatRaw") or "").strip() or None
    if r.get("ItemId"):
        ids, src = [int(r["ItemId"])], anyfile
    elif r.get("SeriesId"):
        ids, src = by_shelf.get(int(r["SeriesId"]), []), pop
    else:
        continue
    for iid in ids:
        if iid not in src or src[iid][2] == fmt:
            refused.append(iid)
            continue
        writes.append((iid, src[iid][0], src[iid][1], src[iid][2], fmt, raw))

print(f"{len(pop)} file(s) in the population; this sheet decides {len(writes)}")
if refused:
    print(f"   {len(refused)} id(s) outside the population, skipped")
for iid, fn, pages, was, want, raw in writes[:12]:
    print(f"   {iid:<8} {pages:>3}pp  Format {was} -> {want}{f' (raw {raw!r})' if raw else ''}   {fn[:60]}")
if len(writes) > 12:
    print(f"   ... and {len(writes) - 12} more")

if not APPLY:
    print("\n(dry run - re-run with --apply)")
    raise SystemExit
con.executemany("UPDATE ComicDetail SET Format = ?, FormatRaw = coalesce(?, FormatRaw) WHERE ItemId = ?",
                [(w, raw, i) for i, _, _, _, w, raw in writes])
con.commit()
print(f"\napplied: {len(writes)} row(s)")
