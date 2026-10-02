"""THE GATE before any removed file is called `gone`: search the WHOLE live library — not just the new files — for a
collected edition that could hold it, and print the candidates with what their own records say they collect.

Why this exists (2026-10-02): the rescan's first verdicts looked for a replacement only among NEW files, by name.
72 removals were reported lost; reading the library showed most were held all along by collections already on the
share under other names — American Flagg Vol. 01 (#1-12), Bone Orchard Mythos Vol. 03 - Tenement (#1-10), Moonshine
Complete Collection, Middlewest Complete Tale, Livewire Deluxe (#1-12), Death or Glory Prestige, Tomb Raider Omnibus
1-2, Astro City Metrobooks, Hip Hop Family Tree Omnibus, Gunslinger Spawn Vol. 06-07 and Red Sonja Vol. 02 (read
from the books). A removal is `gone` ONLY when this prints no candidate, or every candidate has been read and
refused.

Candidates for a removed item (any of):
  * a live collection on the item's own shelf, its SeriesTitle (Series.TitleId) or its franchise, or in its folder /
    parent folder;
  * a live collection anywhere whose filename carries the item's title stem (all significant words);
  * a live judged span (CollectedEditionSpan Source=3) on the item's shelf covering its issue number.
For each candidate the collects clause from its ComicInfo, its ComicVine/GCD link, and its trade record (trade_probe's
title + volume lookup) is printed when one exists. Read-only.
usage: python removal_coverage.py --run docs/books/rescan/20261002 [--verdicts gone,contained?] [--items 1,2] [--out tsv]
"""
import argparse, collections, csv, os, re, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--run"); ap.add_argument("--db", default="data/books/v2/books.db")
ap.add_argument("--verdicts", default="gone"); ap.add_argument("--items"); ap.add_argument("--out")
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
P = "\\\\Library\\Public\\5 - Comics\\"
STOP = {"the", "a", "an", "of", "and", "vol", "volume", "v", "book", "digital", "empire", "tpb", "hc", "edition", "collection",
        "complete", "deluxe", "omnibus", "c2c", "issue", "annual", "special", "one", "shot"}

def stem(fn):
    t = re.sub(r"\.(cbz|cbr|pdf|zip|rar|7z|epub)$", "", fn, flags=re.I)
    t = re.sub(r"\((?:[^()]|\([^()]*\))*\)|\[[^\]]*\]", " ", t)
    t = re.sub(r"\b(?:v|vol\.?|volume|book|#)\s*\d+\b|\b\d{1,4}(?:\.\d)?\b", " ", t, flags=re.I)
    return [w for w in re.findall(r"[a-z0-9']+", t.lower().replace("&", " and ")) if w not in STOP and len(w) > 1]

if a.items:
    todo = [(int(x), "?", "") for x in a.items.split(",")]
else:
    want = set(a.verdicts.split(","))
    todo = [(int(r["ItemId"]), r["Verdict"], r["OldPath"]) for r in csv.DictReader(open(os.path.join(a.run, "replaced.tsv"), encoding="utf-8"), delimiter="\t") if r["Verdict"] in want]

live = c.execute("""SELECT i.Id, i.SeriesId, i.FolderId, i.Path, i.FileName, i.PageCount, coalesce(d.IsCollection,0), s.TitleId, s.Franchise
    FROM Item i LEFT JOIN ComicDetail d ON d.ItemId=i.Id LEFT JOIN Series s ON s.Id=i.SeriesId
    WHERE i.RootId=1 AND i.IsExcluded=0""").fetchall()
colls = [r for r in live if r[6] or (r[5] or 0) >= 80]
by_tok = collections.defaultdict(set)
for r in colls:
    for w in set(stem(r[4]) + stem(os.path.basename(os.path.dirname(r[3])))): by_tok[w].add(r[0])
byid = {r[0]: r for r in colls}
emb = lambda iid: " ".join(x or "" for x in (c.execute("SELECT Summary, Notes FROM ComicEmbedded WHERE ItemId=?", (iid,)).fetchone() or ()))
RX = re.compile(r"(?:Collect(?:s|ing|ed)?|Reprint(?:s|ing)?|Contains|originally published[^.]{0,40}as)\b[^.]{0,200}", re.I)

out = []
for iid, verdict, oldpath in todo:
    row = c.execute("""SELECT i.SeriesId, i.FolderId, i.Path, i.FileName, d.IssueNo, s.TitleId, s.Franchise FROM Item i
        LEFT JOIN ComicDetail d ON d.ItemId=i.Id LEFT JOIN Series s ON s.Id=i.SeriesId WHERE i.Id=?""", (iid,)).fetchone()
    if not row: continue
    sid, fid, path, fn, no, tid, fr = row
    toks = stem(fn)
    cand = {}
    for r in colls:
        why = []
        if r[1] == sid and sid: why.append("same shelf")
        elif tid and r[7] == tid: why.append("same title tier")
        elif fr and r[8] == fr: why.append("same franchise")
        d, rd = os.path.dirname(path), os.path.dirname(r[3])
        if rd == d or rd == os.path.dirname(d) or os.path.dirname(rd) == d: why.append("folder")
        if why and ("same franchise" not in why or toks and set(toks) <= set(stem(r[4]) + stem(os.path.basename(rd)))):
            cand[r[0]] = why
    if toks:
        hit = set.intersection(*[by_tok.get(w, set()) for w in toks]) if all(w in by_tok for w in toks) else set()
        for h in hit: cand.setdefault(h, []).append("title stem")
    span = None
    try:
        n = float(no) if no else None
    except ValueError: n = None
    if n is not None and sid:
        span = c.execute("""SELECT i.FileName, sp.IssueStart, sp.IssueEnd FROM CollectedEditionSpan sp JOIN Item i ON i.Id=sp.ItemId
            WHERE i.SeriesId=? AND i.IsExcluded=0 AND sp.Source=3 AND sp.IssueStart<=? AND sp.IssueEnd>=?""", (sid, n, n)).fetchone()
    lines = []
    for h, why in sorted(cand.items(), key=lambda kv: byid[kv[0]][3]):
        r = byid[h]
        m = RX.search(emb(h))
        lines.append(f"{h} {r[5]}pp [{','.join(why)}] {r[3][len(P):][-110:]}" + (f"  || {m.group(0).strip()[:160]}" if m else ""))
    status = "COVERED (judged span)" if span else ("CANDIDATES - read before deciding" if lines else "NO CANDIDATE")
    out.append((iid, verdict, status, fn, (f"{span[0]} #{span[1]:g}-{span[2]:g}" if span else ""), " | ".join(lines[:8])))
    print(f"\n[{iid}] {verdict} -> {status}: {path[len(P):][-120:]}")
    if span: print(f"    span: {span[0]} #{span[1]:g}-{span[2]:g}")
    for l in lines[:8]: print("    " + l)
    if len(lines) > 8: print(f"    … {len(lines) - 8} more")
if a.out:
    with open(a.out, "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f, delimiter="\t", lineterminator="\n"); w.writerow(["ItemId", "Verdict", "Status", "File", "Span", "Candidates"]); w.writerows(out)
print("\n", collections.Counter(o[2] for o in out))
