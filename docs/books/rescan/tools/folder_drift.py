"""Every NEW item whose shelf differs from the shelf its folder's PRE-EXISTING books sit on (by majority), or — for a
folder with no pre-existing books — from the shelf its sibling NEW books mostly sit on. Catches what year_drift.py's
era test cannot (Titan's 2024 Conan trades on Marvel's 1970-2022 run; IDW Sonic trades on Archie's run). Read-only;
every row is for reading, not applying: a mini's trade may legitimately live on its parent line's shelf.
usage: python folder_drift.py --first-new-id 245531 --out <tsv>
"""
import argparse, collections, csv, os, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--first-new-id", type=int, required=True)
ap.add_argument("--out", required=True)
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
rows = c.execute("""SELECT i.Id, i.SeriesId, i.Path, d.ParsedSeriesKey FROM Item i JOIN ComicDetail d ON d.ItemId = i.Id
        WHERE i.IsExcluded = 0 AND i.SeriesId IS NOT NULL AND i.RootId = 1""").fetchall()
old_by_dir, new_by_dir = collections.defaultdict(collections.Counter), collections.defaultdict(collections.Counter)
for iid, sid, p, k in rows:
    (new_by_dir if iid >= a.first_new_id else old_by_dir)[os.path.dirname(p)][sid] += 1
name = {r[0]: r[1] for r in c.execute("SELECT Id, coalesce(DisplayNameOverride, Name) FROM Series")}
out = []
for iid, sid, p, k in rows:
    if iid < a.first_new_id: continue
    d = os.path.dirname(p)
    pool, basis = (old_by_dir[d], "old") if old_by_dir[d] else (new_by_dir[d], "new")
    top, n = pool.most_common(1)[0]
    tot = sum(pool.values())
    if top != sid and n / tot >= 0.6 and n >= 2:
        out.append((iid, sid, name.get(sid), top, name.get(top), f"{n}/{tot}", basis, k, os.path.basename(p), d[-80:]))
with open(a.out, "w", encoding="utf-8", newline="") as f:
    w = csv.writer(f, delimiter="\t", lineterminator="\n")
    w.writerow(["ItemId", "Shelf", "ShelfName", "FolderShelf", "FolderShelfName", "Share", "Basis", "Key", "File", "Dir"]); w.writerows(out)
print(len(out), "new items off their folder's shelf;", len({r[1] for r in out}), "shelves")
