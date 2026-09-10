"""Clear IsCollection on files that are plainly single issues.

WHAT THIS FIXES. `Plastic Man v2 008 (1968).cbz` is a 36-page single issue, and the parse set
IsCollection on it. While that flag stands the file is a CONTAINER waiting for a range, and any provider
willing to guess one gets to nest the shelf under itself — S94851 was thirteen such rows and nothing else.

THE TEST, and why each half is needed:
  the filename carries a plain issue number   `<something> <NNN>` or `<something> #NN`, with no volume,
                                              omnibus, collection or edition word anywhere in it. A book
                                              that calls itself Vol. 04, an Omnibus, a Deluxe or a
                                              Complete is left alone whatever its page count.
  the file is too thin to be a collection     under 60 pages. A real trade is 100pp and up.
Both must hold. Either alone is not enough: a 40pp "Vol. 01" ashcan exists, and so does a 300-page single
issue of an anthology magazine.

The flag is only ever CLEARED, never set — this cannot turn a book into a collection, so the worst it can
do is stop something being treated as a container.

Dry-run by default. `python unflag_floppies.py [--apply]`
"""
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
APPLY = "--apply" in sys.argv
MAX_PAGES = 60

RX_COLLECTION_WORD = re.compile(
    r"(?i)\b(vol|volume|omnibus|collection|collected|complete|deluxe|epic|masterworks|essential|"
    r"treasury|compendium|anthology|archives|chronicles|library|absolute|3-in-1|2-in-1|tpb|hc)\b")
RX_PLAIN_ISSUE = re.compile(r"(?i)^(?P<stem>.+?)[ _-]+#?(?P<no>\d{1,4})(?:\.\d+)?(?:[ _].*)?$")
RX_EXT = re.compile(r"(?i)\.(cbz|cbr|cb7|pdf|epub)$")
RX_PARENS = re.compile(r"\s*\([^)]*\)\s*")

con = sqlite3.connect(HOT)
rows = con.execute("""SELECT i.Id, i.FileName, coalesce(i.PageCount, 0)
                      FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                      WHERE cd.IsCollection = 1 AND coalesce(i.IsExcluded, 0) = 0""").fetchall()

hits = []
for iid, fn, pages in rows:
    stem = RX_PARENS.sub(" ", RX_EXT.sub("", fn or "")).strip()
    if pages <= 0 or pages >= MAX_PAGES:
        continue
    if RX_COLLECTION_WORD.search(stem):
        continue
    if not RX_PLAIN_ISSUE.match(stem):
        continue
    hits.append((iid, fn, pages))

print(f"{len(rows)} rows carry IsCollection; {len(hits)} are single issues by BOTH tests")
for iid, fn, pages in hits[:20]:
    print(f"   {iid:<8} {pages:>4}pp  {fn[:76]}")
if len(hits) > 20:
    print(f"   ... and {len(hits) - 20} more")

if not APPLY:
    print("\n(dry run - re-run with --apply)")
    raise SystemExit
con.executemany("UPDATE ComicDetail SET IsCollection = 0 WHERE ItemId = ?", [(h[0],) for h in hits])
con.commit()
print(f"\napplied: IsCollection cleared on {len(hits)} row(s)")
