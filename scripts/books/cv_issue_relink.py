"""Re-point ComicVine ISSUE links that sit in the wrong volume.

v1's per-file matcher linked issue files by issue NUMBER inside a title-chosen volume, and often chose another
decade's volume of the same title (Aquaman 1994 #5 -> Aquaman 1962 #5). The identity pass fixed the SHELF
(Series.CvVolumeId); the item links still point into the old volume (16,701 found by cv_credits_import.py).

A link is re-pointed to the shelf volume's issue with the same number ONLY when every guard holds:
  * the link is automatic (Status Matched) — a Manual link is an identity decision and is never touched;
  * the item is a single issue (ComicDetail.IsCollection = 0) with a parsed IssueNo;
  * the OLD volume has the same normalized title as the shelf's volume — the wrong-decade error exactly; a different
    title (an annual, a special, a crossover issue filed with the run) is residue whose own volume may be right;
  * exactly ONE issue of the shelf's volume carries that number (CvIssue, filled from the rip);
  * when the filename/parse has a year and the new issue a cover year, they are within one year.
Anything else is left alone and counted. Dry run by default (verdict CSV); --apply (after a backup) rewrites
ProviderKey/SecondaryKey/Method and appends every changed row, whole, to the undo JSONL first. Chunked by Item.Id
(--after / --limit), idempotent (a re-pointed link is in the shelf volume and no longer qualifies).
usage: python scripts/books/cv_issue_relink.py [--apply] [--after ID] [--limit 20000]
"""
import argparse, collections, csv, json, os, re, sqlite3, unicodedata

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DB = os.path.join(ROOT, "data", "books", "v2", "books.db")
RIP = os.path.join(ROOT, "data", "books", "archive", "mybooks", "comicdb_comicvine_20260122.db")
OUT = os.path.join(ROOT, "docs", "books", "ratings", "cv-issue-relink.csv")
UNDO = os.path.join(ROOT, "docs", "books", "ratings", "cv-issue-relink.undo.jsonl")

ap = argparse.ArgumentParser(); ap.add_argument("--apply", action="store_true")
ap.add_argument("--after", type=int, default=0); ap.add_argument("--limit", type=int, default=20000)
a = ap.parse_args()
c = sqlite3.connect(DB) if a.apply else sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
R = sqlite3.connect(f"file:{RIP}?mode=ro", uri=True)

def tnorm(s):
    s = unicodedata.normalize("NFKD", s or "").encode("ascii", "ignore").decode().lower()
    s = re.sub(r"^the\s+|,\s*the$", "", s.strip())
    return re.sub(r"[^a-z0-9]", "", s)
def inorm(n):
    n = (n or "").strip().lower().lstrip("#")
    try: f = float(n); return str(int(f)) if f == int(f) else str(f)
    except ValueError: return n

vol_name = {}
def volname(v):
    if v not in vol_name:
        r = c.execute("SELECT Name FROM CvVolume WHERE Id=?", (v,)).fetchone()
        vol_name[v] = r[0] if r else None
    return vol_name[v]
vol_issues = {}
def issues(v):
    if v not in vol_issues:
        m = collections.defaultdict(list)
        for iid, num, cover in c.execute("SELECT Id, IssueNumber, CoverDate FROM CvIssue WHERE VolumeId=?", (v,)):
            m[inorm(num)].append((iid, cover))
        vol_issues[v] = m
    return vol_issues[v]

total, moves, after = collections.Counter(), [], a.after
while True:
    rows = c.execute("""SELECT i.Id, i.FileName, l.ProviderKey, s.CvVolumeId, d.IssueNo, d.Year, coalesce(d.IsCollection,0), l.Status
        FROM Item i JOIN ItemProviderLink l ON l.ItemId=i.Id AND l.Provider=0 AND l.Status IN (1,5) AND l.ProviderKey IS NOT NULL
        JOIN Series s ON s.Id=i.SeriesId LEFT JOIN ComicDetail d ON d.ItemId=i.Id
        WHERE i.Kind=0 AND i.IsExcluded=0 AND s.CvVolumeId IS NOT NULL AND i.Id > ? ORDER BY i.Id LIMIT ?""", (after, a.limit)).fetchall()
    if not rows: break
    cnt = collections.Counter()
    for iid, fn, key, shelf_cv, no, year, coll, status in rows:
        try: old_issue = int(key)
        except ValueError: continue
        raw = R.execute("SELECT raw_api_response FROM cv_issue WHERE id=?", (old_issue,)).fetchone()
        old_vol = None
        if raw:
            d = json.loads(raw[0]); d = d.get("results", d); old_vol = (d.get("volume") or {}).get("id")
        else:
            r = c.execute("SELECT VolumeId FROM CvIssue WHERE Id=?", (old_issue,)).fetchone(); old_vol = r[0] if r else None
        if old_vol is None or old_vol == shelf_cv: continue
        cnt["wrong-volume links"] += 1
        if status == 5: cnt["kept: manual link"] += 1; continue
        if coll: cnt["kept: collection"] += 1; continue
        if not no: cnt["kept: no issue number"] += 1; continue
        if tnorm(volname(old_vol)) != tnorm(volname(shelf_cv)): cnt["kept: old volume is another title (residue)"] += 1; continue
        cands = issues(shelf_cv).get(inorm(no), [])
        if len(cands) != 1: cnt["kept: %s issue #%s in the shelf volume" % ("no" if not cands else "several", "n")] += 1; continue
        new_issue, cover = cands[0]
        cy = int(cover[:4]) if cover and cover[:4].isdigit() else None
        if year and cy and abs(int(year) - cy) > 1: cnt["kept: year disagrees"] += 1; continue
        cnt["RELINK"] += 1
        moves.append((iid, old_issue, old_vol, new_issue, shelf_cv, no, fn))
    after = rows[-1][0]; total.update(cnt)
    print(f"through item {after}: {dict(cnt)}", flush=True)

if moves:   # a run that moves nothing never overwrites the last run's record
  with open(OUT, "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f); w.writerow(["itemId", "oldIssue", "oldVolume", "newIssue", "shelfVolume", "issueNo", "fileName"]); w.writerows(moves)
if a.apply and moves:
    cols = [r[1] for r in c.execute("PRAGMA table_info(ItemProviderLink)")]
    with open(UNDO, "a", encoding="utf-8") as u:
        for m in moves:
            for r in c.execute("SELECT * FROM ItemProviderLink WHERE ItemId=? AND Provider=0", (m[0],)):
                u.write(json.dumps(dict(zip(cols, r)), default=str) + "\n")
    with c:
        for iid, _, _, new_issue, shelf_cv, _, _ in moves:
            c.execute("""UPDATE ItemProviderLink SET ProviderKey=?, SecondaryKey=?, Method='identity-volume-relink'
                WHERE ItemId=? AND Provider=0 AND Status=1""", (str(new_issue), str(shelf_cv), iid))
print("TOTAL", dict(total), "->", OUT, "(APPLIED)" if a.apply else "(dry run)")
