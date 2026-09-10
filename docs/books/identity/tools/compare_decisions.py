"""Compare two decision files for the same batch, shelf by shelf — the model-calibration diff.

    python compare_decisions.py decisions/A-002.txt trial-sonnet/A-002.txt

Per shelf: same verdict kind (S vs R)?  same cv id?  same gcd id?  same confidence?  Then the flags each side
raised. Prints every disagreement in full so it can be judged by reading, and a summary at the end. It decides
nothing about which side is right — that is the lead's reading.
"""
import os
import re
import sys
from collections import Counter, defaultdict

RX_S = re.compile(r"^S\s+(\d+)\s+cv=(\S+)\s+gcd=(\S+)\s+([\d.]+)\s*\|\s*(.*)$")
RX_R = re.compile(r"^R\s+(\d+)\s*\|\s*(.*)$")
RX_F = re.compile(r"^F\s+(\d+)\s+(\S+)\s*\|?\s*(.*)$")


def load(path):
    dec, flags = {}, defaultdict(list)
    for raw in open(path, encoding="utf-8"):
        s = raw.rstrip("\n")
        m = RX_S.match(s)
        if m:
            dec[int(m.group(1))] = ("S", m.group(2), m.group(3), m.group(4), m.group(5))
            continue
        m = RX_R.match(s)
        if m:
            dec[int(m.group(1))] = ("R", "-", "-", "-", m.group(2))
            continue
        m = RX_F.match(s)
        if m:
            flags[int(m.group(1))].append((m.group(2), m.group(3)))
    return dec, flags


args = [a for a in sys.argv[1:] if not a.startswith("--")]
a_path, b_path = args[0], args[1]
worklist = None
for k, a in enumerate(sys.argv):
    if a == "--worklist" and k + 1 < len(sys.argv):
        worklist = sys.argv[k + 1]
A, FA = load(a_path)
B, FB = load(b_path)
wl = []


def url_for(cv, gcd):
    """The page most likely to settle it: a GCD id gives a comics.org series page, a CV id a comicvine volume page."""
    out = []
    if gcd not in (None, "-"):
        out.append(f"https://www.comics.org/series/{gcd}/")
    if cv not in (None, "-"):
        out.append(f"https://comicvine.gamespot.com/volume/4050-{cv}/")
    return " · ".join(out) or "(no id on either side — League of Comic Geeks search by name)"
an, bn = os.path.basename(os.path.dirname(os.path.abspath(a_path))) or "A", \
         os.path.basename(os.path.dirname(os.path.abspath(b_path))) or "B"
shelves = sorted(set(A) | set(B))
tally = Counter()
for sid in shelves:
    a, b = A.get(sid), B.get(sid)
    if a is None or b is None:
        tally["missing on one side"] += 1
        print(f"S{sid}: MISSING on {'A' if a is None else 'B'}")
        continue
    same_kind = a[0] == b[0]
    same_cv = a[1] == b[1]
    same_gcd = a[2] == b[2]
    same_conf = a[3] == b[3]
    fa = sorted(f for f, _ in FA.get(sid, []))
    fb = sorted(f for f, _ in FB.get(sid, []))
    if same_kind and same_cv and same_gcd and same_conf and fa == fb:
        tally["identical"] += 1
        continue
    if same_kind and same_cv and same_gcd:
        tally["same ids, differ only in confidence/flags"] += 1
        print(f"S{sid}: same ids; conf {a[3]} vs {b[3]}; flags {fa} vs {fb}")
        continue
    tally["DIFFERENT verdict or ids"] += 1
    wl.append(
        f"## S{sid}\n"
        f"- {an}: {a[0]} cv={a[1]} gcd={a[2]} {a[3]} | {a[4]}\n"
        f"- {bn}: {b[0]} cv={b[1]} gcd={b[2]} {b[3]} | {b[4]}\n"
        f"- settle: which record describes the shelf as a whole — {an}'s (cv {a[1]} / gcd {a[2]}) or "
        f"{bn}'s (cv {b[1]} / gcd {b[2]})? Check the issue count, start year and whether the record is a "
        f"collected-edition series while a numbered run exists.\n"
        f"- pages: {url_for(a[1], a[2])} | {url_for(b[1], b[2])}\n")
    print(f"\nS{sid}: {an} {a[0]} cv={a[1]} gcd={a[2]} {a[3]}\n      | {a[4][:300]}")
    for f, d in FA.get(sid, []):
        print(f"      F {f} | {d[:200]}")
    print(f"      {bn} {b[0]} cv={b[1]} gcd={b[2]} {b[3]}\n      | {b[4][:300]}")
    for f, d in FB.get(sid, []):
        print(f"      F {f} | {d[:200]}")

print("\n== summary over", len(shelves), "shelves")
for k, v in tally.most_common():
    print(f"  {v:4}  {k}")
ca = Counter(v[3] for v in A.values() if v[0] == "S")
cb = Counter(v[3] for v in B.values() if v[0] == "S")
print(f"  confidence {an}: {dict(ca)}   {bn}: {dict(cb)}")
print(f"  flags {an}: {dict(Counter(f for fs in FA.values() for f, _ in fs))}")
print(f"  flags {bn}: {dict(Counter(f for fs in FB.values() for f, _ in fs))}")
if worklist:
    with open(worklist, "a", encoding="utf-8") as fh:
        fh.write(f"# disagreements {os.path.basename(a_path)} vs {os.path.basename(b_path)}\n\n" + "\n".join(wl))
    print(f"  {len(wl)} disagreement(s) appended to {worklist}")
