"""Import ComicVine per-issue creator credits from the offline rip into ItemCredit(Source=Cv).

Before this, ComicVine credits existed nowhere in the hot file: an issue with no ComicInfo carried only LOCG's credits
(wrong-run often enough to be vetted away — locg_credit_vet.py) or none. The rip
(data/books/archive/mybooks/comicdb_comicvine_20260122.db, cv_issue.raw_api_response.person_credits) has them for
every issue ComicVine knows.

Guard (identity first): an item's credits land only when its CV issue link (ItemProviderLink Provider=Cv, Matched or
Manual, ProviderKey = the issue id) names an issue whose OWN volume in the rip equals the item's shelf identity
(Series.CvVolumeId). A link into another volume is skipped and counted — never trusted.
Idempotent: an item that already has ComicInfo or Cv credits is skipped. Chunked by Item.Id (--after / --limit),
progress per chunk; dry run by default, --apply writes one transaction per chunk.
usage: python scripts/books/cv_credits_import.py [--apply] [--after ID] [--limit 5000] [--max-chunks N]
"""
import argparse, collections, json, os, re, sqlite3

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DB = os.path.join(ROOT, "data", "books", "v2", "books.db")
RIP = os.path.join(ROOT, "data", "books", "archive", "mybooks", "comicdb_comicvine_20260122.db")
CV = 1   # TagSource.Cv; ItemProviderLink Provider.Cv = 0
ROLES = {"writer": "Writer", "penciler": "Penciller", "penciller": "Penciller", "artist": "Artist", "inker": "Inker",
         "colorist": "Colorist", "letterer": "Letterer", "editor": "Editor", "cover": "Cover Artist", "plotter": "Plotter",
         "translator": "Translator"}

ap = argparse.ArgumentParser(); ap.add_argument("--apply", action="store_true")
ap.add_argument("--after", type=int, default=0); ap.add_argument("--limit", type=int, default=5000)
ap.add_argument("--max-chunks", type=int, default=0)
a = ap.parse_args()
c = sqlite3.connect(DB) if a.apply else sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
R = sqlite3.connect(f"file:{RIP}?mode=ro", uri=True)

def norm(n): return re.sub(r"\s+", " ", n.strip().lower())

after, chunk, total = a.after, 0, collections.Counter()
while True:
    rows = c.execute("""SELECT i.Id, l.ProviderKey, s.CvVolumeId FROM Item i
        JOIN ItemProviderLink l ON l.ItemId=i.Id AND l.Provider=0 AND l.Status IN (1,5) AND l.ProviderKey IS NOT NULL
        JOIN Series s ON s.Id=i.SeriesId
        WHERE i.Kind=0 AND i.IsExcluded=0 AND i.Id > ? ORDER BY i.Id LIMIT ?""", (after, a.limit)).fetchall()
    if not rows: break
    counts, inserts = collections.Counter(), []
    for iid, key, shelf_cv in rows:
        counts["linked items"] += 1
        if c.execute("SELECT 1 FROM ItemCredit WHERE ItemId=? AND Source IN (0, ?)", (iid, CV)).fetchone():
            counts["skip: already has ComicInfo/Cv credits"] += 1; continue
        try: issue = int(key)
        except ValueError: counts["skip: non-numeric key"] += 1; continue
        raw = R.execute("SELECT raw_api_response FROM cv_issue WHERE id=?", (issue,)).fetchone()
        if not raw: counts["skip: issue not in rip"] += 1; continue
        d = json.loads(raw[0]); d = d.get("results", d)
        vol = (d.get("volume") or {}).get("id")
        if not shelf_cv or vol != shelf_cv: counts["skip: issue's volume is not the shelf's identity"] += 1; continue
        creds = d.get("person_credits") or []
        if not creds: counts["skip: CV has no credits"] += 1; continue
        ordinal, seen = 0, set()
        for p in creds:
            name = (p.get("name") or "").strip()
            if not name: continue
            for r in (x.strip().lower() for x in (p.get("role") or "").split(",")):
                role = ROLES.get(r, r.title() if r else "Other")
                if (role, name.lower()) in seen: continue
                seen.add((role, name.lower()))
                inserts.append((iid, CV, ordinal, role, name, norm(name), str(p.get("id")) if p.get("id") else None)); ordinal += 1
        counts["items credited"] += 1
    counts["credit rows"] = len(inserts)
    if a.apply and inserts:
        with c:
            c.executemany("INSERT OR IGNORE INTO ItemCredit (ItemId, Source, Ordinal, Role, Name, NormalizedName, ProviderPersonId) VALUES (?,?,?,?,?,?,?)", inserts)
    after = rows[-1][0]; chunk += 1; total.update(counts)
    print(f"chunk {chunk}: next --after {after}  {dict(counts)}", flush=True)
    if a.max_chunks and chunk >= a.max_chunks: break
print("TOTAL", dict(total), "(APPLIED)" if a.apply else "(dry run)")
