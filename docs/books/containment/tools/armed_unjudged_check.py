"""Collections carrying a LIVE, UNJUDGED provider claim that currently hold nothing.

Every other check in this directory asks about containers THAT HOLD FILES. This asks about the ones that
hold none — because a claim that matches nothing today is not safe, it is ARMED. It fires the moment a
matching file appears, and files move constantly: a folder fold re-keys a run, an issue number is
corrected, an import lands. This pass alone changed 1,000 issue numbers.

Finding this was the fourth time in one session that a check read zero while a population sat unexamined,
each time because the check was defined over the wrong set (PLAN.md §14.13). Hence this one.

  3,565 collections carry a provider span and no curated decision, holding 0 files.
  3,309 of them sit on a shelf with NO numbered issue file at all — they cannot fire, ever.
    256 sit on a shelf that DOES hold numbered issues — live exposure, and the actionable set.

`python armed_unjudged_check.py [--verbose]`   Prints the exposed set; writes nothing.
"""
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
VERBOSE = "--verbose" in sys.argv
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

rows = list(con.execute("""
 SELECT i.Id, i.SeriesId, coalesce(i.PageCount,0), i.FileName
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 LEFT JOIN CollectionNode n ON n.ItemId = i.Id AND n.TrackRole = 1
 WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
   AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan s WHERE s.ItemId = i.Id AND s.Source = 3)
   AND EXISTS (SELECT 1 FROM CollectedEditionSpan s2
               WHERE s2.ItemId = i.Id AND s2.Source <> 3 AND s2.IssueStart IS NOT NULL)
   AND coalesce(n.ContainsCount, 0) = 0"""))

inert, exposed = 0, []
for iid, sid, pages, fn in rows:
    peers = con.execute("""SELECT count(*) FROM Item x JOIN ComicDetail c2 ON c2.ItemId = x.Id
        WHERE x.SeriesId = ? AND c2.IsCollection = 0 AND c2.IssueNo IS NOT NULL
          AND coalesce(x.PageCount,0) >= 8""", (sid,)).fetchone()[0]
    if peers == 0:
        inert += 1
        continue
    spans = {s: (a, b) for s, a, b in con.execute(
        "SELECT Source, IssueStart, IssueEnd FROM CollectedEditionSpan WHERE ItemId = ? AND IssueStart IS NOT NULL", (iid,))}
    exposed.append((iid, sid, pages, peers, spans, fn))

# THE MIRROR POPULATION. The same danger exists on the other side of the flag: a file marked as a single
# ISSUE that carries a collected-edition claim. PLAN.md 14.12 records that flipping such a file's flag ARMS
# that span - `Guardians of the Galaxy: An Awesome Mix` reads #1-181 against a 139-file shelf, and the
# `2000AD #61-85 Cursed Earth` rips read #1-368 on a 1,754-file Judge Dredd shelf. 135 were either already
# sitting in the tree as containers or claimed three or more issues; those were retracted 2026-09-09.
mirror = list(con.execute("""
 SELECT i.Id, i.SeriesId, coalesce(i.PageCount,0), i.FileName
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 0 AND coalesce(i.PageCount,0) >= 8
   AND EXISTS (SELECT 1 FROM CollectedEditionSpan s
               WHERE s.ItemId = i.Id AND s.Source <> 3 AND s.IssueStart IS NOT NULL)
   AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan t WHERE t.ItemId = i.Id AND t.Source = 3)"""))
dangerous = []
for iid, sid, pages, fn in mirror:
    node = con.execute("SELECT TrackRole FROM CollectionNode WHERE ItemId = ?", (iid,)).fetchone()
    lo, hi = con.execute("""SELECT min(IssueStart), max(IssueEnd) FROM CollectedEditionSpan
        WHERE ItemId = ? AND Source <> 3 AND IssueStart IS NOT NULL""", (iid,)).fetchone()
    if (node and node[0] == 1) or (lo is not None and hi - lo >= 2):
        dangerous.append((iid, sid, pages, int(lo), int(hi), fn))

print(f"{len(rows)} collection(s) armed with an unjudged provider claim and holding nothing")
print(f"   {inert:>5}  on a shelf with no numbered issue file — cannot fire")
print(f"   {len(exposed):>5}  on a shelf that holds numbered issues — LIVE EXPOSURE")
if VERBOSE:
    for iid, sid, pages, peers, spans, fn in sorted(exposed, key=lambda r: -r[3]):
        print(f"   {iid:<8} S{sid:<8} {pages:>4}pp  shelf has {peers:>4} issues  {spans}  {fn[:52]}")
print()
print(f"{len(mirror)} issue file(s) carrying an unjudged collected-edition claim (the mirror population)")
print(f"   {len(dangerous):>5}  already a container, or claiming 3+ issues — DANGEROUS if the flag ever flips")
if VERBOSE:
    for iid, sid, pages, lo, hi, fn in sorted(dangerous, key=lambda r: -(r[4] - r[3]))[:20]:
        print(f"   {iid:<8} S{sid:<8} {pages:>4}pp  #{lo}-{hi}  {fn[:52]}")

sys.exit(1 if (exposed or dangerous) else 0)
