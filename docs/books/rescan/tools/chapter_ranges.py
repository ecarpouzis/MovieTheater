"""Replace the rescan's `u` refusals with ranges the archive itself states (manga chapter tokens on every page).

For every containment decision file holding a `u <item> new in the 2026-10-02 rescan …` / `… armed by …` line,
run the containment pass's `chapters_from_archive.py` on the shelf and, for each refused edition whose pages name
their chapters, write `S <item> <first> <last> 0.97 | <title> | chapter: …` in place of the `u` — but ONLY when
that range overlaps no other edition's archive-stated range on the shelf (an overlap means two editions of one
work, or a coordinate clash: left refused and printed). Editions whose pages carry no chapter token stay `u`.
Dry run by default; `--apply` rewrites the files (one line swapped per edition, nothing else touched).
usage: python chapter_ranges.py [--apply] [--only 10235,12525]
"""
import argparse, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
CTOOLS = os.path.normpath(os.path.join(HERE, "..", "..", "containment", "tools"))
DEC = os.path.normpath(os.path.join(HERE, "..", "..", "containment", "decisions"))
RX_U = re.compile(r"^u (\d+) (new in the 2026-10-02 rescan|armed by a numbered issue the 2026-10-02 rescan)")
RX_OK = re.compile(r"^\s*\[\s*(\d+)\]\s+(.*?)\s{2,}(\d+) chapters\s+c([\d.]+)-([\d.]+)(.*)$")

ap = argparse.ArgumentParser(); ap.add_argument("--apply", action="store_true"); ap.add_argument("--only")
a = ap.parse_args()
only = {int(x) for x in a.only.split(",")} if a.only else None
tot = {"files": 0, "ranged": 0, "overlap": 0, "no-token": 0}
for fn in sorted(os.listdir(DEC)):
    m = re.match(r"^S(\d+)\.txt$", fn)
    if not m: continue
    sid = int(m.group(1))
    if only and sid not in only: continue
    path = os.path.join(DEC, fn)
    lines = open(path, encoding="utf-8").read().split("\n")
    owed = {int(mm.group(1)): k for k, l in enumerate(lines) if (mm := RX_U.match(l))}
    if not owed: continue
    out = subprocess.run([sys.executable, os.path.join(CTOOLS, "chapters_from_archive.py"), str(sid)],
                         capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=CTOOLS).stdout
    ranges = {}
    for l in out.splitlines():
        mm = RX_OK.match(l)
        if mm: ranges[int(mm.group(1))] = (mm.group(2).strip(), mm.group(3), float(mm.group(4)), float(mm.group(5)), mm.group(6).strip())
    changed = False
    for iid, k in owed.items():
        if iid not in ranges: tot["no-token"] += 1; continue
        title, n, s, e, tail = ranges[iid]
        clash = [o for o, r in ranges.items() if o != iid and not (r[3] < s or r[2] > e)]
        if clash:
            tot["overlap"] += 1
            print(f"S{sid} {iid} c{s:g}-{e:g} OVERLAPS {clash} — left refused ({title[:60]})"); continue
        t = re.sub(r"\.(cbz|cbr|zip|rar)$", "", title)
        lines[k] = (f"S {iid} {s:g} {e:g} 0.97 | {t} | chapter: the archive names the chapter on every page and this volume "
                    f"holds {n} of them, c{s:g}-{e:g}, overlapping no other volume on the shelf (2026-10-02 rescan){(' ' + tail) if tail else ''}")
        changed = True; tot["ranged"] += 1
        print(f"S{sid} {iid} -> c{s:g}-{e:g}  {t[:70]}")
    if changed:
        tot["files"] += 1
        if a.apply: open(path, "w", encoding="utf-8").write("\n".join(lines))
print(tot, "applied" if a.apply else "(dry run)")
