"""What GCD says each issue of a COLLECTED series reprints: per issue, the notes' collects clause and the gcd_reprint
roll-up (series + issue ranges). Read-only; feeds hand-written containment ranges.
usage: python gcd_series_reprints.py <gcd series id> [--title-like "%Cerebus%"] [--numbers 3,4,5]
       python gcd_series_reprints.py --find "Cerebus" [--publisher Aardvark]
"""
import argparse, os, re, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "identity", "tools"))
import idbase, gcdnotes

RX = re.compile(r"(?:Collect(?:s|ing|ed)?|Reprint(?:s|ing)?)\b[^.]{0,240}", re.I)
ap = argparse.ArgumentParser(); ap.add_argument("series", nargs="?", type=int); ap.add_argument("--numbers")
ap.add_argument("--find"); ap.add_argument("--publisher")
a = ap.parse_args()
gcd = idbase.open_gcd_dump()
if a.find:
    q = """SELECT s.id, s.name, s.year_began, s.year_ended, p.name, s.issue_count FROM gcd_series s LEFT JOIN gcd_publisher p ON p.id = s.publisher_id
           WHERE s.name LIKE ? AND (? IS NULL OR p.name LIKE ?) ORDER BY s.year_began LIMIT 60"""
    pub = f"%{a.publisher}%" if a.publisher else None
    for r in gcd.execute(q, (f"%{a.find}%", pub, pub)): print(r)
    raise SystemExit(0)
notes = gcdnotes.Notes(gcd, idbase.open_hot())
want = {x.strip() for x in a.numbers.split(",")} if a.numbers else None
for iid, num, title, pages in gcd.execute("SELECT id, number, title, page_count FROM gcd_issue WHERE series_id=? AND deleted=0 ORDER BY sort_code", (a.series,)):
    if want and num not in want: continue
    row = notes.issue(iid)
    m = RX.search((row or {}).get("notes") or "") if row else None
    rp = notes.reprints(iid)
    rps = "; ".join(f"{nm} " + ",".join(f"#{x:g}-{y:g}" if x != y else f"#{x:g}" for x, y in ranges) + f" ({n})" for _s, nm, ranges, n in rp[:4])
    print(f"#{num} [{iid}] {title or ''} {pages or ''}pp | notes: {m.group(0).strip() if m else '-'} | reprints: {rps or '-'}")
