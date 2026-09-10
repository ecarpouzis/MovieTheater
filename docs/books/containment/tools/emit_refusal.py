"""Write a whole-shelf refusal decision file, with the reason supplied by hand and the COVERAGE by the
catalog.

A refusal still has to name every collected edition on the shelf or pass2.py rejects the file. Typing
those ids by hand is exactly the transcription error nobody catches, so this reads them from books.db
and writes one `u <itemId>` line each. It supplies no reason of its own: `--why` is mine, and `--head`
is the prose block explaining what was read.

    python emit_refusal.py --sid=66443 --why="..." --head=head.txt [--note="..."] [--flag=issue-numbers-wrong:114540,114541]

Writes ../decisions/S<sid>.txt and refuses to overwrite an existing file.
"""
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

sid, why, head, note, flags = None, None, None, None, []
for a in sys.argv[1:]:
    k, _, v = a.partition("=")
    if k == "--sid":
        sid = int(v)
    elif k == "--why":
        why = v
    elif k == "--head":
        head = v
    elif k == "--note":
        note = v
    elif k == "--flag":
        kind, _, ids = v.partition(":")
        flags.append((kind, [int(x) for x in ids.split(",") if x]))
    elif k == "--flagdetail":
        flags[-1] = (flags[-1][0], flags[-1][1], v)

if sid is None or not why:
    raise SystemExit("--sid and --why are required")
dst = os.path.join(DEC, f"S{sid}.txt")
if os.path.exists(dst):
    raise SystemExit(f"{dst} already exists - leave it and pick another shelf")

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
    SELECT i.Id, i.FileName FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
    WHERE i.SeriesId = ? AND coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
    ORDER BY i.FileName""", (sid,)).fetchall()
if not rows:
    raise SystemExit(f"S{sid} holds no collected edition")

with open(dst, "w", encoding="utf-8") as out:
    if head:
        out.write(open(head, encoding="utf-8").read().rstrip() + "\n\n")
    if note:
        out.write(f"N {sid} {note}\n\n")
    for kind, ids, *detail in flags:
        for iid in ids:
            out.write(f"F {iid} {kind} | {detail[0] if detail else why}\n")
    if flags:
        out.write("\n")
    for iid, fn in rows:
        out.write(f"u {iid} {why}\n")
print(f"wrote {dst}: {len(rows)} refusals")
