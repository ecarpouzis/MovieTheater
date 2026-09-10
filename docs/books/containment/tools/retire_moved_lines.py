"""Retire decision lines for items that a series rebuild moved to ANOTHER shelf whose own file now decides them.

    python retire_moved_lines.py [--apply]

The coverage contract (pass2 / check_decisions) says a shelf's file decides exactly the collected editions on
that shelf. An identity merge or split moves items; `merge_refusals.py` writes refusals for them on the
DESTINATION shelf's file, but the ORIGIN file still carries its old line and fails with "decision(s) for items
not in this series". This turns that old line into a `# MOVED …` comment — only when the item's current shelf
has a file that decides it (so nothing is ever left undecided), and never touching a line for an item still on
the shelf. Dry-run by default.
"""
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.abspath(os.path.join(HERE, os.pardir, "decisions"))
APPLY = "--apply" in sys.argv
RX = re.compile(r"^([SuF])\s+(\d+)\b")

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
shelf_of = {r[0]: r[1] for r in con.execute("SELECT Id, SeriesId FROM Item")}


def decided_in(sid):
    p = os.path.join(DEC, f"S{sid}.txt")
    if not os.path.exists(p):
        return set()
    out = set()
    for l in open(p, encoding="utf-8"):
        m = RX.match(l)
        if m and m.group(1) in "Su":
            out.add(int(m.group(2)))
    return out


retired = 0
for fn in sorted(os.listdir(DEC)):
    if not (fn.startswith("S") and fn.endswith(".txt")):
        continue
    sid = int(fn[1:-4])
    path = os.path.join(DEC, fn)
    lines = open(path, encoding="utf-8").read().splitlines()
    out, changed = [], []
    for l in lines:
        m = RX.match(l)
        if m and m.group(1) in "Su":
            iid = int(m.group(2))
            now = shelf_of.get(iid)
            if now is not None and now != sid and iid in decided_in(now):
                out.append(f"# MOVED (series rebuild, retired by retire_moved_lines.py): item {iid} now sits on S{now}, whose file decides it")
                out.append("# " + l)
                changed.append((iid, now))
                continue
        out.append(l)
    if changed:
        retired += len(changed)
        print(f"{fn}: {len(changed)} line(s) -> " + ", ".join(f"{i}->S{n}" for i, n in changed))
        if APPLY:
            open(path, "w", encoding="utf-8").write("\n".join(out) + "\n")
print(f"{retired} line(s) {'retired' if APPLY else 'would be retired (dry run; --apply to write)'}")
