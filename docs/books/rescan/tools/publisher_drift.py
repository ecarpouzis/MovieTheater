"""NEW items whose top-level publisher folder (the first segment under 5 - Comics) is not the folder that holds the
majority of their shelf's PRE-EXISTING books — IDW Sonic trades on Archie's Sonic, Titan Conan on Marvel's Conan,
Ignition's Beauty on Image's. Read-only; group per shelf and publisher folder for reading.
usage: python publisher_drift.py --first-new-id 245531 --out <txt>
"""
import argparse, collections, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--first-new-id", type=int, required=True)
ap.add_argument("--out", required=True)
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
P = "\\\\Library\\Public\\5 - Comics\\"
pub = lambda p: p[len(P):].split("\\", 1)[0] if p.startswith(P) else "?"
old = collections.defaultdict(collections.Counter); new = collections.defaultdict(list)
for iid, sid, p in c.execute("SELECT Id, SeriesId, Path FROM Item WHERE IsExcluded = 0 AND SeriesId IS NOT NULL AND RootId = 1"):
    if iid < a.first_new_id: old[sid][pub(p)] += 1
    else: new[sid].append((iid, pub(p), p[len(P):]))
name = {r[0]: r[1] for r in c.execute("SELECT Id, coalesce(DisplayNameOverride, Name) FROM Series")}
out, n = [], 0
for sid, items in new.items():
    if not old[sid]: continue
    top, k = old[sid].most_common(1)[0]
    if k / sum(old[sid].values()) < 0.6: continue
    off = [x for x in items if x[1] != top and x[1] not in old[sid]]
    if not off: continue
    n += len(off)
    out.append(f"\n### S{sid} {name.get(sid)}  held: {dict(old[sid].most_common(3))}")
    for iid, pb, rel in sorted(off, key=lambda x: x[2]): out.append(f"   {iid}  {rel}")
open(a.out, "w", encoding="utf-8").write("\n".join(out))
print(n, "new items under a publisher folder their shelf never used;", len([l for l in out if l.startswith("\n###")]), "shelves")
