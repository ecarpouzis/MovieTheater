"""Did each replacing collection land on the shelf of the issues it replaced? (read-only)

For every `contained` / `contained?` / `edition` verdict whose target is an indexed item, compare the target's
shelf with the shelf(s) the replaced items sat on. A difference is not always wrong (a mini's trade can live on
the parent's line shelf) but it is where a new file's parsed key put it on the wrong run — read every row.
usage: python shelf_drift.py --run data/books/rescan/20261002
"""
import argparse, collections, csv, os, sqlite3

P = "\\\\Library\\Public\\5 - Comics\\"
ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--db", default="data/books/v2/books.db")
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
item = {r[0][len(P):].lower(): r[1:] for r in c.execute("select Path, Id, SeriesId, FileName from Item where RootId=1 and IsExcluded=0")}
shelf_of = {r[0]: r[1] for r in c.execute("select Id, SeriesId from Item where RootId=1")}
name = {r[0]: r[1] for r in c.execute("select Id, coalesce(DisplayNameOverride, Name) from Series")}
key = {r[0]: r[1] for r in c.execute("select s.Id, min(d.ParsedSeriesKey) from Series s join Item i on i.SeriesId=s.Id join ComicDetail d on d.ItemId=i.Id group by s.Id")}
by_t = collections.defaultdict(list)
for r in csv.DictReader(open(os.path.join(a.run, "replaced.tsv"), encoding="utf-8"), delimiter="\t"):
    if r["Target"] and r["Verdict"] != "gone": by_t[r["Target"].lower()].append(r)
rows = []
for t, rs in sorted(by_t.items()):
    hit = item.get(t)
    if not hit: continue
    tid, tsid, fn = hit
    old = collections.Counter(shelf_of.get(int(r["ItemId"])) for r in rs)
    if set(old) == {tsid}: continue
    o, n = old.most_common(1)[0]
    rows.append([tid, fn, tsid, name.get(tsid, ""), key.get(tsid, ""), o, name.get(o, ""), key.get(o, ""), n, rs[0]["Verdict"]])
with open(os.path.join(a.run, "shelf_drift.tsv"), "w", encoding="utf-8", newline="") as f:
    w = csv.writer(f, delimiter="\t", lineterminator="\n")
    w.writerow(["TargetItem", "File", "TargetShelf", "TargetName", "TargetKey", "OldShelf", "OldName", "OldKey", "Replaced", "Verdict"]); w.writerows(rows)
for r in rows: print(f"{r[0]} {r[1][:60]} | now S{r[2]} '{r[3][:30]}' | replaced on S{r[5]} '{r[6][:30]}' key='{r[7][:40]}' x{r[8]}")
print(len(rows), "of", len(by_t), "targets sit on a different shelf than what they replaced")
