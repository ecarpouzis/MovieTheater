"""What the share LOST: every item the rescan replaced with verdict `gone`, re-checked against today's library —
is the same issue still held on its shelf (another copy), or does a live collected edition on that shelf now hold it by
a judged range? Whatever is neither is truly missing. Read-only.
usage: python missing_report.py --run docs/books/rescan/20261002 [--out <tsv>]
"""
import argparse, csv, os, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--out")
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
rows = [r for r in csv.DictReader(open(os.path.join(a.run, "replaced.tsv"), encoding="utf-8"), delimiter="\t") if r["Verdict"] == "gone"]
out = []
for r in rows:
    iid = int(r["ItemId"])
    sid, fn, pc, no, coll = c.execute("""SELECT i.SeriesId, i.FileName, i.PageCount, d.IssueNo, d.IsCollection FROM Item i
        LEFT JOIN ComicDetail d ON d.ItemId=i.Id WHERE i.Id=?""", (iid,)).fetchone()
    shelf = c.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id=?", (sid,)).fetchone()
    status, by = "MISSING", ""
    if no and not coll:
        dup = c.execute("""SELECT i.FileName FROM Item i JOIN ComicDetail d ON d.ItemId=i.Id WHERE i.SeriesId=? AND i.IsExcluded=0
            AND d.IsCollection=0 AND ltrim(d.IssueNo,'0')=ltrim(?,'0') LIMIT 1""", (sid, no)).fetchone()
        try: n = float(no)
        except ValueError: n = None
        span = c.execute("""SELECT i.FileName, sp.IssueStart, sp.IssueEnd FROM CollectedEditionSpan sp JOIN Item i ON i.Id=sp.ItemId
            WHERE i.SeriesId=? AND i.IsExcluded=0 AND sp.Source=3 AND sp.IssueStart<=? AND sp.IssueEnd>=? LIMIT 1""", (sid, n, n)).fetchone() if n is not None else None
        if dup: status, by = "held (another copy)", dup[0]
        elif span: status, by = "held (in a collection)", f"{span[0]} #{span[1]:g}-{span[2]:g}"
    out.append((status, iid, sid, shelf[0] if shelf else "", no or "", pc, r["OldPath"], r["Why"], by))
out.sort(key=lambda x: (x[0] != "MISSING", x[6]))
if a.out:
    with open(a.out, "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f, delimiter="\t", lineterminator="\n")
        w.writerow(["Status", "ItemId", "SeriesId", "Shelf", "Issue", "Pages", "OldPath", "Why", "NowHeldBy"]); w.writerows(out)
for o in out: print(f"{o[0]:24} {o[1]} | {o[6][:120]} | {o[8][:80] or o[7][:80]}")
print({s: sum(1 for o in out if o[0] == s) for s in {o[0] for o in out}})
