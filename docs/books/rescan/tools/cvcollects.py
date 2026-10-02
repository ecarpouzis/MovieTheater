"""What does a collected edition collect? Look it up in the LOCAL ComicVine rip (no network).
Finds CV volumes whose name contains every given word (cvref.db), then prints each issue's description
from the rip's raw API JSON, trimmed to the sentences that name what it collects.
usage: python cvcollects.py "sonic the hedgehog" [--year 2018] [--issue 17] [--max 6]
"""
import argparse, html, json, re, sqlite3

ARC = "data/books/archive/mybooks/"
ap = argparse.ArgumentParser()
ap.add_argument("name"); ap.add_argument("--year", type=int); ap.add_argument("--issue"); ap.add_argument("--max", type=int, default=6)
a = ap.parse_args()
ref = sqlite3.connect(f"file:{ARC}cvref.db?mode=ro", uri=True)
rip = sqlite3.connect(f"file:{ARC}comicdb_comicvine_20260122.db?mode=ro", uri=True)
words = [w for w in re.split(r"\W+", a.name.lower()) if w]
q = "select volId,name,year,issueCount,publisherName from cv_vol where " + " and ".join("lower(name) like ?" for _ in words)
args = [f"%{w}%" for w in words]
if a.year: q += " and year between ? and ?"; args += [a.year - 1, a.year + 1]
vols = ref.execute(q + " order by year limit 40", args).fetchall()
for v in vols[: a.max]:
    print(f"== CV {v[0]}  {v[1]} ({v[2]}) issues={v[3]} {v[4]}")
    iq = "select issueId, number, name, coverDate from cv_iss where volId=?"
    ia = [v[0]]
    if a.issue: iq += " and number=?"; ia.append(a.issue)
    for iid, num, nm, cd in ref.execute(iq + " order by numKey", ia).fetchall()[:30]:
        raw = rip.execute("select raw_api_response from cv_issue where id=?", (iid,)).fetchone() or \
              rip.execute("select raw_api_response from cv_issues where id=?", (iid,)).fetchone()
        desc = ""
        if raw:
            try:
                d = json.loads(raw[0]); d = d.get("results", d)
                txt = html.unescape(re.sub(r"<[^>]+>", " ", (d.get("description") or "") + " " + (d.get("deck") or "")))
                hits = re.findall(r"[^.]*\b(?:collect|reprint|contain|issues?|#\d)[^.]*\.", txt, re.I)
                desc = " ".join(h.strip() for h in hits)[:400]
            except Exception as e:
                desc = f"(unparsed: {e})"
        print(f"   #{num} {nm or ''} [{cd}] {desc}")
if not vols: print("no CV volume matches")
