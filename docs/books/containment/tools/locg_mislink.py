"""Measure the LOCG mis-link precisely, on the half-B population, before any of it is reported.

The claim to test: a "Vol. NN" trade was matched to the LOCG record for ISSUE #NN of the same series,
and every span taken off that record is therefore about a different object. LOCG states the record's own
format and title on the page, so the test needs to believe nothing LOCG says about issues.

Reports, for the shelves in the queue half given:
  * how many collected editions carry a LOCG span at all;
  * of those, how many sit on a record whose own page says "Comic";
  * of THOSE, how many assert a span equal to the volume ordinal (the §6.2 shape);
  * how many are winning on the live CollectionNode (SpanSource = Locg), and how many real issue files
    (>=10pp) they would swallow. That last number is the file-loss exposure of this one defect.
"""
import collections
import os
import re
import sqlite3
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prose import page  # noqa: E402  - the cached-page reader, shell trap included

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
RXN = re.compile(r"^\s*(\d{1,5})(?:\.\d+)?\s*$")

lst = next((a.split("=", 1)[1] for a in sys.argv[1:] if a.startswith("--list=")), None)
if lst:
    half = lst
    sids = [int(x) for x in open(os.path.join(HERE, lst)).read().split()]
else:
    half = next((a.split("=", 1)[1] for a in sys.argv[1:] if a.startswith("--half=")), "b")
    out = subprocess.run([sys.executable, os.path.join(HERE, "risk_queue.py"), f"--half={half}"],
                         capture_output=True, text=True).stdout
    sids = [int(m.group(1)) for m in re.finditer(r"^  S(\d+)", out, re.M)]

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
n = comic = ordinal = winning = shell = edition = other = 0
swallow = set()
rows_out = []
for sid in sids:
    issues = [(float(r[0]), r[1]) for r in con.execute(
        """SELECT cd.IssueNo, i.Id FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
           WHERE i.SeriesId=? AND cd.IsCollection=0 AND coalesce(i.IsExcluded,0)=0
             AND coalesce(i.PageCount,0)>=10 AND cd.IssueNo GLOB '[0-9]*'""", (sid,))
        if RXN.match(str(r[0]))]
    for iid, fn, pc, vol, a, b, ref, nsrc in con.execute(
            """SELECT i.Id, i.FileName, i.PageCount, cd.VolumeNo, s.IssueStart, s.IssueEnd, s.ProviderRef,
                      coalesce(nd.SpanSource,0)
               FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
               JOIN CollectedEditionSpan s ON s.ItemId=i.Id AND s.Source=0
               LEFT JOIN CollectionNode nd ON nd.ItemId=i.Id
               WHERE i.SeriesId=? AND cd.IsCollection=1 AND coalesce(i.IsExcluded,0)=0
                 AND s.IssueStart IS NOT NULL""", (sid,)):
        n += 1
        title, fmt, lpages, col = page(str(ref).strip())
        if title.startswith("(SHELL"):
            shell += 1
            kind = "shell"
        elif title.startswith("(no cached"):
            other += 1
            kind = "absent"
        elif re.match(r"(?i)^comic\b", fmt):
            comic += 1
            kind = "COMIC"
        else:
            edition += 1
            kind = "edition"
        isord = kind == "COMIC" and vol is not None and a == b == float(vol)
        if isord:
            ordinal += 1
        hit = [x[1] for x in issues if a <= x[0] <= b]
        if nsrc == 4:
            winning += 1
            if kind == "COMIC":
                swallow |= set(hit)
        if kind == "COMIC":
            rows_out.append((sid, iid, pc, vol, a, b, len(hit), nsrc == 4, isord, title, fn))

print(f"half {half}: {n} collected editions carry a LOCG span")
print(f"   {edition:>4} sit on a LOCG EDITION page")
print(f"   {comic:>4} sit on a LOCG single-COMIC page   <-- the mis-link")
print(f"   {shell:>4} on a shell page (unknown), {other:>4} with no cached page")
print(f"   of the mis-linked, {ordinal} assert exactly the volume ordinal (#N-N where N = Vol. N)")
print(f"   {winning} LOCG spans are the winning node span; the mis-linked ones among them cover "
      f"{len(swallow)} real issue files (>=10pp)")
print("\n-- mis-linked spans that WIN on the node and cover files --")
for sid, iid, pc, vol, a, b, hit, win, isord, title, fn in sorted(rows_out, key=lambda r: -r[6]):
    if win and hit:
        print(f"  S{sid:<7} [{iid:>7}] vol={str(vol):>3} {str(pc):>5}pp  claims #{a:g}-{b:g} "
              f"covering {hit:>3} files   locg={title[:34]:<34} {fn[:46]}")
