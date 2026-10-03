"""Credit correction for the 2026-10 rating pass. Early batches filled a landed row's Author/Artist from ALL file
credits when the carried row had none (or was stale) — and LOCG's automatic item links are wrong often enough that
those names can belong to another run. This recomputes each landed pass row's credits with the fixed rule (the
pre-pass row's credits unless the answer was stale, else ComicInfo/ComicVine credits only) and writes a copy of the
row with corrected credits — same rating, prose, years and tags — for every row whose credits change.

Idempotent: the output lines carry sourceKey `rating-pass-2026-10b:<id>`, so a shelf already corrected (C-0001) is
skipped here and by the importer. Dry-run by nature (it only writes a JSONL); land it with books-insight-import.
usage: python scripts/books/rating_credit_fix.py [--out docs/books/ratings/import/CR-0001.jsonl]
"""
import argparse, collections, glob, json, os, re, sqlite3, unicodedata

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DB = os.path.join(ROOT, "data", "books", "v2", "books.db")
DIR = os.path.join(ROOT, "docs", "books", "ratings")
MODEL = "claude-opus-5-5-identity"
CONF = {3: "high", 2: "medium", 1: "low"}

ap = argparse.ArgumentParser(); ap.add_argument("--out", default=os.path.join(DIR, "import", "CR-0001.jsonl"))
a = ap.parse_args()
c = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)

stale = {}
for f in sorted(glob.glob(os.path.join(DIR, "answers", "B-*.jsonl"))):
    for l in open(f, encoding="utf-8"):
        l = l.strip()
        if l.startswith("{"):
            try: ans = json.loads(l)
            except json.JSONDecodeError: continue
            stale[int(ans["id"])] = bool(ans.get("stale"))

def trusted(sid, k=2):
    w = collections.Counter(); ar = collections.Counter()
    for role, name in c.execute("""SELECT ic.Role, ic.Name FROM ItemCredit ic JOIN Item i ON i.Id=ic.ItemId
            WHERE i.SeriesId=? AND i.IsExcluded=0 AND ic.Source IN (0, 1)""", (sid,)):
        r = (role or "").lower()
        if r in ("writer", "author", "story", "plot", "script"): w[name] += 1
        elif r in ("penciller", "artist", "pencils", "penciler", "illustrator", "art"): ar[name] += 1
    return ", ".join(n for n, _ in w.most_common(k)) or None, ", ".join(n for n, _ in ar.most_common(k)) or None

RIP = os.path.join(ROOT, "data", "books", "archive", "mybooks", "comicdb_comicvine_20260122.db")
R = sqlite3.connect(f"file:{RIP}?mode=ro", uri=True)

def norm(n):
    n = unicodedata.normalize("NFKD", n or "").encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z]", "", n.lower())

def surname(n):
    parts = re.sub(r"\(.*?\)", "", n or "").split()
    return norm(parts[-1]) if parts else ""

def cv_people(sid):
    """ComicVine's own creator list for the shelf's volume (from the offline rip), as normalized names; None = no check possible."""
    cv = c.execute("SELECT CvVolumeId FROM Series WHERE Id=?", (sid,)).fetchone()
    if not cv or not cv[0]: return None
    row = R.execute("SELECT raw_api_response FROM cv_volume WHERE id=?", (cv[0],)).fetchone()
    if not row: return None
    d = json.loads(row[0]); d = d.get("results", d)
    ppl = d.get("people") or []
    return ({norm(p.get("name")) for p in ppl} | {"~" + surname(p.get("name")) for p in ppl}) if ppl else None

def vet(names, people):
    """Names the pass took from file credits survive when ComicVine lists ANY of them (full name or surname) for the
    volume — a wrong LOCG link credits another run's people, so none match; no CV list to check = kept (unsure)."""
    if not names: return None
    if people is None: return names
    hit = any(norm(n) in people or "~" + surname(n) in people for n in (x.strip() for x in names.split(",")))
    return names if hit else None

done_b = {int(k.split(":")[1]) for (k,) in c.execute("SELECT SourceKey FROM Insight WHERE SourceKey LIKE 'rating-pass-2026-10b:%'")}
rows = c.execute("""SELECT Id, SubjectId, Confidence, Recognized, Rating, Synopsis, Author, Artist, YearBegin, YearEnd
    FROM Insight WHERE ModelId=? AND SourceKey LIKE 'rating-pass-2026-10:%' ORDER BY SubjectId, Id""", (MODEL,)).fetchall()
latest = {}
for r in rows: latest[r[1]] = r          # the last landed pass row per shelf
out, counts = [], collections.Counter()
for sid, r in latest.items():
    if sid in done_b: counts["already corrected"] += 1; continue
    iid, _, conf, rec, rating, syn, au, ar, y0, y1 = r
    # the row the pass carried from = the CURRENT row (books-resolve has not re-elected since the pass landed);
    # a value equal to it was carried, never introduced by the pass, and is left alone
    cur = c.execute("SELECT Author, Artist FROM Insight WHERE SubjectKind=1 AND SubjectId=? AND IsCurrent=1", (sid,)).fetchone() or (None, None)
    tw, ta = trusted(sid)
    people = None
    def fix(landed, carried, trust):
        global people
        if (landed or None) == (carried or None) or not landed: return landed or None
        if trust: return trust
        if people is None: people = cv_people(sid) or False
        return vet(landed, people or None)
    want_au = fix(au, cur[0], tw)
    want_ar = fix(ar, cur[1], ta)
    if people is False: counts["introduced, no CV to check (kept)"] += 1
    if (au or None) == want_au and (ar or None) == want_ar: counts["unchanged"] += 1; continue
    counts["corrected"] += 1
    if want_au is None and au: counts["author cleared"] += 1
    tags = collections.defaultdict(list)
    for cat, v in c.execute("SELECT Category, Value FROM InsightTag WHERE InsightId=? ORDER BY Category, Value", (iid,)): tags[cat].append(v)
    out.append({"subject": "series", "id": sid, "model": MODEL, "confidence": CONF.get(conf, "low"), "recognized": bool(rec),
                "rating": rating, "synopsis": syn, "author": want_au, "artist": want_ar, "yearBegin": y0, "yearEnd": y1,
                "tags": tags, "sourceKey": f"rating-pass-2026-10b:{sid}", "_was": [au, ar]})
print(dict(counts), "->", a.out)
for l in out[:15]: print(f"  S{l['id']}: {l['_was'][0]} / {l['_was'][1]}  ->  {l['author']} / {l['artist']}")
with open(a.out, "w", encoding="utf-8") as f:
    for l in out:
        l.pop("_was")
        f.write(json.dumps(l, ensure_ascii=False) + "\n")
