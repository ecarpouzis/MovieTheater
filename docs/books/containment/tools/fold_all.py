"""Fold every title folder in the library, in one pass, and write one mapping.

Same rule as fold_by_folder for a single folder — this only avoids doing 385 full-table LIKE scans, and
prints a per-folder summary so the proposal can be read before it is applied. Folders naming two runs at
once are reported, never guessed.

`python fold_all.py [--write out.jsonl] [--top N]`
"""
import collections
import json
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fold_by_folder as F  # noqa: E402

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEP = chr(92)
out_path, top, only_broken = None, 25, True
for a in sys.argv[1:]:
    if a.startswith("--write"):
        out_path = a.split("=", 1)[1] if "=" in a else "fold_all.jsonl"
    elif a.startswith("--top"):
        top = int(a.split("=", 1)[1])
    elif a == "--everything":
        only_broken = False

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""SELECT i.Id, i.Path, i.FileName, cd.IsCollection, cd.IssueNo, cd.ParsedSeriesKey, i.SeriesId
                      FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                      WHERE i.Path LIKE '%5 - Comics%' AND coalesce(i.IsExcluded,0) = 0""").fetchall()
print(f"{len(rows)} comic files loaded")

titles = collections.Counter()
for r in rows:
    parts = (r[1] or "").split("5 - Comics" + SEP, 1)[-1].split(SEP)
    if len(parts) >= 3:
        titles[parts[1]] += 1

# WHAT IS WORTH FOLDING. Re-keying a file moves it to a different Series, and the old row is abandoned —
# with its ComicVine link, its display name and any marks a person left on it. So the fold is limited to
# runs that are actually broken today, in one of the two ways containment cannot survive:
#   scattered   the run's files sit on MORE THAN ONE Series, so a trade can never nest the issues it
#               collects (Green Lantern v3 (1990) is spread over seven shelves).
#   conflated   one Series carries files from two different runs, so two #1s share a number space.
# A run that is already alone on its own shelf is left exactly as it is.
now_series = {r[0]: r[6] for r in rows}


def broken_keys(p):
    by_key = collections.defaultdict(set)
    by_shelf = collections.defaultdict(set)
    for d in p:
        sid = now_series.get(d["itemId"])
        if sid is None:
            continue
        by_key[d["key"]].add(sid)
        by_shelf[sid].add(d["key"])
    return {k for k, sids in by_key.items()
            if len(sids) > 1 or any(len(by_shelf[s]) > 1 for s in sids)}


proposal, per, ambiguous = [], [], []
for title in sorted(titles):
    p, notes = F.fold(title, con, verbose=False, rows=rows)
    if only_broken and p:
        keep = broken_keys(p)
        p = [d for d in p if d["key"] in keep]
    if p:
        moves = len({d["key"] for d in p})
        per.append((title, moves, len(p)))
        proposal += p
    for stem, nums, years, members, iss in notes:
        ambiguous.append((title, stem, nums, years, len(members),
                          f"{int(iss[0])}..{int(iss[-1])}" if iss else "no issue numbers"))

print(f"\n{len(per)} title folders would fold {len(proposal)} files into "
      f"{len({d['key'] for d in proposal})} runs\n")
for title, moves, n in sorted(per, key=lambda t: -t[2])[:top]:
    print(f"  {n:>5} files -> {moves:>3} runs   {title[:56]}")

if ambiguous:
    print(f"\n{len(ambiguous)} folder(s) name two runs at once and are LEFT for a decision:")
    for title, stem, nums, years, n, span in ambiguous[:20]:
        print(f"  {title[:28]:<28} {stem[:26]:<26} v{'+v'.join(nums)} {','.join(years)}  "
              f"{n:>4} files, issues {span}")

if out_path:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), out_path), "w", encoding="utf-8") as fh:
        for d in proposal:
            fh.write(json.dumps(d, ensure_ascii=False) + chr(10))
    print(f"\n{len(proposal)} decision lines -> {out_path}")
