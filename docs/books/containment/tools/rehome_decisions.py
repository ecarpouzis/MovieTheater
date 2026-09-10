"""Follow every judgement to the shelf its ITEM now lives on.

A decision file is named for a Series, but what was judged is an ITEM: "this book collects #22-27". When
fold_by_folder moves a file to the run its folder names, the judgement is still true — it is simply filed
under the wrong shelf, and pass2 rightly refuses to expand a file holding decisions for items that are not
in that series.

So the judgement moves with the item. Nothing is re-decided here and no range is changed; the lines are
regrouped and the prose that explained them travels with the largest group, with a line recording where
each file's contents came from.

`U <seriesId>` (refuse everything on this shelf) is expanded into one `u <itemId>` per item BEFORE the
regroup — a blanket refusal is about a shelf, and once its items are on several shelves the blanket would
silently start covering books it never saw.

Dry-run by default. `python rehome_decisions.py [--apply]`
"""
import collections
import os
import re
import sqlite3
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")
HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
APPLY = "--apply" in sys.argv

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
home = {r[0]: r[1] for r in con.execute("SELECT Id, SeriesId FROM Item WHERE SeriesId IS NOT NULL")}
members = collections.defaultdict(list)
for iid, sid in con.execute("""SELECT i.Id, i.SeriesId FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
                               WHERE cd.IsCollection=1 AND coalesce(i.IsExcluded,0)=0 AND i.SeriesId IS NOT NULL"""):
    members[sid].append(iid)

files = sorted(f for f in os.listdir(DEC) if re.match(r"^S\d+\.txt$", f))
lines_for = collections.defaultdict(list)          # target series -> [(kind, itemId, text)]
prose_for = collections.defaultdict(list)          # target series -> [(count, header text)]
moved, kept, orphan = 0, 0, []

for f in files:
    sid = int(re.match(r"^S(\d+)", f).group(1))
    header, body = [], []
    for raw in open(os.path.join(DEC, f), encoding="utf-8"):
        line = raw.rstrip("\n")
        if line.startswith("#") or not line.strip():
            header.append(line)
            continue
        body.append(line)
    out = []
    for line in body:
        kind, rest = (line.split(None, 1) + [""])[:2]
        if kind in ("S", "u", "F"):
            iid = int(rest.split("|")[0].split()[0])
            out.append((kind, iid, line))
        elif kind == "U":
            why = rest.split(None, 1)[1] if len(rest.split(None, 1)) > 1 else "no known range"
            for iid in members.get(sid, []):
                out.append(("u", iid, f"u {iid} {why}"))
        elif kind == "N":
            out.append(("N", None, line))
    where = collections.Counter(home.get(iid) for _, iid, _ in out if iid is not None)
    for kind, iid, line in out:
        target = sid if iid is None else home.get(iid)
        if target is None:
            orphan.append((f, line[:60]))
            continue
        if kind == "N":
            target = where.most_common(1)[0][0] if where else sid
        lines_for[target].append((kind, iid, line))
        if iid is not None:
            if target == sid:
                kept += 1
            else:
                moved += 1
    if where:
        top = where.most_common(1)[0][0]
        prose_for[top].append((where[top], "\n".join(header)))

print(f"{len(files)} decision files -> {len(lines_for)} shelves")
print(f"  judgements that stay put : {kept}")
print(f"  judgements that MOVE     : {moved}")
print(f"  items no longer on any shelf : {len(orphan)}")
for f, l in orphan[:6]:
    print(f"     {f}: {l}")

if not APPLY:
    print("\n(dry run - re-run with --apply)")
    raise SystemExit

for f in files:
    os.remove(os.path.join(DEC, f))
for sid, rows in sorted(lines_for.items()):
    prose = sorted(prose_for.get(sid, []), reverse=True)
    seen, body = set(), []
    for kind, iid, line in rows:
        key = (kind, iid, line)
        if key in seen:
            continue
        seen.add(key)
        body.append(line)
    with open(os.path.join(DEC, f"S{sid}.txt"), "w", encoding="utf-8") as fh:
        if prose:
            fh.write(prose[0][1].rstrip() + "\n")
        if len(prose) > 1:
            fh.write(f"#\n# (this shelf also received judgements re-homed from {len(prose) - 1} other "
                     f"decision file(s) when fold_by_folder moved their files here)\n")
        fh.write("\n" + "\n".join(body) + "\n")
print(f"\napplied: {len(lines_for)} decision files written")
