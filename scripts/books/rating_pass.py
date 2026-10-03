"""The comic shelf rating pass (2026-10): a model re-judges every comic shelf's insight with what the identity pass
established, and the verdicts become NEW insight rows through `books-insight-import` (append-only; nothing is
overwritten). Rubric: docs/books/ratings/RUBRIC.md.

Chunked, resumable, idempotent (the owner's bulk-job rule):
  --emit N        write the next batch of N not-yet-emitted shelves (largest first) to docs/books/ratings/batches/
                  B-####.txt; a shelf in ANY batch file is never emitted again, so a kill loses nothing.
  --apply FILE    turn an answers file (one JSON line per shelf, the rubric's format) into books-insight-import JSONL
                  at docs/books/ratings/import/<name>.jsonl — the current row's synopsis, credits, years and tags are
                  carried forward, the answer's rating/confidence/audience/awards (and `g` = genre) applied; a `stale`
                  answer drops the carried PROSE — synopsis, credits, years — while the tags stay (credits then come
                  from the files, never from the model).
  --status        emitted / answered / imported counts and what remains.
usage: python scripts/books/rating_pass.py --emit 150 | --apply docs/books/ratings/answers/B-0001.jsonl | --status
"""
import argparse, collections, glob, html, json, os, re, sqlite3, sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DB = os.path.join(ROOT, "data", "books", "v2", "books.db")
LEGS = os.path.join(ROOT, "data", "books", "v2", "books-legs.db")
DIR = os.path.join(ROOT, "docs", "books", "ratings")
MODEL = "claude-opus-5-5-identity"          # rank 3 (Transforms.ModelRank: *opus*), the suffix records the method
CONF = {"H": "high", "M": "medium", "L": "low"}
AUD = {"all-ages", "teen", "mature", "adult"}
VOCAB = {"genre", "theme", "tone", "setting", "era", "audience", "character-focus", "award", "publisher-context"}  # InsightImportService.TagCategories

ap = argparse.ArgumentParser(); ap.add_argument("--emit", type=int); ap.add_argument("--apply"); ap.add_argument("--status", action="store_true")
# corrections: --emit-ids <file of shelf ids> --name C-0001 writes a correction batch (trusted credits only); --apply with
# --tag b lands it under a NEW source key, carrying prose/tags from the pass's own row and credits from the pre-pass row
ap.add_argument("--emit-ids"); ap.add_argument("--name"); ap.add_argument("--tag", default=""); ap.add_argument("--out")
a = ap.parse_args()
c = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
for d in ("batches", "answers", "import"): os.makedirs(os.path.join(DIR, d), exist_ok=True)

def emitted():
    ids = set()
    for f in glob.glob(os.path.join(DIR, "batches", "B-*.txt")):
        for l in open(f, encoding="utf-8"):
            m = re.match(r"^S(\d+) \|", l)
            if m: ids.add(int(m.group(1)))
    return ids

def prior(sid):
    """The best pre-pass insight row (the one this pass superseded) — for a correction's credits."""
    return c.execute("""SELECT Author, Artist FROM Insight WHERE SubjectKind=1 AND SubjectId=? AND ModelId<>?
        ORDER BY Rank DESC, Confidence DESC, GeneratedAt DESC, Id DESC LIMIT 1""", (sid, MODEL)).fetchone()

def current(sid):
    n = c.execute("""SELECT Id, ModelId, Confidence, Rating, Synopsis, Author, Artist, YearBegin, YearEnd, Recognized
        FROM Insight WHERE SubjectKind=1 AND SubjectId=? AND IsCurrent=1""", (sid,)).fetchone()
    tags = collections.defaultdict(list)
    if n:
        for cat, v in c.execute("SELECT Category, Value FROM InsightTag WHERE InsightId=? ORDER BY Category, Value", (n[0],)): tags[cat].append(v)
    return n, tags

def credits(sid, k=3, trusted_only=False):
    """The shelf's most-credited writers/artists. `trusted_only` reads ComicInfo (the file's own tags) and ComicVine
    credits only: LOCG's automatic item links are wrong often enough (16 % disagree with ComicInfo where both exist;
    Captain America 2013's trades carried Stern/Byrne) that an insight's Author must never be filled from them."""
    w = collections.Counter(); ar = collections.Counter()
    src = "AND ic.Source IN (0, 1)" if trusted_only else ""
    for role, name in c.execute(f"""SELECT ic.Role, ic.Name FROM ItemCredit ic JOIN Item i ON i.Id=ic.ItemId
            WHERE i.SeriesId=? AND i.IsExcluded=0 {src}""", (sid,)):
        r = (role or "").lower()
        if r in ("writer", "author", "story", "plot", "script"): w[name] += 1
        elif r in ("penciller", "artist", "pencils", "penciler", "illustrator", "art"): ar[name] += 1
    return [n for n, _ in w.most_common(k)], [n for n, _ in ar.most_common(k)]

if a.status:
    em = emitted()
    ans = sum(1 for f in glob.glob(os.path.join(DIR, "answers", "*.jsonl")) for l in open(f, encoding="utf-8") if l.strip().startswith("{"))
    imp = sum(1 for f in glob.glob(os.path.join(DIR, "import", "*.jsonl")) for l in open(f, encoding="utf-8") if l.strip())
    total = c.execute("SELECT count(DISTINCT i.SeriesId) FROM Item i WHERE i.IsExcluded=0 AND i.Kind=0 AND i.SeriesId IS NOT NULL").fetchone()[0]
    landed = c.execute("SELECT count(DISTINCT SubjectId) FROM Insight WHERE SubjectKind=1 AND ModelId=?", (MODEL,)).fetchone()[0]
    print({"shelves": total, "emitted": len(em), "answered": ans, "import_lines": imp, "landed_in_db": landed, "remaining_to_emit": total - len(em)})
    sys.exit(0)


SHELF_SQL = """SELECT s.Id, coalesce(s.DisplayNameOverride, s.Name), s.YearStart, s.YearEnd, s.CvVolumeId, p.Name,
        count(i.Id), sum(coalesce(d.IsCollection,0)), min(cast(nullif(d.IssueNo,'') AS REAL)), max(cast(nullif(d.IssueNo,'') AS REAL))
    FROM Series s JOIN Item i ON i.SeriesId=s.Id AND i.IsExcluded=0 AND i.Kind=0
    LEFT JOIN ComicDetail d ON d.ItemId=i.Id LEFT JOIN Publisher p ON p.Id=s.PublisherId
    {where} GROUP BY s.Id ORDER BY count(i.Id) DESC, s.Id"""

def packet(row, L, trusted=False):
    sid, name, y0, y1, cv, pub, files, colls, lo, hi = row
    cvl = ""
    if cv:
        v = c.execute("SELECT Name, StartYear, PublisherName, CountOfIssues, Deck FROM CvVolume WHERE Id=?", (cv,)).fetchone()
        desc = L.execute("SELECT Description FROM CvVolumeDescription WHERE CvVolumeId=?", (cv,)).fetchone()
        txt = (v[4] if v and v[4] else "") or (re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", desc[0]))) if desc and desc[0] else "")
        cvl = f"CV {cv}: {v[0]} ({v[1]}, {v[2]}, {v[3]} iss)" + (f" — {txt[:220]}" if txt else "") if v else f"CV {cv}"
    w, ar = credits(sid, trusted_only=trusted)
    cur, tags = current(sid)
    conf = {3: "H", 2: "M", 1: "L", 0: "?"}
    curl = (f"cur: {cur[1]} {conf.get(cur[2], '?')} r={cur[3]} aud={','.join(tags.get('audience', [])) or '-'} "
            f"genre={','.join(tags.get('genre', [])[:4]) or '-'} aw={','.join(tags.get('award', [])) or '-'}"
            + (f" syn: {re.sub(chr(10), ' ', cur[4])[:140]}" if cur[4] else "")) if cur else "cur: none"
    iss = f"#{lo:g}-{hi:g}" if lo is not None and hi is not None else "-"
    return (f"S{sid} | {name} | {pub or '?'} {y0 or '?'}-{y1 or ''} | {files} files ({colls} coll) {iss} | "
            f"W: {', '.join(w) or '-'} | A: {', '.join(ar) or '-'} | {cvl or 'no CV'} | {curl}")

if a.emit:
    em = emitted()
    rows = c.execute(SHELF_SQL.format(where="")).fetchall()
    todo = [r for r in rows if r[0] not in em][:a.emit]
    if not todo: print("nothing left to emit"); sys.exit(0)
    L = sqlite3.connect(f"file:{LEGS}?mode=ro", uri=True)
    n = 1 + max([int(re.search(r"B-(\d+)", f).group(1)) for f in glob.glob(os.path.join(DIR, "batches", "B-*.txt"))] or [0])
    path = os.path.join(DIR, "batches", f"B-{n:04d}.txt")
    out = [f"# B-{n:04d} — {len(todo)} shelves. Rubric: docs/books/ratings/RUBRIC.md. Answer one JSON line per shelf into "
           f"docs/books/ratings/answers/B-{n:04d}.jsonl", ""]
    out += [packet(r, L) for r in todo]
    open(path, "w", encoding="utf-8").write("\n".join(out) + "\n")
    print({"batch": os.path.basename(path), "shelves": len(todo), "files_covered": sum(t[6] for t in todo),
           "remaining_to_emit": len(rows) - len(em) - len(todo)})
    sys.exit(0)

if a.emit_ids:
    # a CORRECTION batch: named shelves re-packeted with TRUSTED credits only (ComicInfo / ComicVine), the pass's own
    # current row shown as `cur`, written to batches/<name>.txt (names outside the B- series, so --emit never sees them)
    if not a.name or a.name.startswith("B-"): sys.exit("--name is required and must not start with B-")
    ids = [int(x) for x in re.findall(r"\d+", open(a.emit_ids, encoding="utf-8").read())]
    rows = {r[0]: r for r in c.execute(SHELF_SQL.format(where=f"WHERE s.Id IN ({','.join(map(str, ids)) or '0'})")).fetchall()}
    L = sqlite3.connect(f"file:{LEGS}?mode=ro", uri=True)
    path = os.path.join(DIR, "batches", f"{a.name}.txt")
    out = [f"# {a.name} — CORRECTION batch, {len(rows)} shelves. Rubric: docs/books/ratings/RUBRIC.md. The credits shown are "
           f"TRUSTED ones only (the files' own ComicInfo / ComicVine); `cur` is this pass's earlier answer. Answer into "
           f"docs/books/ratings/answers/{a.name}.jsonl", ""]
    out += [packet(rows[i], L, trusted=True) for i in ids if i in rows]
    open(path, "w", encoding="utf-8").write("\n".join(out) + "\n")
    print({"batch": os.path.basename(path), "shelves": len(rows), "missing": [i for i in ids if i not in rows]})
    sys.exit(0)

if a.apply:
    name = os.path.splitext(os.path.basename(a.apply))[0]
    lines, bad = [], []
    for k, raw in enumerate(open(a.apply, encoding="utf-8"), 1):
        raw = raw.strip()
        if not raw.startswith("{"): continue
        try: ans = json.loads(raw)
        except json.JSONDecodeError: bad.append((k, "unparseable")); continue
        sid = int(ans["id"])
        if not c.execute("SELECT 1 FROM Series WHERE Id=?", (sid,)).fetchone(): bad.append((k, f"no shelf {sid}")); continue
        cur, tags = current(sid)
        stale = bool(ans.get("stale"))
        aud = [x for x in ans.get("aud") or [] if x in AUD]
        if ans.get("aud") and not aud: bad.append((k, f"bad audience {ans.get('aud')}")); continue
        conf = CONF.get(str(ans.get("c", "")).upper())
        if not conf: bad.append((k, f"bad confidence {ans.get('c')}")); continue
        r = ans.get("r")
        if r is not None and not (1 <= int(r) <= 100): bad.append((k, f"bad rating {r}")); continue
        # stale drops the carried PROSE (synopsis, credits, years) — the tags are judged separately: `g` replaces genre
        # only the importer's closed vocabulary carries forward (a few old rows hold art-style/format/... tags it rejects)
        newtags = {cat: list(v) for cat, v in tags.items() if cat in VOCAB}
        if ans.get("g"): newtags["genre"] = [str(x) for x in ans["g"]]
        if aud: newtags["audience"] = aud
        if "awr" in ans:  # REPLACE the awards (a correction: the carried ones belonged to another run)
            newtags.pop("award", None)
            if ans["awr"]: newtags["award"] = [str(x) for x in ans["awr"]]
        for w in ans.get("aw") or []:
            newtags.setdefault("award", [])
            if w not in newtags["award"]: newtags["award"].append(w)
        wr, art = credits(sid, 2, trusted_only=True)
        # credits: a correction (--tag) takes them from the PRE-pass row — the pass's own row may hold LOCG-derived names
        base = prior(sid) if a.tag else (cur[5], cur[6]) if cur else (None, None)
        author = ((base[0] if base else None) if not stale else None) or (", ".join(wr) or None)
        artist = ((base[1] if base else None) if not stale else None) or (", ".join(art) or None)
        syn = ans.get("syn") or (cur[4] if cur and not stale else None)
        lines.append({"subject": "series", "id": sid, "model": MODEL, "confidence": conf,
                      "recognized": (bool(cur[9]) if cur and not stale else conf == "high"),
                      "rating": int(r) if r is not None else None, "synopsis": syn, "author": author, "artist": artist,
                      "yearBegin": cur[7] if cur and not stale else None, "yearEnd": cur[8] if cur and not stale else None,
                      "tags": newtags, "sourceKey": f"rating-pass-2026-10{a.tag}:{sid}"})
    outp = a.out or os.path.join(DIR, "import", f"{name}.jsonl")
    with open(outp, "w", encoding="utf-8") as f:
        for l in lines: f.write(json.dumps(l, ensure_ascii=False) + "\n")
    print({"answers": name, "import_lines": len(lines), "rejected": len(bad), "out": outp})
    for k, why in bad: print(f"   line {k}: {why}")
