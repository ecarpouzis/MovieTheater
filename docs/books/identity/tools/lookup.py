"""Ad-hoc name lookup for a reading worker: the same ComicVine rip and GCD dump the packet consulted,
probed with a spelling of YOUR choosing, exact first and then as a substring.

    python lookup.py "Battle Action" [--year 1977] [--contains]

Exact = the normalised name matches (the packet's own rule). --contains = the normalised name CONTAINS the
probe, capped at 25 hits per source, sorted by year. --year keeps hits within ±1 of the year.
This is a lookup whose results you read; it decides nothing.
"""
import sys

from idbase import Evidence, norm_name
from identity_packet import Ctx

args = [a for a in sys.argv[1:] if not a.startswith("--")]
if not args:
    raise SystemExit(__doc__)
probe = norm_name(args[0])
year = None
for k, a in enumerate(sys.argv):
    if a == "--year" and k + 1 < len(sys.argv):
        year = int(sys.argv[k + 1])
        if sys.argv[k + 1] in args:
            args.remove(sys.argv[k + 1])
contains = "--contains" in sys.argv

ctx = Ctx(Evidence())

# ── --issues <volId>: the issue ids of one volume (TOOLS_TODO 12) ───────────────────────────────
# An `I` line names an ISSUE, and the packet mostly shows VOLUMES; ~2,000 I lines were written `cv=-`
# for want of this. cvref's cv_iss is indexed on volId, so it is a lookup, not a scan.
if "--issues" in sys.argv:
    k = sys.argv.index("--issues")
    vol = int(sys.argv[k + 1])
    rows = ctx.cvref.execute(
        "SELECT issueId, number, name, coverDate, storeDate FROM cv_iss WHERE volId=? ORDER BY numKey, issueId",
        (vol,)).fetchall() if ctx.cvref is not None else []
    v = ctx.cv_index()
    meta = next((r for rs in v.values() for r in rs if r[0] == vol), None)
    if meta:
        print(f'volume {vol} "{meta[1]}" {meta[3] or "?"} {meta[5] or "?"} — {meta[4] or "?"} issues per the rip')
    print(f"{len(rows)} cached issue(s)")
    for iid, num_, name, cd, sd in rows:
        print(f"   issue {iid:<9} #{str(num_ or '?'):<8} {cd or sd or '':<12} {(name or '')[:60]}")
    raise SystemExit(0)



def keep(y):
    return year is None or (y is not None and abs(int(y) - year) <= 1)


cv = ctx.cv_index()
hits = list(cv.get(probe, ()))
if contains:
    hits += [r for k, rs in cv.items() if probe in k and k != probe for r in rs]
hits = [h for h in hits if keep(h[3])][:25]
print(f"ComicVine rip — {len(hits)} hit(s) for '{probe}'" + (f" year {year}±1" if year else ""))
for h in sorted(hits, key=lambda h: (h[3] or 0)):
    print(f'   {h[0]} "{h[1]}" {h[3] or "?"} {h[5] or "?"} {h[4] or "?"} issues')

gc = ctx.gcd_index()
hits = list(gc.get(probe, ()))
if contains:
    hits += [r for k, rs in gc.items() if probe in k and k != probe for r in rs]
hits = [h for h in hits if keep(h[2])][:25]
print(f"GCD dump — {len(hits)} hit(s)")
for h in sorted(hits, key=lambda h: (h[2] or 0)):
    print(f'   {h[0]} "{h[1]}" {h[2] or "?"}-{h[3] or "?"} {h[6] or "?"} [{h[5] or ""}] {h[4] or "?"} issues')
