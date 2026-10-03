"""Vet LOCG item credits against ComicVine. ItemCredit(Source=Locg) was copied once from LOCG's automatic item links at
migration and nothing re-derives it; a wrong link credits ANOTHER run's creators (X-Treme X-Men 2012 under Claremont),
and those rows feed the Author/Artist facets and filters.

An item's LOCG credits are judged WRONG when its shelf has a ComicVine volume whose people list (the offline rip) names
NONE of them (full name or surname, accent-folded). Unsure = kept: no CV volume, no people list, or a shelf listed in
docs/books/ratings/identity-suspects.txt (its CV link itself is in doubt).

Dry run by default: writes the verdict CSV (itemId, shelf, names) and counts. --apply (after a backup) deletes exactly
the judged items' Source=Locg ItemCredit rows and sets their automatic (Matched) LOCG ItemProviderLink to Cleared so its description stops feeding them too;
the CSV is the undo record. Chunked by item id (--after / --limit), idempotent (a vetted item has no LOCG rows left).
usage: python scripts/books/locg_credit_vet.py [--apply] [--after ID] [--limit N]
"""
import argparse, collections, csv, json, os, re, sqlite3, unicodedata

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DB = os.path.join(ROOT, "data", "books", "v2", "books.db")
RIP = os.path.join(ROOT, "data", "books", "archive", "mybooks", "comicdb_comicvine_20260122.db")
OUT = os.path.join(ROOT, "docs", "books", "ratings", "locg-credit-vet.csv")
LOCG, CLEARED, MATCHED, MANUAL = 3, 6, 1, 5   # TagSource.Locg; LinkStatus.Cleared / Matched / Manual

ap = argparse.ArgumentParser(); ap.add_argument("--apply", action="store_true")
ap.add_argument("--after", type=int, default=0); ap.add_argument("--limit", type=int, default=0)
a = ap.parse_args()
c = sqlite3.connect(DB if a.apply else f"file:{DB}?mode=ro", uri=not a.apply)
R = sqlite3.connect(f"file:{RIP}?mode=ro", uri=True)

def norm(n):
    n = unicodedata.normalize("NFKD", n or "").encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z]", "", n.lower())
def surname(n):
    parts = re.sub(r"\(.*?\)", "", n or "").split()
    return norm(parts[-1]) if parts else ""

suspects = set()
sp = os.path.join(ROOT, "docs", "books", "ratings", "identity-suspects.txt")
if os.path.exists(sp):
    suspects = {int(m) for m in re.findall(r"\bS?(\d{1,6})\b", open(sp, encoding="utf-8").read())}

people_cache = {}
def people(cv):
    if cv not in people_cache:
        row = R.execute("SELECT raw_api_response FROM cv_volume WHERE id=?", (cv,)).fetchone()
        ppl = []
        if row:
            d = json.loads(row[0]); d = d.get("results", d); ppl = d.get("people") or []
        people_cache[cv] = ({norm(p.get("name")) for p in ppl} | {"~" + surname(p.get("name")) for p in ppl}) if ppl else None
    return people_cache[cv]

rows = c.execute("""SELECT ic.ItemId, i.SeriesId, s.CvVolumeId, group_concat(ic.Name, '|')
    FROM ItemCredit ic JOIN Item i ON i.Id=ic.ItemId JOIN Series s ON s.Id=i.SeriesId
    WHERE ic.Source=? AND i.Kind=0 AND ic.ItemId > ? GROUP BY ic.ItemId ORDER BY ic.ItemId""" + (f" LIMIT {a.limit}" if a.limit else ""),
    (LOCG, a.after)).fetchall()
counts, wrong = collections.Counter(), []
for iid, sid, cv, names in rows:
    counts["items with LOCG credits"] += 1
    if not cv: counts["kept: no CV volume"] += 1; continue
    if sid in suspects: counts["kept: identity suspect"] += 1; continue
    ppl = people(cv)
    if ppl is None: counts["kept: no CV people list"] += 1; continue
    if c.execute(f"SELECT 1 FROM ItemProviderLink WHERE ItemId=? AND Provider=2 AND Status={MANUAL}", (iid,)).fetchone():
        counts["kept: manual LOCG link"] += 1; continue
    ns = [n for n in names.split("|") if n]
    if any(norm(n) in ppl or "~" + surname(n) in ppl for n in ns): counts["confirmed"] += 1; continue
    counts["WRONG (no name on the CV volume)"] += 1
    wrong.append((iid, sid, cv, "; ".join(dict.fromkeys(ns))))

mode = "a" if a.after else "w"
with open(OUT, mode, newline="", encoding="utf-8") as f:
    w = csv.writer(f)
    if mode == "w": w.writerow(["itemId", "seriesId", "cvVolumeId", "locgNames"])
    w.writerows(wrong)
if a.apply and wrong:
    ids = [x[0] for x in wrong]
    with c:
        undo = os.path.join(ROOT, "docs", "books", "ratings", "locg-credit-vet.undo.jsonl")
        cols = [r[1] for r in c.execute("PRAGMA table_info(ItemCredit)")]
        lcols = [r[1] for r in c.execute("PRAGMA table_info(ItemProviderLink)")]
        with open(undo, "a", encoding="utf-8") as u:     # every row this run changes, whole, before it changes
            for k in range(0, len(ids), 500):
                q = ",".join(map(str, ids[k:k + 500]))
                for r in c.execute(f"SELECT * FROM ItemCredit WHERE Source={LOCG} AND ItemId IN ({q})"):
                    u.write(json.dumps({"table": "ItemCredit", "row": dict(zip(cols, r))}, ensure_ascii=False, default=str) + "\n")
                for r in c.execute(f"SELECT * FROM ItemProviderLink WHERE Provider=2 AND Status={MATCHED} AND ItemId IN ({q})"):
                    u.write(json.dumps({"table": "ItemProviderLink", "row": dict(zip(lcols, r))}, ensure_ascii=False, default=str) + "\n")
        for k in range(0, len(ids), 500):
            q = ",".join(map(str, ids[k:k + 500]))
            counts["credit rows deleted"] += c.execute(f"DELETE FROM ItemCredit WHERE Source={LOCG} AND ItemId IN ({q})").rowcount
            counts["links cleared"] += c.execute(f"UPDATE ItemProviderLink SET Status={CLEARED}, Applied=0 WHERE Provider=2 AND Status={MATCHED} AND ItemId IN ({q})").rowcount
print(dict(counts), "next --after", rows[-1][0] if rows else None, "->", OUT, "(APPLIED)" if a.apply else "(dry run)")
