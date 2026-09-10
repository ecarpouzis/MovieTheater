"""Give partial rips a comparable issue number without losing the fact that they are partial.

THE PROBLEM. 583 files carried an IssueNo that is not a number - `217 (GL only)` is the Green Lantern story
cut out of Flash #217, `03 (B Story)` is the second story of issue #3 as GCD indexes it, `22 (Edit)` is an
edited rip. The leading number IS the issue; the parenthetical says the file is one piece of it. But a
non-numeric IssueNo cannot be compared, so NO RANGE CAN EVER REACH THESE FILES - they are invisible to
containment, which is exactly the "issue details" defect this pass exists to remove.

THE FIX, and why it is a reading rather than a guess. The number is printed on the file; only the marker is
being moved. `ParseNotes` is the TEXT column for precisely this and is already populated on 34,171 rows, so
nothing is lost and no schema changes: IssueNo becomes the number, IssueSource becomes ParseSource.Manual
(8), and the marker is appended to ParseNotes.

WHAT IS DELIBERATELY NOT CONVERTED. `Annual 02` and `Part 05` are their OWN coordinate spaces - an annual is
not issue two of the run, and converting it would file it among the monthlies. Those 36 are left alone and
reported.

A story letter is not a duplicate to be feared: `03 (A Story)` and `03 (B Story)` both become #3 because
both really are stories from issue #3, and a book collecting #3 contains both.

`python normalise_partial_rips.py [--apply]`   Dry run by default.
"""
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
APPLY = "--apply" in sys.argv

NUMERIC = re.compile(r"-?\d+(\.\d+)?")
LEAD = re.compile(r"^0*(\d+)\s*\((.+)\)$")

con = sqlite3.connect(HOT)
rows = [r for r in con.execute(
    """SELECT i.Id, cd.IssueNo, cd.ParseNotes, i.FileName FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
       WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IssueNo IS NOT NULL""")
        if not NUMERIC.fullmatch(str(r[1]).strip())]

writes, skipped = [], []
for iid, no, notes, fn in rows:
    m = LEAD.fullmatch(str(no).strip())
    if not m:
        skipped.append((iid, str(no).strip(), fn))
        continue
    num, marker = str(int(m.group(1))), m.group(2).strip()
    tag = f"partial rip: {marker} (IssueNo was {str(no).strip()!r})"
    merged = tag if not notes else (notes if tag in notes else f"{notes}; {tag}")
    writes.append((num, merged, iid, str(no).strip(), fn))

print(f"{len(rows)} non-numeric issue number(s)")
print(f"  convertible  : {len(writes)}")
print(f"  left alone   : {len(skipped)}  (their own coordinate space - an annual is not issue two)")
for iid, num, marker, no, fn in [(w[2], w[0], w[1], w[3], w[4]) for w in writes[:8]]:
    print(f"   {iid:<8} {no:<16} -> #{num:<5} {fn[:56]}")
if len(writes) > 8:
    print(f"   ... and {len(writes)-8} more")
print("  not converted:", sorted({s[1] for s in skipped})[:12])

if not APPLY:
    print("\n(dry run - re-run with --apply)")
    raise SystemExit

con.executemany("UPDATE ComicDetail SET IssueNo = ?, IssueSource = 8, ParseNotes = ? WHERE ItemId = ?",
                [(w[0], w[1], w[2]) for w in writes])
con.commit()
print(f"\napplied: {len(writes)} partial rip(s) given a comparable issue number, marker kept in ParseNotes")
