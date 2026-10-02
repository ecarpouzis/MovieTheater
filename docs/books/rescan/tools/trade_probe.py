"""For collections no stored link describes: find the TRADE's own record by title + volume number and print what it
says it collects. GCD: series named like the trade (any series whose name starts with the stem, incl. 'TPB'/'Deluxe'
lines), the issue numbered like the volume (1 for a one-volume collection) -> notes' collects clause + gcd_reprint
roll-up. ComicVine rip: volumes named like the stem, the issue numbered like the volume -> description's clause.
Read-only; a reader decides.
usage: python trade_probe.py --items 246141,246144 | --todo data/books/rescan/20261002/range_todo.txt
"""
import argparse, html, json, os, re, sqlite3, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "identity", "tools"))
import idbase, gcdnotes

RX = re.compile(r"(?:Collect(?:s|ing|ed)?|Reprint(?:s|ing)?|Contains)\b[^.]{0,240}", re.I)
ap = argparse.ArgumentParser(); ap.add_argument("--items"); ap.add_argument("--todo")
a = ap.parse_args()
con = idbase.open_hot(); gcd = idbase.open_gcd_dump()
rip = sqlite3.connect("file:F:/Work/MovieTheater/data/books/archive/mybooks/comicdb_comicvine_20260122.db?mode=ro", uri=True)
notes = gcdnotes.Notes(gcd, con)
ids = [int(x) for x in a.items.split(",")] if a.items else [int(m.group(1)) for l in open(a.todo, encoding="utf-8") if (m := re.match(r"^\s+(\d+) ", l))]
from identity_packet import Ctx
cvref = Ctx(idbase.Evidence()).cvref   # the indexed name/number view of the rip (cv_vol, cv_iss)

def stem_vol(fn):
    t = re.sub(r"\.(cbz|cbr|pdf)$", "", fn, flags=re.I)
    t = re.sub(r"\s*\((?:[^()]|\([^()]*\))*\)", "", t)          # parentheticals
    m = re.search(r"\s+(?:Vol\.?|Volume|v|Book)\s*0*(\d+)\b", t, re.I)
    vol = int(m.group(1)) if m else 1
    stem = t[:m.start()] if m else t
    stem = re.split(r"\s+-\s+", stem)[0] if " - " in stem and m is None else stem
    return stem.strip(" -"), vol

for iid in ids:
    fn, pc = con.execute("SELECT FileName, PageCount FROM Item WHERE Id=?", (iid,)).fetchone()
    stem, vol = stem_vol(fn)
    print(f"\n[{iid}] {fn[:100]} ({pc}pp)  -> stem '{stem}' vol {vol}")
    probe = stem.replace("&", "%").replace(":", "%").replace(".", "%").replace("'", "%")
    for sid, sname, y0, pub in gcd.execute("""SELECT s.id, s.name, s.year_began, p.name FROM gcd_series s LEFT JOIN gcd_publisher p ON p.id=s.publisher_id
            WHERE s.name LIKE ? AND s.language_id = (SELECT id FROM stddata_language WHERE code='en') ORDER BY s.year_began DESC LIMIT 12""", (probe + "%",)):
        for gi, num, pages in gcd.execute("SELECT id, number, page_count FROM gcd_issue WHERE series_id=? AND deleted=0 AND (number=? OR number=?)", (sid, str(vol), f"{vol}")):
            row = notes.issue(gi); m = RX.search((row or {}).get("notes") or "") if row else None
            rp = notes.reprints(gi)
            rps = "; ".join(f"{nm} " + ",".join(f"#{x:g}-{y:g}" if x != y else f"#{x:g}" for x, y in rg) for _s, nm, rg, _n in rp[:3])
            if m or rps:
                print(f"   GCD {sname} ({pub} {y0}) #{num} [{gi}] {pages or ''}pp | {m.group(0).strip()[:200] if m else ''} | {rps}")
    for vid, vname, vy in cvref.execute("SELECT volId, name, year FROM cv_vol WHERE name LIKE ? ORDER BY year DESC LIMIT 12", (probe + "%",)):
        for (ci,) in cvref.execute("SELECT issueId FROM cv_iss WHERE volId=? AND number=?", (vid, str(vol))):
            r = rip.execute("SELECT raw_api_response FROM cv_issue WHERE id=?", (ci,)).fetchone()
            if not r: continue
            raw = r[0]
            try:
                d = json.loads(raw); d = d.get("results", d)
                txt = html.unescape(re.sub(r"<[^>]+>", " ", (d.get("description") or "") + " " + (d.get("deck") or "")))
            except Exception: continue
            m = RX.search(txt)
            if m: print(f"   CV {vname} ({vy}) #{vol} [{ci}] | {m.group(0).strip()[:220]}")
