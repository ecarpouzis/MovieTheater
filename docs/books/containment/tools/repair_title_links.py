"""Undo the batch-wide franchise/title stamp that books-series-split used to apply.

THE FAULT. Until now the split verb computed ONE franchise and ONE TitleId for a whole batch — the first
non-empty pair it found among all the shelves the batch touched — and wrote them onto every Series row it
created. A 44-run batch therefore filed `G.I. Joe v2 (2001)` and `Batman Beyond v6 (2016)` under the title
`30 Days of Night`. The verb now inherits per key; this repairs the rows it already wrote.

TWO INDEPENDENT CHECKS, each provable without trusting the other:

  title      a Series' TitleId is wrong when the title's Key is not the stem of the Series' own name.
             `Harley Quinn v2 (2014)` under title `Harley Quinn` is right; the same run under
             `30 Days of Night` is not. This needs no history — the row contradicts itself.
  franchise  the shelf a run was split OUT of is recorded in series-split-undo.csv (SeriesIdAtSplit, the
             FIRST row for each item, which is the original shelf before any later re-key). A run adopts
             that shelf's franchise. Where the log says nothing, the row is left alone.

Both are conservative: a link that checks out is untouched, and a franchise with no recorded parent stays.
Dry-run by default. `python repair_title_links.py [--apply]`
"""
import collections
import csv
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
UNDO = [r"F:\Work\MovieTheater\docs\books\containment\reports\series-split-undo.csv",
        r"F:\Work\MovieTheater\series-split-undo.csv",
        r"F:\Work\MovieTheater\src\MovieTheater.BooksHost\series-split-undo.csv"]
APPLY = "--apply" in sys.argv

RX_RUN = re.compile(r"^(?P<title>.+?)\s+v(?:ol(?:ume)?)?\.?\s*\d{1,3}(?:\.\d+)?\s*\((?:19|20)\d{2}\)\s*$", re.I)
RX_YEAR = re.compile(r"\s*\((?:19|20)\d{2}\)\s*$")


def title_of(name, parsed):
    for cand in (parsed or "", name or ""):
        m = RX_RUN.match((cand or "").strip())
        if m:
            return m.group("title").strip()
    return RX_YEAR.sub("", (name or "").strip()).strip()


con = sqlite3.connect(HOT)
titles = {r[0]: r[1] for r in con.execute("SELECT Id, Key FROM SeriesTitle")}
rows = con.execute("""SELECT s.Id, coalesce(s.DisplayNameOverride, s.Name), s.ParsedKey, s.TitleId, s.Franchise
                      FROM Series s WHERE s.CanonicalKey NOT LIKE 'book:%'""").fetchall()

bad_title = []
for sid, name, parsed, tid, fr in rows:
    if tid is None:
        continue
    key = titles.get(tid)
    if key is None or key.strip().lower() != title_of(name, parsed).strip().lower():
        bad_title.append((sid, name, tid, key))

# ── franchise, from the shelf each run was actually split out of ───────────────────────────────────
first_parent = {}                                   # itemId -> the shelf it sat on BEFORE any split
new_key_of = {}                                     # itemId -> the key it ended up with
for path in UNDO:
    if not os.path.exists(path):
        continue
    with open(path, encoding="utf-8") as fh:
        for row in csv.DictReader(fh):
            try:
                item = int(row["ItemId"]); was = int(row["SeriesIdAtSplit"])
            except (KeyError, ValueError):
                continue
            first_parent.setdefault(item, was)
            new_key_of[item] = row.get("NewParsedSeriesKey", "")

now_series = {r[0]: r[1] for r in con.execute("SELECT Id, SeriesId FROM Item WHERE SeriesId IS NOT NULL")}
parent_fr = {r[0]: r[1] for r in con.execute(
    "SELECT Id, Franchise FROM Series WHERE Franchise IS NOT NULL AND Franchise <> ''")}
votes = collections.defaultdict(collections.Counter)
for item, was in first_parent.items():
    cur = now_series.get(item)
    fr = parent_fr.get(was)
    if cur and fr:
        votes[cur][fr] += 1

cur_fr = {r[0]: r[1] for r in con.execute("SELECT Id, Franchise FROM Series")}
bad_fr = []
for sid, c in votes.items():
    want = c.most_common(1)[0][0]
    if (cur_fr.get(sid) or "") != want:
        bad_fr.append((sid, cur_fr.get(sid), want))

print(f"Series carrying a TitleId that contradicts their own name : {len(bad_title)}")
for sid, name, tid, key in bad_title[:10]:
    print(f"   S{sid:<7} {name[:40]:<40} title {tid} = {key!r}")
print(f"Series whose franchise disagrees with the shelf they came from : {len(bad_fr)}")
for sid, cur, want in bad_fr[:10]:
    print(f"   S{sid:<7} {cur!r:<24} -> {want!r}")

if not APPLY:
    print("\n(dry run - re-run with --apply, then re-run build_titles.py --apply to re-link)")
    raise SystemExit
con.executemany("UPDATE Series SET TitleId = NULL WHERE Id = ?", [(s,) for s, *_ in bad_title])
con.executemany("UPDATE Series SET Franchise = ? WHERE Id = ?", [(w, s) for s, _, w in bad_fr])
con.commit()
print(f"\napplied: {len(bad_title)} title links cleared, {len(bad_fr)} franchises corrected")
