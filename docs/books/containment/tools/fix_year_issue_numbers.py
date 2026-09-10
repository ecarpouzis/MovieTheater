"""Issue numbers that are actually the YEAR off the same filename.

WHAT WENT WRONG. Files in the DC event-chronology trees are named `Justice League 024 - December 2013.cbr`
— issue, then month, then year. The parse took the last number it saw and stored IssueNo = 2013. On a
shelf that also holds a collected edition, an issue numbered 2013 is outside every range, so it can never
be nested; and a shelf full of them looks like it has no ladder at all.

THE RULE, and why each clause is there:
  the stored number is a YEAR                1900-2099. Nothing else is touched.
  that same year appears in the FILE NAME    proof of where the number came from. If the year is not in
                                             the name, the number came from metadata and this tool has no
                                             standing to argue with it.
  a number stands BEFORE the year            the issue. Taken as the LAST such token before the year, so
                                             `000 Action Comics 023-2 - November 2013` gives 23.2 and not
                                             the leading ordinal 000, and a month name is skipped because
                                             it is not a number.
  that number is not itself a year           or the fix would restore the fault it is fixing.

A `.N` or `-N` suffix immediately after the token is carried across (`023.2` and `023-2` both mean #23.2),
because DC's New 52 `.1`/`.2` issues are real issues and folding them onto the integer would collide them
with the issue they hang off.

Every change is written to a review CSV whether or not it is applied. Dry-run by default.
`python fix_year_issue_numbers.py [--apply] [--csv out.csv]`
"""
import csv
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
HERE = os.path.dirname(os.path.abspath(__file__))
APPLY = "--apply" in sys.argv
csv_path = "issue-number-year-fix.csv"
for a in sys.argv[1:]:
    if a.startswith("--csv"):
        csv_path = a.split("=", 1)[1]

RX_YEAR_ONLY = re.compile(r"^(19|20)\d{2}$")
RX_EXT = re.compile(r"(?i)\.(cbz|cbr|cb7|pdf|epub)$")
# a standalone number, optionally with a .N / -N part-issue suffix
RX_TOKEN = re.compile(r"(?<![\d.])(\d{1,4})(?:[.-](\d))?(?![\d])")
RX_LEAD_ORDINAL = re.compile(r"^[A-Za-z.]*\d{1,3}[.\s_-]+")
RX_MONTH = r"jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|jun(e)?|jul(y)?|aug(ust)?|sep(t|tember)?|oct(ober)?|nov(ember)?|dec(ember)?"
# between the issue number and the year there may be separators and at most a month name - nothing else.
# `Zenith - Data Byte`, `Anniversary Extravaganza, Free Comic Book Day` and `Secret Files & Origins` are
# all text, and a number sitting that far from the year is not the issue this file is.
RX_GAP = re.compile(r"^[\s._\-,()#\[\]]*(?:" + RX_MONTH + r")?[\s._\-,()#\[\]]*$", re.I)
RX_VOL_BEFORE = re.compile(r"(?i)v(?:ol(?:ume)?)?[.\s]*$")
RX_MONTH_BEFORE = re.compile(r"(?i)(?:" + RX_MONTH + r")[\s._\-]*$")
RX_ORDINAL_SUFFIX = re.compile(r"(?i)^(st|nd|rd|th)")

con = sqlite3.connect(HOT)
rows = con.execute("""SELECT i.Id, i.FileName, cd.IssueNo, i.SeriesId
                      FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                      WHERE cd.IsCollection = 0 AND coalesce(i.IsExcluded, 0) = 0
                        AND cd.IssueNo IS NOT NULL""").fetchall()

changes, no_candidate = [], 0
for iid, fn, no, sid in rows:
    if not RX_YEAR_ONLY.match(str(no)):
        continue
    stem = RX_EXT.sub("", fn or "")
    at = stem.find(str(no))
    if at < 0:
        continue                                        # the year is not in the name; not our business
    if at > 0 and stem[at - 1] == "#":
        continue                                        # `2000AD #2000` - the year IS the issue number
    head = RX_LEAD_ORDINAL.sub("", stem[:at], count=1)   # drop a chronology-folder ordinal prefix
    offset = len(stem[:at]) - len(head)
    best = None
    for m in RX_TOKEN.finditer(head):
        whole, part = m.group(1), m.group(2)
        if RX_YEAR_ONLY.match(whole):
            continue
        if RX_VOL_BEFORE.search(head[:m.start()]):
            continue                                    # `v3` is the volume, not the issue
        if RX_MONTH_BEFORE.search(head[:m.start()]):
            continue                                    # `March 22, 2014` - a day, not an issue
        if RX_ORDINAL_SUFFIX.match(head[m.end():]):
            continue                                    # `10th Anniversary`
        if not RX_GAP.match(head[m.end():]):
            continue                                    # words stand between it and the year
        best = f"{int(whole)}.{part}" if part else str(int(whole))
    if best is None:
        no_candidate += 1
        continue
    changes.append((iid, sid, fn, str(no), best))

print(f"{len(changes)} issue number(s) are the year off their own filename and have a number before it")
print(f"{no_candidate} more carry a year but no number stands before it - left alone")
for iid, sid, fn, was, now in changes[:15]:
    print(f"   {iid:<8} S{sid:<7} #{was} -> #{now:<6} {fn[:62]}")

with open(os.path.join(HERE, csv_path), "w", encoding="utf-8", newline="") as fh:
    w = csv.writer(fh)
    w.writerow(["ItemId", "SeriesId", "FileName", "WasIssueNo", "NewIssueNo"])
    w.writerows(changes)
print(f"\nreview sheet -> {csv_path}")

if not APPLY:
    print("(dry run - re-run with --apply)")
    raise SystemExit
con.executemany("UPDATE ComicDetail SET IssueNo = ?, IssueSource = 'filename-year-repair' WHERE ItemId = ?",
                [(n, i) for i, _, _, _, n in changes])
con.commit()
print(f"applied: {len(changes)} issue number(s) corrected")
