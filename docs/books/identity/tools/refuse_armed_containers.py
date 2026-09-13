"""Refuse the armed-but-unjudged containers a wave landing exposes.

    python refuse_armed_containers.py [--wave N] [--apply]

`merge_refusals.py` covers collected editions that MOVED shelves. This covers the other direction: the edition
stayed put and the identity merge brought ISSUE FILES onto its shelf, so a provider span nobody judged is now
nesting real files — `audit_containment.py` fails on "a container with no judged span row at all" with a nested
count. Doctrine (containment PLAN §8, §10.3): an unjudged claim does not nest; the verdict is a refusal until the
book is read. Shelves with a decision file get an ADDENDUM; shelves without one get a file refusing EVERY
collected edition on the shelf (the coverage contract). Dry run by default.
"""
import ast
import datetime
import os
import re
import sqlite3
import subprocess
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
CONT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir, "containment"))
DEC = os.path.join(CONT, "decisions")
APPLY = "--apply" in sys.argv
WAVE = sys.argv[sys.argv.index("--wave") + 1] if "--wave" in sys.argv else "?"
STAMP = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

out = subprocess.run([sys.executable, os.path.join(CONT, "tools", "audit_containment.py"), "--verbose"],
                     capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
block, armed = False, []
for line in out.splitlines():
    if "a container with no judged span row at all" in line:
        block = True
        continue
    if block and re.match(r"\s+(FAIL|ok|lead)\s", line):
        block = False
    if block and line.strip().startswith("("):
        try:
            tup = ast.literal_eval(line.strip())
        except Exception:
            continue
        if isinstance(tup[3], int) and tup[3] > 0:
            armed.append((tup[0], tup[1], tup[3], tup[4]))

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
names = {r[0]: r[1] for r in con.execute("SELECT Id, Name FROM Series")}
plan = []
for sid in sorted({a[1] for a in armed}):
    mine = [a for a in armed if a[1] == sid]
    path = os.path.join(DEC, f"S{sid}.txt")
    exists = os.path.exists(path)
    already = set()
    if exists:
        for raw in open(path, encoding="utf-8"):
            t = raw.strip().split()
            if len(t) >= 2 and t[0] in ("S", "u") and t[1].isdigit():
                already.add(int(t[1]))
    on_shelf = [r[0] for r in con.execute(
        "SELECT i.Id FROM Item i JOIN ComicDetail c ON c.ItemId=i.Id WHERE i.SeriesId=? AND c.IsCollection=1 "
        "AND i.IsExcluded=0 ORDER BY i.Id", (sid,))]
    targets = [i for i in (on_shelf if not exists else [a[0] for a in mine]) if i not in already]
    skipped = [a[0] for a in mine if a[0] in already]
    if skipped:
        print(f"  S{sid}: {skipped} already decided in the file yet unjudged in the DB — re-import needed, not a refusal")
    if not targets:
        continue
    lines = [""] if exists else []
    if exists:
        lines.append(f"# ADDENDUM (identity pass, wave {WAVE}, {STAMP}): the identity merge brought issue files onto this shelf,")
        lines.append("# and the provider span on the edition(s) below — never judged — started nesting them. Refused until read.")
    else:
        lines.append(f"# S{sid} {names.get(sid, '?')} — created by the identity pass, wave {WAVE}, {STAMP}.")
        lines.append("# The identity merge brought issue files onto this shelf and an unjudged provider span started nesting")
        lines.append(f"# them; this shelf had no decision file, so it must now cover ALL {len(on_shelf)} of its collected editions.")
        lines.append("# Nothing here is a range. Every line is a refusal until the book is read (containment PLAN §8, §10.3).")
    nested = {a[0]: a[2] for a in mine}
    for iid in targets:
        why = (f"unjudged provider span armed by the wave-{WAVE} identity merge, nesting {nested[iid]} issue file(s) it was never measured against"
               if iid in nested else "was on this shelf when the wave-%s merge arrived" % WAVE)
        lines.append(f"u {iid} {why}; no range read from the book")
    plan.append((path, exists, lines, targets))

for path, exists, lines, targets in plan:
    print(f"  {'APPEND to' if exists else 'CREATE   '} {os.path.relpath(path, CONT)}   {len(targets)} refusal line(s)")
    for l in lines:
        print(f"      {l[:150]}")
print(f"\n{len(armed)} armed container(s) on {len({a[1] for a in armed})} shelf/shelves; {len(plan)} file(s) to write")
if not APPLY:
    print("(dry run — nothing written. Re-run with --apply, then wave_fix.ps1.)")
    raise SystemExit(0)
for path, exists, lines, _t in plan:
    with open(path, "a" if exists else "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{'appended to' if exists else 'created'} {path}")
