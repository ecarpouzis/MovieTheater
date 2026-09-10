"""Does splitting a conflated shelf by its folder run-markers actually fix the numbering?

The claim under test: on a shelf that holds several publishing runs, the duplicate issue numbers are the
runs colliding, and the folders already say which file belongs to which run. If that is true, then
grouping each shelf's files by folder run-marker should make the duplicates DISAPPEAR inside each group.
If it is false — if the duplicates survive the split — the folders are not the run boundary and this
whole approach is wrong.

Reports the before/after per shelf and in total. Writes nothing.
"""
import collections
import json
import re
import sqlite3
import sys

SURVEY = sys.argv[1]
HOT = sys.argv[2] if len(sys.argv) > 2 else r"F:\Work\MovieTheater\data\books\v2\books.db"

RX_NUM = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")


def num(s):
    m = RX_NUM.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
detail = {r[0]: (r[1], r[2]) for r in con.execute(
    "SELECT ItemId, IssueNo, IsCollection FROM ComicDetail")}

survey = json.load(open(SURVEY, encoding="utf-8"))
multi = [s for s in survey if s["runs"] > 1]

tot_before = tot_after = 0
rows = []
for s in multi:
    all_nums, per_group = [], []
    for g in s["groups"]:
        ns = []
        for iid in g["itemIds"]:
            d = detail.get(iid)
            if not d or d[1]:            # collections do not carry an issue number
                continue
            v = num(d[0])
            if v is not None:
                ns.append(v)
        all_nums += ns
        per_group.append(ns)
    before = sum(c for c in collections.Counter(all_nums).values() if c > 1)
    after = sum(sum(c for c in collections.Counter(ns).values() if c > 1) for ns in per_group)
    tot_before += before
    tot_after += after
    if before:
        rows.append((s["seriesId"], s["name"], len(all_nums), before, after, s["runs"]))

print(f"{len(multi)} multi-run shelves")
print(f"  files sharing an issue number BEFORE the split: {tot_before}")
print(f"  files sharing an issue number AFTER  the split: {tot_after}")
if tot_before:
    print(f"  the folders explain {(tot_before - tot_after) / tot_before:.1%} of the collisions")

rows.sort(key=lambda r: -(r[3] - r[4]))
print("\n--- the 20 shelves the split helps most ---")
for sid, name, n, b, a, runs in rows[:20]:
    print(f"  S{sid:<7} {n:>5} numbered  collisions {b:>5} -> {a:<5} ({runs} runs)  {name}")

print("\n--- shelves where the split does NOT resolve the collisions (folders are not the boundary) ---")
stubborn = [r for r in rows if r[4] > 0.5 * r[3]]
print(f"  {len(stubborn)} of {len(rows)}")
for sid, name, n, b, a, runs in sorted(stubborn, key=lambda r: -r[4])[:15]:
    print(f"  S{sid:<7} {n:>5} numbered  collisions {b:>5} -> {a:<5} ({runs} runs)  {name}")
