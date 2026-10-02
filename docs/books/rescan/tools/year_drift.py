"""New items that landed on a same-title shelf of a DIFFERENT era (IDW 2018 Sonic trades on the Archie 1993 run;
Titan 2024 Conan trades on the Marvel 1970 run). A new book whose year is outside its shelf's pre-existing span by
more than `--slack` years, on a shelf with a provider-identified run, is listed for reading. Read-only.
usage: python year_drift.py --first-new-id 245531 [--slack 3]
"""
import argparse, collections, re, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--first-new-id", type=int, required=True)
ap.add_argument("--slack", type=int, default=3)
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
span = {}
for sid, lo, hi, n in c.execute("""SELECT i.SeriesId, min(d.Year), max(d.Year), count(*) FROM Item i JOIN ComicDetail d ON d.ItemId = i.Id
        WHERE i.Id < ? AND i.IsExcluded = 0 AND d.Year BETWEEN 1900 AND 2100 GROUP BY i.SeriesId""", (a.first_new_id,)):
    span[sid] = (lo, hi, n)
meta = {r[0]: r[1:] for r in c.execute("SELECT s.Id, coalesce(s.DisplayNameOverride, s.Name), s.CvVolumeId, v.StartYear FROM Series s LEFT JOIN CvVolume v ON v.Id = s.CvVolumeId")}
hits = collections.defaultdict(list)
for iid, sid, fn, yr in c.execute("""SELECT i.Id, i.SeriesId, i.FileName, d.Year FROM Item i JOIN ComicDetail d ON d.ItemId = i.Id
        WHERE i.Id >= ? AND i.IsExcluded = 0 AND d.Year BETWEEN 1900 AND 2100""", (a.first_new_id,)):
    if sid not in span: continue
    lo, hi, n = span[sid]
    if yr > hi + a.slack or yr < lo - a.slack:
        hits[sid].append((iid, yr, fn))
for sid, hs in sorted(hits.items(), key=lambda kv: -len(kv[1])):
    lo, hi, n = span[sid]; nm, cv, cy = meta.get(sid, ("?", None, None))
    print(f"S{sid} {nm[:45]} (cv {cv} {cy}) held {lo}-{hi} x{n}: {len(hs)} new outside -> e.g. [{hs[0][0]}] {hs[0][1]} {hs[0][2][:80]}")
print(len(hits), "shelves,", sum(len(v) for v in hits.values()), "new items")
