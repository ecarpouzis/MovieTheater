"""Repair Series.Franchise using the TITLE tier, then report what else the tier can refine.

My splits created run-Series without carrying the originating shelf's `Franchise` across, so runs of a
title that had one now sit beside siblings that still do. The title tier makes the repair evidence-based
rather than a guess: a run inherits the franchise its OWN title already carries, and only when the title
is unanimous about it.

Three sources, strongest first — a row is repaired by the first that answers, and the source is recorded:
  parent    the Series this run was split OUT of still carries a franchise (from series-split-undo.csv,
            which records every item's previous key, plus the pre-split backup).
  sibling   another run of the SAME TITLE carries one, and every run of that title that has a franchise
            agrees. A title where two runs disagree is left alone and listed.
  none      nothing to inherit; untouched.

Dry-run by default. `python repair_franchise.py [--apply]`
"""
import collections
import csv
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
BACKUP = r"F:\Work\MovieTheater\data\books\v2\backup-20260908-141722\books.db"
UNDO = r"F:\Work\MovieTheater\series-split-undo.csv"
APPLY = "--apply" in sys.argv

RX_RUN = re.compile(r"^(?P<title>.+?)\s+v(?:ol(?:ume)?)?\.?\s*\d{1,3}(?:\.\d+)?\s*\((?:19|20)\d{2}\)\s*$", re.I)
RX_YEAR = re.compile(r"\s*\((?:19|20)\d{2}\)\s*$")

con = sqlite3.connect(HOT)
rows = con.execute("""
    SELECT s.Id, coalesce(s.DisplayNameOverride, s.Name), s.Franchise, s.ParsedKey
    FROM Series s WHERE s.CanonicalKey NOT LIKE 'book:%'""").fetchall()


def title_of(name, parsed):
    for cand in (parsed or "", name or ""):
        m = RX_RUN.match(cand.strip())
        if m:
            return m.group("title").strip()
    return RX_YEAR.sub("", (name or "").strip()).strip()


by_title = collections.defaultdict(list)
for sid, name, fr, parsed in rows:
    by_title[title_of(name, parsed)].append((sid, name, fr))

# ── source 1: the shelf each split item came from, via the undo log + the pre-split backup ──────────
parent_fr = {}
if os.path.exists(UNDO) and os.path.exists(BACKUP):
    old = sqlite3.connect(f"file:{BACKUP}?mode=ro", uri=True)
    old_fr = {r[0]: r[1] for r in old.execute(
        "SELECT Id, Franchise FROM Series WHERE Franchise IS NOT NULL AND Franchise <> ''")}
    now_series = {r[0]: r[1] for r in con.execute("SELECT ItemId, SeriesId FROM (SELECT Id AS ItemId, SeriesId FROM Item)")}
    votes = collections.defaultdict(collections.Counter)
    with open(UNDO, encoding="utf-8") as fh:
        for row in csv.DictReader(fh):
            try:
                item = int(row["ItemId"]); was = int(row["SeriesIdAtSplit"])
            except (KeyError, ValueError):
                continue
            fr = old_fr.get(was)
            cur = now_series.get(item)
            if fr and cur:
                votes[cur][fr] += 1
    for sid, c in votes.items():
        parent_fr[sid] = c.most_common(1)[0][0]

fixed, conflicted, untouched = [], [], 0
for title, group in by_title.items():
    have = {f for _, _, f in group if f}
    for sid, name, fr in group:
        if fr:
            continue
        if sid in parent_fr:
            fixed.append((sid, name, parent_fr[sid], "parent"))
        elif len(have) == 1:
            fixed.append((sid, name, next(iter(have)), "sibling"))
        elif len(have) > 1:
            conflicted.append((sid, name, sorted(have)))
        else:
            untouched += 1

by_src = collections.Counter(s for _, _, _, s in fixed)
print(f"comic Series with no franchise that CAN be repaired : {len(fixed)}  {dict(by_src)}")
print(f"  titles whose runs disagree about the franchise    : {len(conflicted)}  (left alone)")
print(f"  nothing in their title carries one                : {untouched}")
print("\nsample:")
for sid, name, fr, src in fixed[:12]:
    print(f"   S{sid:<7} <- {fr!r:<22} via {src:<8} {name[:44]}")
for sid, name, opts in conflicted[:5]:
    print(f"   S{sid:<7} CONFLICT {opts}  {name[:40]}")

if APPLY and fixed:
    con.executemany("UPDATE Series SET Franchise = ? WHERE Id = ?", [(f, s) for s, _, f, _ in fixed])
    con.commit()
    print(f"\napplied: {len(fixed)} Series given a franchise")
else:
    print("\n(dry run - re-run with --apply)")
con.close()
