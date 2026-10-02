"""Evidence packet for unranged collections: every source that states what a trade COLLECTS, side by side. Read-only.

Per collection (from containment_priority.tsv): its pages; the shelf's live numbered issues and the judged ranges
of its sibling editions (the volume pattern); GCD's own statement (the trade's GCD issue from the identity reading
or v1's link -> `gcd_issue.notes` "Collects …" clause + the `gcd_reprint` roll-up, via gcdnotes.Notes); the
ComicVine rip's description of the trade's CV issue ("Collects …"); the book's ComicInfo Summary/Notes clause.
The reader decides; nothing here writes a range.
usage: python range_evidence.py --run data/books/rescan/20261002 [--limit N]
"""
import argparse, csv, html, json, os, re, sqlite3, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "identity", "tools"))
import idbase, gcdnotes

RX_COLL = re.compile(r"(?:Collect(?:s|ing|ed)?|Reprint(?:s|ing)?|Contains material originally published[^.]*?as)\b[^.]{0,220}", re.I)
ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--limit", type=int, default=0)
a = ap.parse_args()
con = idbase.open_hot()
gcd = idbase.open_gcd_dump()
rip = sqlite3.connect("file:F:/Work/MovieTheater/data/books/archive/mybooks/comicdb_comicvine_20260122.db?mode=ro", uri=True)
notes = gcdnotes.Notes(gcd, con)
item_gcd = notes.item_gcd_issues()
item_cv = {r[0]: r[1] for r in con.execute("""SELECT ItemId, ProviderKey FROM ItemProviderLink WHERE Provider = 0
        AND Status IN (1,5) AND ProviderKey GLOB '[0-9]*' ORDER BY Status""")}
rows = list(csv.DictReader(open(os.path.join(a.run, "containment_priority.tsv"), encoding="utf-8"), delimiter="\t"))
if a.limit: rows = rows[:a.limit]
out = []
by_shelf = {}
for r in rows: by_shelf.setdefault(int(r["SeriesId"]), []).append(r)
for sid, rs in by_shelf.items():
    name = con.execute("SELECT coalesce(DisplayNameOverride, Name), CvVolumeId FROM Series WHERE Id=?", (sid,)).fetchone()
    sib = con.execute("""SELECT i.FileName, sp.IssueStart, sp.IssueEnd FROM CollectedEditionSpan sp JOIN Item i ON i.Id = sp.ItemId
        WHERE i.SeriesId = ? AND i.IsExcluded = 0 AND sp.Source = 3 AND sp.IssueStart IS NOT NULL ORDER BY sp.IssueStart""", (sid,)).fetchall()
    out.append(f"\n### S{sid} {name[0]}  (cv {name[1]})  live issues: {rs[0]['LiveIssues']}  [{rs[0]['IssueNumbers'][:120]}]")
    for f, s, e in sib[:14]:
        out.append(f"   judged: #{s:g}-{e:g}  {f[:80]}")
    if len(sib) > 14: out.append(f"   judged: … {len(sib) - 14} more")
    for r in rs:
        iid = int(r["ItemId"])
        out.append(f"  - {iid}  {r['File'][:100]}  [{r['Pages']}pp]")
        g = item_gcd.get(iid)
        if g:
            row = notes.issue(g)
            if row:
                m = RX_COLL.search(row["notes"] or "")
                out.append(f"      GCD {g} ({row['series']} #{row['number']}, {row['pages']}pp): {m.group(0).strip() if m else '(no collects clause)'}")
                for _s, nm, ranges, n in notes.reprints(g)[:4]:
                    out.append(f"      GCD reprints: {nm} " + ", ".join(f"#{x:g}-{y:g}" if x != y else f"#{x:g}" for x, y in ranges) + f"  ({n} stories)")
        c = item_cv.get(iid)
        if c:
            raw = rip.execute("SELECT raw_api_response FROM cv_issue WHERE id=?", (int(c),)).fetchone()
            if raw:
                try:
                    d = json.loads(raw[0]); d = d.get("results", d)
                    txt = html.unescape(re.sub(r"<[^>]+>", " ", (d.get("description") or "") + " " + (d.get("deck") or "")))
                    m = RX_COLL.search(txt)
                    out.append(f"      CV {c}: {m.group(0).strip() if m else '(no collects clause)'}")
                except Exception: pass
        emb = con.execute("SELECT Summary, Notes FROM ComicEmbedded WHERE ItemId=?", (iid,)).fetchone()
        if emb:
            m = RX_COLL.search(" ".join(x or "" for x in emb))
            if m: out.append(f"      ComicInfo: {m.group(0).strip()}")
open(os.path.join(a.run, "range_evidence.txt"), "w", encoding="utf-8").write("\n".join(out))
print("shelves", len(by_shelf), "collections", len(rows))
