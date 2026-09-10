"""Turn `chapters_from_archive.py` output into a decision file, so the ranges are transcribed by machine.

The judgement is mine — which shelf, whether the ladder tiles, whether the chapter numbers are the
coordinate the held files use. The TRANSCRIPTION should not be, because a hand-typed 135 that should be
136 is exactly the kind of error nobody catches.

`python make_manga_decision.py <seriesId> <chapters.txt> > ../decisions/S<id>.txt`
"""
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
RX_OK = re.compile(r"^\s*\[\s*(\d+)\]\s+(.*?)\s{2,}(\d+) chapters\s+c([\d.]+)-([\d.]+)(.*)$")
RX_NO = re.compile(r"^\s*\[\s*(\d+)\]\s+(.*?)\s{2,}(no chapter token|UNREADABLE)")

sid = int(sys.argv[1])
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
titles = {r[0]: r[1] for r in con.execute(
    "SELECT Id, FileName FROM Item WHERE SeriesId = ?", (sid,))}

ranged, refused = [], []
for line in open(sys.argv[2], encoding="utf-8", errors="ignore"):
    m = RX_OK.match(line)
    if m:
        iid, n, lo, hi, rest = int(m.group(1)), int(m.group(3)), m.group(4), m.group(5), m.group(6)
        ranged.append((iid, lo, hi, n, "GAPS" in rest))
        continue
    m = RX_NO.match(line)
    if m:
        refused.append((int(m.group(1)), m.group(3)))

def title(iid):
    fn = titles.get(iid, "")
    return re.sub(r"\.(cbz|cbr|cb7|cbt|pdf|epub)$", "", fn, flags=re.I).split(" (")[0]

for iid, lo, hi, n, gaps in ranged:
    note = (f"the archive names the chapter on every page, and this volume holds {n} of them, c{lo}-c{hi}"
            + ("; the run is not contiguous inside the volume" if gaps else ""))
    print(f"S {iid} {lo} {hi} 0.97 | {title(iid)} | {note}")
for iid, why in refused:
    print(f"u {iid} the rip names no chapter on its pages ({why.lower()}), so the chapter range cannot be "
          f"determined; a volume number is not a chapter number and must not stand in for one")
print(f"\n# {len(ranged)} ranged, {len(refused)} refused", file=sys.stderr)
