"""Supersede lines for shelves whose current insight is NOT the rating pass's answer (docs/books/ratings/outranked.tsv):
an older row with a higher claimed confidence (a pre-pass Opus row describing another run, or the pass's own first
answer judged from wrong LOCG credits) outranks the correction. Each line copies the pass's best answer for the shelf
(its correction row if any, else its pass row) with `"supersedes": true`, so books-insight-import flags the shelf's
earlier rows superseded and InsightCurrency buries them — nothing is deleted.

Credits are re-vetted, never carried from the outranking row: ComicInfo/ComicVine credits, else the answer's names
only if ComicVine's people list for the volume confirms one of them, else none.
Idempotent (sourceKey rating-pass-2026-10c:<id>). Writes JSONL only; land it with books-insight-import.
usage: python scripts/books/rating_supersede.py [--out docs/books/ratings/import/SU-0001.jsonl]
"""
import argparse, collections, json, os, re, sqlite3, unicodedata

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
DIR = os.path.join(ROOT, "docs", "books", "ratings")
DB = os.path.join(ROOT, "data", "books", "v2", "books.db")
RIP = os.path.join(ROOT, "data", "books", "archive", "mybooks", "comicdb_comicvine_20260122.db")
MODEL = "claude-opus-5-5-identity"
CONF = {3: "high", 2: "medium", 1: "low"}

ap = argparse.ArgumentParser(); ap.add_argument("--out", default=os.path.join(DIR, "import", "SU-0001.jsonl"))
a = ap.parse_args()
c = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
R = sqlite3.connect(f"file:{RIP}?mode=ro", uri=True)

def norm(n):
    n = unicodedata.normalize("NFKD", n or "").encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z]", "", n.lower())
def surname(n):
    parts = re.sub(r"\(.*?\)", "", n or "").split()
    return norm(parts[-1]) if parts else ""
def cv_people(sid):
    cv = c.execute("SELECT CvVolumeId FROM Series WHERE Id=?", (sid,)).fetchone()
    if not cv or not cv[0]: return None
    row = R.execute("SELECT raw_api_response FROM cv_volume WHERE id=?", (cv[0],)).fetchone()
    if not row: return None
    d = json.loads(row[0]); d = d.get("results", d); ppl = d.get("people") or []
    return ({norm(p.get("name")) for p in ppl} | {"~" + surname(p.get("name")) for p in ppl}) if ppl else None
def trusted(sid, k=2):
    w = collections.Counter(); ar = collections.Counter()
    for role, name in c.execute("""SELECT ic.Role, ic.Name FROM ItemCredit ic JOIN Item i ON i.Id=ic.ItemId
            WHERE i.SeriesId=? AND i.IsExcluded=0 AND ic.Source IN (0, 1)""", (sid,)):
        r = (role or "").lower()
        if r in ("writer", "author", "story", "plot", "script"): w[name] += 1
        elif r in ("penciller", "artist", "pencils", "penciler", "illustrator", "art"): ar[name] += 1
    return ", ".join(n for n, _ in w.most_common(k)) or None, ", ".join(n for n, _ in ar.most_common(k)) or None
def vet(names, people):
    if not names or people is None: return None          # unconfirmable here = none (the outranking row was wrong)
    hit = any(norm(n) in people or "~" + surname(n) in people for n in (x.strip() for x in names.split(",")))
    return names if hit else None

ids = [int(l.split("\t")[0]) for l in open(os.path.join(DIR, "outranked.tsv"), encoding="utf-8") if l[:1].isdigit()]
out, counts = [], collections.Counter()
for sid in ids:
    row = c.execute(f"""SELECT Id, Confidence, Recognized, Rating, Synopsis, Author, Artist, YearBegin, YearEnd FROM Insight
        WHERE SubjectKind=1 AND SubjectId=? AND ModelId='{MODEL}' AND SourceKey LIKE 'rating-pass-2026-10%'
        ORDER BY (SourceKey LIKE 'rating-pass-2026-10b:%') DESC, Id DESC LIMIT 1""", (sid,)).fetchone()
    if not row: counts["no pass row"] += 1; continue
    iid, conf, rec, rating, syn, au, ar, y0, y1 = row
    tw, ta = trusted(sid); ppl = cv_people(sid)
    author = tw or vet(au, ppl); artist = ta or vet(ar, ppl)
    tags = collections.defaultdict(list)
    for cat, v in c.execute("SELECT Category, Value FROM InsightTag WHERE InsightId=? ORDER BY Category, Value", (iid,)): tags[cat].append(v)
    counts["lines"] += 1
    if (author or None) != (au or None): counts["author changed"] += 1
    out.append({"subject": "series", "id": sid, "model": MODEL, "confidence": CONF.get(conf, "low"), "recognized": bool(rec),
                "rating": rating, "synopsis": syn, "author": author, "artist": artist, "yearBegin": y0, "yearEnd": y1,
                "tags": tags, "sourceKey": f"rating-pass-2026-10c:{sid}", "supersedes": True})
with open(a.out, "w", encoding="utf-8") as f:
    for l in out: f.write(json.dumps(l, ensure_ascii=False) + "\n")
print(dict(counts), "->", a.out)
