"""After books-scan: prove the scan did exactly what the diff predicted. Read-only.
- every item marked missing by this run is in replaced.tsv (or was already excluded before the run);
- every replaced item is now excluded with reason 'missing';
- no relocated item was marked missing or duplicated;
- new items: count, broken count.
usage: python post_scan_check.py --run data/books/rescan/20261002 --first-new-id 245531 --since 2026-10-02
"""
import argparse, csv, os, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--db", default="data/books/v2/books.db")
ap.add_argument("--first-new-id", type=int, required=True); ap.add_argument("--since", required=True)
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
repl = {int(r["ItemId"]) for r in csv.DictReader(open(os.path.join(a.run, "replaced.tsv"), encoding="utf-8"), delimiter="\t")}
reloc = {int(r["ItemId"]) for r in csv.DictReader(open(os.path.join(a.run, "relocate.tsv"), encoding="utf-8"), delimiter="\t")}
marked = {r[0] for r in c.execute("""select i.Id from Item i join ItemState s on s.ItemId=i.Id
    where i.RootId=1 and s.BrokenReason='missing' and s.ExcludedAt >= ?""", (a.since,))}
excluded_missing = {r[0] for r in c.execute("""select i.Id from Item i join ItemState s on s.ItemId=i.Id
    where i.RootId=1 and i.IsExcluded=1 and s.BrokenReason='missing'""")}
print("marked missing this run:", len(marked))
print("  of them in replaced.tsv:", len(marked & repl), " NOT in replaced:", sorted(marked - repl)[:20])
print("replaced not excluded/missing:", sorted(repl - excluded_missing)[:20], len(repl - excluded_missing))
print("relocated now missing:", sorted(reloc & excluded_missing)[:20])
n, lo, hi = c.execute("select count(*), min(Id), max(Id) from Item where Id >= ?", (a.first_new_id,)).fetchone()
br = c.execute("select count(*) from Item i join ItemState s on s.ItemId=i.Id where i.Id >= ? and s.IsBroken=1", (a.first_new_id,)).fetchone()[0]
print(f"new items: {n} (ids {lo}-{hi}), broken: {br}")
dup = c.execute("select lower(Path), count(*) from Item where RootId=1 group by 1 having count(*)>1").fetchall()
print("case-folded duplicate paths:", len(dup), dup[:5])
