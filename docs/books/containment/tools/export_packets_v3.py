"""The complete evidence for a series. Nothing reduced, nothing summarised away.

v2 carried `heldIssues: [1,2,3,4,5,13,14,15]` — bare numbers. The answer to Baltimore was in the issue
FILENAMES ("Baltimore 016 - The Infernal Train 01 (of 03)"), which carry both the continuous library
number and the arc's own. Reducing them to numbers threw away the only evidence that settled the shelf.
So v3 reduces nothing.

Per series:
  folder paths the items actually live in (context: neighbours, conventions, batch dirs)
  EVERY item, collection and issue alike, with its full filename, path, page count, file size,
  parsed issue/volume number, and format

Per item:
  every CollectedEditionSpan from every source, with v1's note verbatim
  GCD's reprint SET (dominant series named), not a range
  LOCG forward contents (which issues this edition holds) and the LOCG ROLE
     — container-only means LOCG says it IS a collection, contained-only means it IS an issue,
       which is an identity signal independent of filename and ComicInfo
  LOCG reverse edges (which editions claim THIS item), so an issue's membership is known without
     trusting its parsed number
  the "Collects ... #N" prose off the cached LOCG page — the only source that states NON-contiguity
  v1's book-inspection result, including its deliberate "no issue range" skips
"""
import json, os, re, sqlite3, sys
from collections import defaultdict, Counter

HOT = r"F:/Work/MovieTheater/data/books/v2/books.db"
LEGS = r"F:/Work/MovieTheater/data/books/v2/books-legs.db"
GCD = r"F:/Work/MovieTheater/data/books/archive/mybooks/GrandComicsDatabase-06-01-06.db"
HTML = r"F:/Work/MovieTheater/data/books/archive/locg_cache/detail_html"
V1 = r"F:/Work/MovieTheater/docs/books/containment/v1_curation.jsonl"
PROSE_CACHE = "locg_prose_all.jsonl"
OUT = sys.argv[1] if len(sys.argv) > 1 else "packets3.jsonl"

h = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
h.execute(f"ATTACH DATABASE 'file:{LEGS}?mode=ro' AS legs")
g = sqlite3.connect(f"file:{GCD}?mode=ro", uri=True)

RX_NUM = re.compile(r"^\s*(\d{1,4})(?:\.(\d+))?\s*$")
SRC = {0: "locg", 1: "gcd", 2: "cv", 3: "curated"}
RX_PROSE = re.compile(r"(?:collect(?:s|ed|ing)?|reprints)\b[^<.]{0,240}", re.I)
RX_HASHNUM = re.compile(r"#\s*\d")


def num(s):
    if s is None:
        return None
    m = RX_NUM.match(str(s))
    return None if not m else float(m.group(1)) + (float("0." + m.group(2)) if m.group(2) else 0)


# ── provider links ────────────────────────────────────────────────────────────────────────────
links = defaultdict(dict)
for iid, prov, pk, matched, status, method in h.execute(
        "SELECT ItemId, Provider, ProviderKey, MatchedKey, Status, Method FROM ItemProviderLink"):
    if pk and str(pk).isdigit():
        links[iid][prov] = (int(pk), matched, status, method)

# ── GCD reprint sets, dominant reprinted series only ──────────────────────────────────────────
gcd_raw = defaultdict(list)
tl = list({v[3][0] for v in links.values() if 3 in v})
for i in range(0, len(tl), 900):
    chunk = ",".join(str(t) for t in tl[i:i + 900])
    for tgt, number, sname in g.execute(f"""
            SELECT rp.target_issue_id, i.number, s.name FROM gcd_reprint rp
            JOIN gcd_issue i ON i.id = rp.origin_issue_id JOIN gcd_series s ON s.id = i.series_id
            WHERE rp.target_issue_id IN ({chunk})"""):
        n = num(number)
        if n is not None:
            gcd_raw[tgt].append((sname, n))
gcd_sets = {}
for tgt, pairs in gcd_raw.items():
    dom = Counter(s for s, _ in pairs).most_common(1)[0][0]
    gcd_sets[tgt] = {"series": dom, "issues": sorted({n for s, n in pairs if s == dom}),
                     "other": sorted({s for s, _ in pairs} - {dom})[:4]}

# ── LOCG: forward contents, reverse claims, and the role each id plays ─────────────────────────
locg_fwd = defaultdict(list)     # container id -> [{issue, series, ordinal, chapter, source}]
locg_rev = defaultdict(list)     # contained id -> [{container id, series, title, format}]
as_container, as_contained = Counter(), Counter()
for cid, did, ordinal, chapter, source, isn, iss, cn, cs, ct, cf in h.execute("""
        SELECT e.ContainerLocgComicId, e.ContainedLocgComicId, e.Ordinal, e.ChapterTitle, e.Source,
               d.IssueNumber, d.SeriesName, c.LocgComicId, c.SeriesName, c.Title, c.Format
        FROM legs.LocgContainment e
        LEFT JOIN legs.LocgComicRaw d ON d.LocgComicId = e.ContainedLocgComicId
        LEFT JOIN legs.LocgComicRaw c ON c.LocgComicId = e.ContainerLocgComicId"""):
    as_container[cid] += 1
    as_contained[did] += 1
    locg_fwd[cid].append({"issue": num(isn), "raw": isn, "series": isn and iss or iss,
                          "ordinal": ordinal, "chapter": chapter, "source": source})
    locg_rev[did].append({"container": cid, "series": cs, "title": ct, "format": cf, "source": source})

# ── the "Collects ..." prose off the cached LOCG page, extracted once and cached ───────────────
prose = {}
if os.path.exists(PROSE_CACHE):
    for line in open(PROSE_CACHE, encoding="utf-8"):
        r = json.loads(line)
        prose[r["locgId"]] = r["prose"]
else:
    wanted = {v[2][0] for v in links.values() if 2 in v}
    tmp = PROSE_CACHE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as pc:
        for cid in wanted:
            p = os.path.join(HTML, f"{cid}.html")
            if not os.path.exists(p):
                continue
            try:
                html = open(p, encoding="utf-8", errors="ignore").read()
            except OSError:
                continue
            for m in RX_PROSE.findall(html):
                if RX_HASHNUM.search(m):
                    t = " ".join(m.split())[:240]
                    prose[cid] = t
                    pc.write(json.dumps({"locgId": cid, "prose": t}, ensure_ascii=False) + "\n")
                    break
    os.replace(tmp, PROSE_CACHE)   # only a COMPLETE sweep becomes the cache

# ── v1's book inspection ──────────────────────────────────────────────────────────────────────
v1 = {}
if os.path.exists(V1):
    for line in open(V1, encoding="utf-8"):
        r = json.loads(line)
        v1[r["itemId"]] = r

# ── spans ─────────────────────────────────────────────────────────────────────────────────────
spans = defaultdict(list)
for iid, src, a, b, title, ref, conf, note, contig in h.execute("""
        SELECT ItemId, Source, IssueStart, IssueEnd, EditionTitle, ProviderRef, Confidence, Note, Contiguous
        FROM CollectedEditionSpan WHERE IssueStart IS NOT NULL"""):
    spans[iid].append({"src": SRC.get(src, src), "start": a, "end": b, "title": title, "conf": conf,
                       "contiguous": bool(contig) if contig is not None else None,
                       "gold": src == 3 and not (ref or "").startswith("model:"),
                       "providerRef": ref, "note": note})


def locg_block(iid):
    lk = links.get(iid, {})
    if 2 not in lk:
        return None
    cid = lk[2][0]
    b = {"id": cid, "asContainer": as_container.get(cid, 0), "asContained": as_contained.get(cid, 0)}
    b["role"] = ("collection" if b["asContainer"] and not b["asContained"]
                 else "issue" if b["asContained"] and not b["asContainer"]
                 else "both" if b["asContainer"] else "unknown")
    if cid in locg_fwd:
        b["contains"] = locg_fwd[cid][:80]
    if cid in locg_rev:
        b["claimedBy"] = locg_rev[cid][:20]
    if cid in prose:
        b["prose"] = prose[cid]
    return b


series = h.execute("""
    SELECT DISTINCT s.Id, coalesce(s.DisplayNameOverride, s.Name), s.CanonicalKey, s.YearStart, s.YearEnd
    FROM Series s JOIN Item i ON i.SeriesId = s.Id JOIN ComicDetail cd ON cd.ItemId = i.Id
    WHERE cd.IsCollection = 1 AND s.CanonicalKey NOT LIKE 'book:%' ORDER BY s.Id""").fetchall()

n = 0
with open(OUT, "w", encoding="utf-8") as out:
    for sid, name, key, y0, y1 in series:
        rows = h.execute("""
            SELECT i.Id, i.FileName, i.Path, i.PageCount, i.FileSize, cd.IsCollection, cd.IssueNo,
                   cd.VolumeNo, cd.Year, cd.Format, cd.FormatRaw, r.ReadNumber, r.ReadTier,
                   n.ContainsCount, n.SpanLabel, n.SpanSource, n.TrackRole
            FROM Item i LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
            LEFT JOIN ReadingOrderEntry r ON r.ItemId = i.Id
            LEFT JOIN CollectionNode n ON n.ItemId = i.Id
            WHERE i.SeriesId = ? AND coalesce(i.IsExcluded,0) = 0
            ORDER BY i.Path, i.FileName""", (sid,)).fetchall()

        folders, cols, issues = Counter(), [], []
        for (iid, fn, path, pc, fs, iscol, ino, vol, yr, fmt, fraw, rnum, rtier,
             cnt, label, ssrc, role) in rows:
            folders[os.path.dirname(path or "")] += 1
            ent = {"id": iid, "file": fn, "pages": pc, "mb": round((fs or 0) / 1048576, 1),
                   "issueNo": ino, "vol": vol, "year": yr, "format": fraw or fmt,
                   "readNumber": rnum}
            lb = locg_block(iid)
            if lb:
                ent["locg"] = lb
            if iid in v1:
                ent["v1"] = v1[iid]
            if iscol:
                ent["node"] = {"contains": cnt, "label": label, "spanSource": ssrc, "trackRole": role}
                if iid in spans:
                    ent["spans"] = spans[iid]
                lk = links.get(iid, {})
                if 3 in lk and lk[3][0] in gcd_sets:
                    ent["gcd"] = dict(gcd_sets[lk[3][0]], link=lk[3][1], method=lk[3][3])
                cols.append(ent)
            else:
                if iid in spans:
                    ent["spans"] = spans[iid]
                issues.append(ent)

        if not cols:
            continue
        out.write(json.dumps({"seriesId": sid, "name": name, "key": key, "years": [y0, y1],
                              "folders": [f for f, _ in folders.most_common()],
                              "collections": cols, "issues": issues}, ensure_ascii=False) + "\n")
        n += 1

print(f"{n} series packets -> {OUT}")
print(f"   LOCG prose cached for {len(prose)} editions")
