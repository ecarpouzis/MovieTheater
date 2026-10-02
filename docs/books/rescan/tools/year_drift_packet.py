"""Reading packet for year_drift.py's shelves: per shelf, the HELD books (issue/year/folder, condensed) and every new
book outside the shelf's era with its folder, so a reader can tell a continuing numbering (#257 of a 1994 run) from
a new run that restarted at #1 under the same title. Read-only.
usage: python year_drift_packet.py --first-new-id 245531 --out <txt> [--slack 3]
"""
import argparse, collections, os, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--first-new-id", type=int, required=True)
ap.add_argument("--slack", type=int, default=3); ap.add_argument("--out", required=True)
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
P = "\\\\Library\\Public\\5 - Comics\\"
rows = c.execute("""SELECT i.Id, i.SeriesId, i.Path, d.Year, d.IssueNo, d.IsCollection, d.ParsedSeriesKey FROM Item i JOIN ComicDetail d ON d.ItemId = i.Id
        WHERE i.IsExcluded = 0 AND i.SeriesId IS NOT NULL""").fetchall()
by = collections.defaultdict(list)
for r in rows: by[r[1]].append(r)
out = []
for sid, rs in by.items():
    old = [r for r in rs if r[0] < a.first_new_id and r[3] and 1900 <= r[3] <= 2100]
    if not old: continue
    lo, hi = min(r[3] for r in old), max(r[3] for r in old)
    new = [r for r in rs if r[0] >= a.first_new_id and r[3] and (r[3] > hi + a.slack or r[3] < lo - a.slack)]
    if not new: continue
    s = c.execute("SELECT coalesce(DisplayNameOverride, Name), CanonicalKey, CvVolumeId FROM Series WHERE Id=?", (sid,)).fetchone()
    out.append(f"\n### S{sid} {s[0]}  key={s[1]}  cv={s[2]}  held {lo}-{hi}")
    folders = collections.Counter(os.path.dirname(r[2])[len(P):] if r[2].startswith(P) else os.path.dirname(r[2]) for r in rs if r[0] < a.first_new_id)
    for f, n in folders.most_common(4): out.append(f"   held folder x{n}: {f}")
    yrs = collections.defaultdict(list)
    for r in old: yrs[r[3]].append(("C" if r[5] else "") + str(r[4] or "-"))
    out.append("   held: " + "; ".join(f"{y}:{','.join(v[:8])}{'…' if len(v) > 8 else ''}" for y, v in sorted(yrs.items()))[:600])
    for r in sorted(new, key=lambda r: r[2]):
        out.append(f"   NEW {r[0]} y{r[3]} #{r[4]}{' C' if r[5] else ''} key={r[6]}  {r[2][len(P):] if r[2].startswith(P) else r[2]}")
open(a.out, "w", encoding="utf-8").write("\n".join(out))
print(len([l for l in out if l.startswith("\n###")]), "shelves")
