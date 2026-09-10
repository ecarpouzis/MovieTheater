"""Check issue-number changes against what is INSIDE each archive, and revert whatever cannot be shown.

WHY THIS EXISTS. `fix_year_issue_numbers.py` re-derived 486 issue numbers from the FILE NAME. The old
value was demonstrably wrong — it was the year, lifted from that same name — but that does not license
guessing the new one from the same string. §1 of PLAN.md is precisely about not doing that.

THE EVIDENCE, and why it is not another parse of the file name:

  page entry names   the pages inside a .cbz/.cbr are named by whoever made the rip, from the comic:
                     `Batman (2011-) 022-000.jpg`, `Detective Comics (2011-) 025-031.jpg`. The number
                     standing immediately before the page index is the issue. This is the same class of
                     evidence the manga pass already trusts (`... - c001 (v01) - p002`), and it lives in a
                     different record from the outer file name — a rip whose container says `022` on all
                     thirty-two pages is not guessing.
                     At least two pages must agree, and they must agree with each other.
  ComicInfo.xml      the metadata sheet the ripper wrote inside the archive. Used when present; it is
                     rarer than the page names and, in this population, not always right either.

Verdicts:
  AGREES        the archive says what we wrote. Keep, and record IssueSource so it is known to be checked.
  DISAGREES     the archive says something else. Take the archive's answer.
  NO EVIDENCE   the pages are named `00.jpg`, `01.jpg` and there is no ComicInfo. REVERT to the previous
                value and list the file. An unverified guess is not an improvement on a known-bad value;
                it is a worse one, because it looks right.

`python verify_issue_numbers.py <changes.csv> [--apply] [--list out.csv]`
The CSV is the review sheet the fix wrote: ItemId,SeriesId,FileName,WasIssueNo,NewIssueNo
"""
import csv
import os
import re
import sqlite3
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from collections import Counter

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif")

args = [a for a in sys.argv[1:] if not a.startswith("--")]
APPLY = "--apply" in sys.argv
list_path = "issue-number-unverified.csv"
for a in sys.argv[1:]:
    if a.startswith("--list"):
        list_path = a.split("=", 1)[1]
csv_path = args[0] if args else os.path.join(HERE, "issue-number-year-fix.csv")

# the issue number is the last number before the page index in a page's entry name
RX_ENTRY = re.compile(r"(?<![\d.])(\d{1,4})(?:[.-](\d))?\s*[-_ ]\s*0*(\d{1,3})$")
RX_YEAR = re.compile(r"^(19|20)\d{2}$")

con = sqlite3.connect(HOT)
paths = {r[0]: r[1] for r in con.execute("SELECT Id, Path FROM Item")}


def entries(full):
    try:
        if zipfile.is_zipfile(full):
            with zipfile.ZipFile(full) as z:
                return z.namelist()
        out = subprocess.run([SEVENZIP, "l", "-ba", "-slt", full], capture_output=True, text=True, timeout=180)
        return [l[7:].strip() for l in out.stdout.splitlines() if l.startswith("Path = ")]
    except Exception:
        return []


def from_pages(names):
    """The issue number the page entry names agree on, or None. Two pages must say it."""
    votes = Counter()
    for n in names:
        base = os.path.splitext(os.path.basename(n))[0]
        if not n.lower().endswith(IMG):
            continue
        m = RX_ENTRY.search(base)
        if not m:
            continue
        whole, part = m.group(1), m.group(2)
        if RX_YEAR.match(whole):
            continue
        votes[f"{int(whole)}.{part}" if part else str(int(whole))] += 1
    if not votes:
        return None
    best, n = votes.most_common(1)[0]
    return best if n >= 2 else None


def from_comicinfo(full, names):
    try:
        name = next((n for n in names if n.lower().endswith("comicinfo.xml")), None)
        if not name:
            return None
        if zipfile.is_zipfile(full):
            with zipfile.ZipFile(full) as z:
                data = z.read(name)
        else:
            out = subprocess.run([SEVENZIP, "e", "-so", full, "ComicInfo.xml", "-r"],
                                 capture_output=True, timeout=120)
            data = out.stdout
        num = ET.fromstring(data.decode("utf-8-sig", "replace")).findtext("Number")
        return num.strip() if num and num.strip() else None
    except Exception:
        return None


def same(a, b):
    try:
        return abs(float(a) - float(b)) < 1e-9
    except (TypeError, ValueError):
        return str(a).strip() == str(b).strip()


rows = list(csv.DictReader(open(csv_path, encoding="utf-8")))
agree, disagree, blind = [], [], []
for k, r in enumerate(rows, 1):
    iid = int(r["ItemId"])
    full = paths.get(iid)
    names = entries(full) if full else []
    said = from_pages(names)
    src = "page names"
    if said is None:
        said = from_comicinfo(full, names)
        src = "ComicInfo"
    if said is None:
        blind.append(r)
    elif same(said, r["NewIssueNo"]):
        agree.append(r)
    else:
        disagree.append((r, said, src))
    if k % 100 == 0:
        print(f"  ... {k}/{len(rows)} checked")

print(f"\n{len(rows)} change(s) checked against the archive's own record")
print(f"  the archive AGREES with the change   : {len(agree)}")
print(f"  the archive says something ELSE      : {len(disagree)}")
print(f"  the archive says nothing (reverting) : {len(blind)}")
for r, said, src in disagree[:15]:
    print(f"     {r['ItemId']:<8} wrote #{r['NewIssueNo']:<6} archive says #{said:<6} ({src})  {r['FileName'][:46]}")
for r in blind[:10]:
    print(f"     {r['ItemId']:<8} wrote #{r['NewIssueNo']:<6} -> back to #{r['WasIssueNo']:<6} {r['FileName'][:46]}")

with open(os.path.join(HERE, list_path), "w", encoding="utf-8", newline="") as fh:
    w = csv.writer(fh)
    w.writerow(["ItemId", "SeriesId", "FileName", "WasIssueNo", "GuessedIssueNo"])
    w.writerows([[r["ItemId"], r["SeriesId"], r["FileName"], r["WasIssueNo"], r["NewIssueNo"]] for r in blind])
print(f"\nunverified files listed -> {list_path}")

if not APPLY:
    print("(dry run - re-run with --apply)")
    raise SystemExit
con.executemany("UPDATE ComicDetail SET IssueNo = ?, IssueSource = 'archive-page-names' WHERE ItemId = ?",
                [(r["NewIssueNo"], int(r["ItemId"])) for r in agree])
con.executemany("UPDATE ComicDetail SET IssueNo = ?, IssueSource = 'archive-page-names' WHERE ItemId = ?",
                [(said, int(r["ItemId"])) for r, said, _ in disagree])
con.executemany("UPDATE ComicDetail SET IssueNo = ?, IssueSource = 'unverified-reverted' WHERE ItemId = ?",
                [(r["WasIssueNo"], int(r["ItemId"])) for r in blind])
con.commit()
print(f"\napplied: {len(agree)} confirmed, {len(disagree)} corrected to the archive, {len(blind)} reverted")
