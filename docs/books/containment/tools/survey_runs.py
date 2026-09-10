"""Which shelves hold MORE THAN ONE publishing run, according to the folders they live in?

This is PLAN.md §4.4 — open since v1. A `Series` that conflates runs which restart numbering makes the
same issue number name several different comics, so a "#1-6" edition overlaps every run's #1-6 and the
over-collection guard has to refuse the lot. The census reads it as "1,012 files sharing a number".

The evidence is the folder, and in this library the folder is explicit: `_Green Lantern\\_Completed
Series\\02 Green Lantern v2 (1960)` and `\\03 Green Lantern v3 (1990)` and `\\10 Green Lantern v7 (2023)`
are three runs, and every one of their files currently parses to the single key `Green Lantern (1960)`
because the series name comes from ComicInfo, which says "Green Lantern" for all of them.

This SURVEYS. It writes nothing. It reproduces ComicTitleParser.RelativeComponents +
BestSeriesComponent so the numbers describe what the real parser sees.
"""
import collections
import json
import os
import re
import sqlite3
import sys

HOT = sys.argv[1] if len(sys.argv) > 1 else r"F:\Work\MovieTheater\data\books\v2\books.db"
OUT = sys.argv[2] if len(sys.argv) > 2 else "run_survey.json"

RX_FOLDER_YEAR = re.compile(r"\(\s*((19|20)\d{2})\s*\)")
RX_FOLDER_VOLUME = re.compile(r"\b(?:v(?:ol(?:ume)?)?\.?\s*)(\d+)\b(?!\s*\.\s*\d)", re.I)

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
roots = [r[0] for r in con.execute("SELECT Path FROM LibraryRoot ORDER BY length(Path) DESC")]


def components(path):
    """ComicTitleParser.RelativeComponents: the folder parts below a library root, file name excluded."""
    norm = (path or "").replace("/", os.sep if os.sep == "\\" else "\\").replace("/", "\\")
    for r in roots:
        nr = r.replace("/", "\\").rstrip("\\")
        if nr and norm.lower().startswith(nr.lower()):
            parts = [p for p in norm[len(nr):].lstrip("\\").split("\\") if p]
            return parts[:-1] if len(parts) > 1 else []
    parts = [p for p in norm.split("\\") if p]
    return parts[:-1] if len(parts) > 1 else []


def best_component(comp):
    """ComicTitleParser.BestSeriesComponent: first component after the publisher carrying a (YYYY)."""
    if len(comp) <= 1:
        return None, None, None
    raw = None
    for i in range(1, len(comp)):
        if RX_FOLDER_YEAR.search(comp[i]):
            raw = comp[i]
            break
    if raw is None:
        raw = comp[1]
    ym = RX_FOLDER_YEAR.search(raw)
    year = int(ym.group(1)) if ym else None
    vm = RX_FOLDER_VOLUME.search(raw)
    volume = (vm.group(1).lstrip("0") or "0") if vm else None
    return raw, year, volume


rows = con.execute("""
    SELECT i.SeriesId, i.Id, i.Path, cd.IsCollection, cd.IssueNo
    FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
    WHERE i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0 AND i.SeriesId IS NOT NULL""").fetchall()

per = collections.defaultdict(lambda: collections.defaultdict(list))
for sid, iid, path, iscol, ino in rows:
    raw, year, volume = best_component(components(path))
    per[sid][(year, volume, raw)].append((iid, iscol, ino))

multi = []
for sid, groups in per.items():
    runs = {v for (y, v, r) in groups if v is not None}
    years = {y for (y, v, r) in groups if y is not None}
    if len(runs) > 1 or len(years) > 1:
        multi.append((sid, groups, len(runs), len(years)))

by_run = [m for m in multi if m[2] > 1]
by_year = [m for m in multi if m[2] <= 1 and m[3] > 1]
print(f"shelves holding more than one folder RUN MARKER (v2 / v3 / v7): {len(by_run)}")
print(f"   files in them: {sum(sum(len(v) for v in g.values()) for _, g, _, _ in by_run)}")
print(f"shelves holding one run marker but more than one folder YEAR:  {len(by_year)}")
print(f"   files in them: {sum(sum(len(v) for v in g.values()) for _, g, _, _ in by_year)}")

names = {r[0]: r[1] for r in con.execute(
    "SELECT Id, coalesce(DisplayNameOverride, Name) FROM Series")}

print("\n--- the 25 largest multi-run shelves ---")
for sid, groups, nr, ny in sorted(by_run, key=lambda m: -sum(len(v) for v in m[1].values()))[:25]:
    total = sum(len(v) for v in groups.values())
    marks = sorted({(y, v) for (y, v, r) in groups if v is not None}, key=lambda t: (t[1] or "", t[0] or 0))
    print(f"  S{sid:<7} {total:>5} files  {nr} runs {marks[:6]}  {names.get(sid, '?')}")

out = []
for sid, groups, nr, ny in multi:
    out.append({
        "seriesId": sid,
        "name": names.get(sid),
        "runs": nr,
        "years": ny,
        "groups": [{"year": y, "volume": v, "folder": r, "files": len(items),
                    "collections": sum(1 for it in items if it[1]),
                    "itemIds": [it[0] for it in items]}
                   for (y, v, r), items in sorted(groups.items(), key=lambda kv: -len(kv[1]))],
    })
json.dump(out, open(OUT, "w", encoding="utf-8"), indent=1)
print(f"\n{len(out)} shelves written to {OUT}")
