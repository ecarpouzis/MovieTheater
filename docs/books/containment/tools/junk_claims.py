"""Provider claims that are refutable from the file itself, and what they would cost.

`python junk_claims.py --list=<file>|<sid>...`

Three refutations, in order of how little they have to assume:

  thin      the book does not have the pages. Fewer than 12 pages per claimed issue is the same floor
            `PageArithmetic.Flag` already uses; a 100-page book cannot hold 62 issues.
  mislink   LOCG's own page for the record this span came from says it is a single "Comic" of ~28 pages,
            not an edition — so the span describes a different object (this is the mechanism behind the
            §6.2 "Vol. 07 -> #7-7" shape: the trade was matched to ISSUE #7).
  ordinal   the claim is exactly #N-N where N is the volume ordinal on the filename (§6.2).

For each, the issue FILES of >=10pp the claim covers — the ones a de-duplication would call redundant.
"""
import collections
import json
import os
import re
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prose import page  # noqa: E402

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
SRC = {0: "LOCG", 1: "GCD", 2: "CV"}
RXN = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")


def num(s):
    m = RXN.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


ids, out_path = [], None
for a in sys.argv[1:]:
    if a.startswith("--write="):
        out_path = a.split("=", 1)[1]
for a in sys.argv[1:]:
    if a.startswith("--list="):
        ids += [int(x) for x in open(os.path.join(HERE, a.split("=", 1)[1])).read().split()]
    elif not a.startswith("--"):
        ids.append(int(a))

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
tot = collections.Counter()
retract = {}
curated = {r[0] for r in con.execute(
    "SELECT ItemId FROM CollectedEditionSpan WHERE Source=3 AND IssueStart IS NOT NULL")}
files_at_risk = collections.defaultdict(set)
attached = collections.defaultdict(set)
print(f"{'series':<9} {'item':>8} {'vol':>4} {'pp':>5} {'src':>5} {'claim':<12} {'ppi':>6} {'win':>4} "
      f"{'files':>5}  why / file")
for sid in ids:
    issues = [(num(r[0]), r[1]) for r in con.execute(
        """SELECT cd.IssueNo, i.Id FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
           WHERE i.SeriesId=? AND cd.IsCollection=0 AND coalesce(i.IsExcluded,0)=0
             AND coalesce(i.PageCount,0)>=10""", (sid,)) if num(r[0]) is not None]
    for iid, fn, pc, vol, src, a, b, ref, nsrc, kids in con.execute(
            """SELECT i.Id, i.FileName, i.PageCount, cd.VolumeNo, s.Source, s.IssueStart, s.IssueEnd,
                      s.ProviderRef, coalesce(nd.SpanSource,0),
                      (SELECT count(*) FROM CollectionNode c WHERE c.ParentItemId = i.Id)
               FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
               JOIN CollectedEditionSpan s ON s.ItemId=i.Id AND s.Source<>3
               LEFT JOIN CollectionNode nd ON nd.ItemId=i.Id
               WHERE i.SeriesId=? AND cd.IsCollection=1 AND coalesce(i.IsExcluded,0)=0
                 AND s.IssueStart IS NOT NULL""", (sid,)):
        why = []
        ppi = (pc / (b - a + 1)) if pc and b >= a else None
        if ppi is not None and ppi < 12:
            why.append("thin")
        if vol is not None and a == b == float(vol):
            why.append("ordinal")
        if src == 0 and ref:
            title, fmt, lpages, col = page(str(ref).strip())
            if re.match(r"(?i)^comic\b", fmt):
                why.append(f"mislink={title[:30]}")
        if not why:
            continue
        hit = {x[1] for x in issues if a <= x[0] <= b}
        wins = (nsrc == 2 and src == 2) or (nsrc == 3 and src == 1) or (nsrc == 4 and src == 0)
        tot[why[0].split("=")[0]] += 1
        if wins and iid not in curated:
            # A refuted claim is worse than no claim: it nests files the book cannot hold, and the
            # de-duplication then calls them redundant. Retract it with a tombstone — a Curated row with a
            # NULL range, which ReadingOrderJob reads as "this shelf has no answer here" and which stops the
            # provider leg from being handed the item again. Items already judged by hand are left alone.
            retract[iid] = dict(itemId=iid, seriesId=sid, unknown=True, batch="junk-retract",
                                why=f"{SRC[src]} claims #{a:g}-{b:g} on a {pc}pp book"
                                    + (f" ({ppi:.1f}pp per issue, below the 12pp floor)" if "thin" in why[0] else "")
                                    + (f"; the record it came from is {why[-1][8:]}, a single comic" if any(w.startswith("mislink") for w in why) else "")
                                    + ("; the claim is exactly the volume ordinal" if "ordinal" in why else "")
                                    + f" — it would nest {len(hit)} issue file(s) this book cannot contain")
        if wins:
            files_at_risk[sid] |= hit
            attached[sid] |= {r[0] for r in con.execute(
                """SELECT n.ItemId FROM CollectionNode n JOIN Item i2 ON i2.Id=n.ItemId
                   JOIN ComicDetail c2 ON c2.ItemId=i2.Id
                   WHERE n.ParentItemId=? AND c2.IsCollection=0""", (iid,))}
        print(f"S{sid:<8} {iid:>8} {str(vol):>4} {str(pc):>5} {SRC[src]:>5} {f'#{a:g}-{b:g}':<12} "
              f"{(f'{ppi:.1f}' if ppi else '-'):>6} {'WIN' if wins else '':>4} {len(hit):>5} "
              f"att={kids:<4} {','.join(why)[:40]} | {fn[:40]}")
print(f"\n{sum(tot.values())} refutable claims: {dict(tot)}")
print(f"{sum(len(v) for v in files_at_risk.values())} issue files lie INSIDE a winning refutable claim, "
      f"on {len(files_at_risk)} shelves; {sum(len(v) for v in attached.values())} issue files are "
      f"nested under one TODAY")
if out_path:
    with open(os.path.join(HERE, out_path), "w", encoding="utf-8") as fh:
        for r in retract.values():
            fh.write(json.dumps(r, ensure_ascii=False) + chr(10))
    print(f"{len(retract)} retraction(s) -> {out_path}  (items already judged by hand are excluded)")
