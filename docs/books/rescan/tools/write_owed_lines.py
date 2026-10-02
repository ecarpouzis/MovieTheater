"""Append the rescan's containment decisions to every decision file that owes one (after owed_containment.py).

Every owed edition gets exactly one line: an `S` where the reading proved the range (the PROVEN table below,
each with the evidence sentence), otherwise `u` — refused until someone reads the book. Each file gains one
dated ADDENDUM block. Idempotent: an edition that already has a line is skipped.
usage: python write_owed_lines.py --run data/books/rescan/20261002 [--apply]
"""
import argparse, collections, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.normpath(os.path.join(HERE, "..", "..", "containment", "decisions"))

PROVEN = {  # itemId: (start, end, confidence, evidence)
    245873: (42, 47, 0.95, "the book's own ComicInfo summary: 'Collects MONSTRESS #42-47'; it replaced our #42-47"),
    245874: (48, 53, 0.85, "the held volumes run six issues each (Vol. 04 #19-24, Vol. 05 #25-30, Vol. 08 #42-47 by its ComicInfo), so Vol. 09 = #48-53; it replaced our #48-53"),
    245884: (47, 50, 0.9, "it replaced our #47-50 and the held Vol. 08 ends at #42 (Vol. 09 = #43-46); 175pp = 32+32+48+68"),
    246165: (9, 12, 0.9, "it replaced our #9-12 of the 12-issue mini; 96pp = 4 x 24pp"),
    246420: (9, 12, 0.9, "it replaced our #9-12; 116pp = 4 issues"),
    245977: (311, 315, 0.9, "the ComicVine rip: 'Volume Three collects G.I. JOE: A REAL AMERICAN HERO issues #311-315'; it replaced our #311-313"),
    245951: (7, 12, 0.85, "the held Vol. 01 is judged #1-6; it replaced our #7-8; 157pp = six issues"),
    245650: (1, 5, 0.9, "the Deluxe Edition reprints the 5-issue mini; it replaced our TPB of it (item 10888, judged #1-5)"),
    245685: (1, 4, 0.9, "it collects the Book of Life mini #1-4 (of 04), which it replaced"),
    245670: (1, 14, 0.9, "a second rip of Mister X Vol. 01 - The Archives, the same book as item 14535 (judged #1-14)"),
}

ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--apply", action="store_true")
a = ap.parse_args()
owed = collections.defaultdict(list)
cur = None
for line in open(os.path.join(a.run, "owed_containment.txt"), encoding="utf-8"):
    m = re.match(r"### S(\d+) ", line)
    if m: cur = int(m.group(1)); continue
    m = re.match(r"  - (\d+)  (.+?)  \[(\d+|None)pp\]", line)
    if m and cur: owed[cur].append((int(m.group(1)), m.group(2), m.group(3)))
n_s = n_u = 0
for sid, eds in sorted(owed.items()):
    path = os.path.join(DEC, f"S{sid}.txt")
    text = open(path, encoding="utf-8").read()
    have = {int(x) for x in re.findall(r"^[Su] (\d+)", text, re.M)}
    block = ["", f"# ADDENDUM (comics rescan, 2026-10-02): editions new on this shelf since the rescan (docs/books/rescan/). A range is",
             "# written only where the book, the ComicVine rip or the held volumes' pattern proves it; the rest are refused until read."]
    for iid, fn, pp in eds:
        if iid in have: continue
        if iid in PROVEN:
            s, e, conf, why = PROVEN[iid]
            title = re.sub(r"\s*\((?:digital|Digital)[^)]*\).*$", "", re.sub(r"\.(cbz|cbr)$", "", fn))
            block.append(f"S {iid} {s} {e} {conf} | {title} | {why}"); n_s += 1
        else:
            block.append(f"u {iid} new in the 2026-10-02 rescan ({fn}, {pp}pp); no range read from the book"); n_u += 1
    if len(block) > 3 and a.apply:
        with open(path, "a", encoding="utf-8") as f: f.write("\n".join(block) + "\n")
print({"files": len(owed), "S": n_s, "u": n_u, "applied": a.apply})
