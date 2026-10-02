"""NEW numbered issues whose issue number the shelf ALREADY holds from a different era (#5 of 2022 beside #5 of 1992):
a relaunch that restarted its numbering and joined the old run by title. year_drift.py misses it whenever the old run
also has late books; this compares like with like — the same number, years apart. Read-only.
usage: python number_clash.py --first-new-id 245531 [--gap 5]
"""
import argparse, collections, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--first-new-id", type=int, required=True)
ap.add_argument("--gap", type=int, default=5)
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
held = collections.defaultdict(lambda: collections.defaultdict(list))
for sid, no, y in c.execute("""SELECT i.SeriesId, d.IssueNo, d.Year FROM Item i JOIN ComicDetail d ON d.ItemId=i.Id
        WHERE i.Id < ? AND i.IsExcluded=0 AND d.IsCollection=0 AND d.IssueNo GLOB '[0-9]*' AND d.Year BETWEEN 1900 AND 2100""", (a.first_new_id,)):
    held[sid][no.lstrip("0") or "0"].append(y)
name = {r[0]: r[1] for r in c.execute("SELECT Id, Name FROM Series")}
hits = collections.defaultdict(list)
for iid, sid, no, y, p in c.execute("""SELECT i.Id, i.SeriesId, d.IssueNo, d.Year, i.Path FROM Item i JOIN ComicDetail d ON d.ItemId=i.Id
        WHERE i.Id >= ? AND i.IsExcluded=0 AND d.IsCollection=0 AND d.IssueNo GLOB '[0-9]*' AND d.Year BETWEEN 1900 AND 2100""", (a.first_new_id,)):
    ys = held[sid].get(no.lstrip("0") or "0")
    if ys and min(abs(y - x) for x in ys) > a.gap:
        hits[sid].append((iid, no, y, min(ys), p.split("\\")[-2][:60], p.split("\\")[-1][:70]))
for sid, hs in sorted(hits.items(), key=lambda kv: -len(kv[1])):
    print(f"S{sid} {name.get(sid)}: {len(hs)}")
    for h in hs[:6]: print("    ", h)
print(len(hits), "shelves")
