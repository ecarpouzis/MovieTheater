"""Write issue numbers that a person (or the model) READ, one file at a time, from a hand-written sheet.

This tool derives NOTHING. It takes `ItemId,IssueNo` rows that were written by reading each book — its
title, its cover, its indicia — and stores them. There is no pattern here to be wrong about a thousand
files at once, which is the whole point: every previous attempt to infer issue numbers with a rule over
the whole population produced a rule that was right about most files and quietly wrong about the rest.

It prints the file name beside each change so the write can be checked against what was read, and refuses
any row whose item does not exist.

Dry-run by default. `python apply_read_issue_numbers.py <sheet.csv> [--apply]`
"""
import csv
import os
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
args = [a for a in sys.argv[1:] if not a.startswith("--")]
APPLY = "--apply" in sys.argv
sheet = args[0] if args else os.path.join(HERE, "issue-numbers-read.csv")
if not os.path.exists(sheet):
    sheet = os.path.join(HERE, os.path.basename(sheet))

con = sqlite3.connect(HOT)
names = {r[0]: (r[1], r[2]) for r in con.execute(
    """SELECT i.Id, i.FileName, cd.IssueNo FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id""")}

rows = list(csv.DictReader(open(sheet, encoding="utf-8")))
writes, missing, unchanged = [], [], 0
for r in rows:
    iid = int(r["ItemId"])
    if iid not in names:
        missing.append(iid)
        continue
    fn, was = names[iid]
    # An EMPTY IssueNo in the sheet means "this file has no issue number", which is a real answer and
    # often the only true one: `The Phantom - The Complete Newspaper Dailies v03 - 1939 - 1940` is volume
    # three of a strip reprint, and the 1939 stored on it is the first year it covers. A wrong number is
    # worse than none, because it puts the file inside a range that does not contain it.
    want = (r["IssueNo"] or "").strip() or None
    if (str(was) if was is not None else None) == want:
        unchanged += 1
        continue
    writes.append((iid, fn, was, want))

print(f"{len(rows)} row(s) read from {os.path.basename(sheet)}")
print(f"  already correct : {unchanged}")
print(f"  to write        : {len(writes)}")
print(f"  item not found  : {len(missing)}")
for iid, fn, was, now in writes[:20]:
    print(f"   {iid:<8} #{was} -> {('#' + now) if now else '(no number)':<12} {fn[:62]}")
if len(writes) > 20:
    print(f"   ... and {len(writes) - 20} more")

if not APPLY:
    print("\n(dry run - re-run with --apply)")
    raise SystemExit
# IssueSource is the ParseSource ENUM stored as INTEGER (Manual = 8). Writing the string 'read-by-hand'
# into it worked in SQLite and broke the type for EF - 2,090 rows had to be repaired. The provenance goes
# in ParseNotes, which is the TEXT column meant for it.
con.executemany("""UPDATE ComicDetail
                   SET IssueNo = ?, IssueSource = 8,
                       ParseNotes = CASE WHEN ParseNotes IS NULL OR ParseNotes = '' THEN 'IssueNo set by hand'
                                         WHEN instr(ParseNotes, 'IssueNo set by hand') > 0 THEN ParseNotes
                                         ELSE ParseNotes || '; IssueNo set by hand' END
                   WHERE ItemId = ?""",
                [(now, iid) for iid, _, _, now in writes])
con.commit()
print(f"\napplied: {len(writes)} issue number(s) written from the sheet")
