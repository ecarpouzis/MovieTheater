"""Audit ISSUE DETAILS over the whole population: the number, the collection flag, the format.

Containment has three standing checks (`audit_containment.py`, `overclaim_check.py`, `overlap_check.py`).
Issue details had none — they were corrected wherever a shelf was read, which is the queue-shaped mistake
this pass kept making. This asks the population instead, one known defect class per check.

Every class here was found by READING files during the containment pass, so none of them is speculative:

  year coordinate      `Annual 2021` -> #2021, `Free Comic Book Day 2016` -> #2016, `The Eternaut 1969`
  sort prefix          `11 Infinite_Crisis_07` -> #11, `07 Secret Warriors v02` -> #7  (chronology trees)
  sub-line number      `Locas #1` inside a Love and Rockets Library volume -> #1
  title number         `Black Hammer '45` -> #45, `Marvel Age ... 1000`
  flag, both ways      a 1pp variant cover flagged as a collection; a 400pp book flagged as a single issue
  format               `Format = TPB` on a page-rip of one issue (the trade's ComicInfo came with it)

`python audit_issue_details.py [--verbose] [--limit N]`   Prints a count per class; verbose lists samples.
"""
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
VERBOSE = "--verbose" in sys.argv
LIMIT = 15
for a in sys.argv[1:]:
    if a.startswith("--limit"):
        LIMIT = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

# THE OUTLIER PRINCIPLE. A number is not suspicious because it looks like a year or a prefix - `2000AD
# 1900` is a real prog number and `#1000000` is a real DC One Million issue, and a rule that flags them is
# the exact regex mistake this pass was told three times to stop making. What makes a number suspicious is
# that it does not belong to the LADDER OF ITS OWN SHELF: an `Annual 2021` sitting among issues numbered
# 1-50 is an outlier; prog 1900 sitting among 1899 and 1901 is not. So every check below is gated on the
# number being far outside the run's own range, which is evidence rather than pattern.
ladder = {}
for sid, no in con.execute("""SELECT i.SeriesId, cd.IssueNo FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
   WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IssueNo IS NOT NULL AND cd.IsCollection = 0
     AND coalesce(i.PageCount,0) >= 8 AND i.SeriesId IS NOT NULL"""):
    try:
        ladder.setdefault(sid, []).append(float(no))
    except ValueError:
        pass
peers = {}
for sid, vals in ladder.items():
    vals.sort()
    if vals:
        peers[sid] = (vals[len(vals) // 2], vals[0], vals[-1], len(vals))


def outlier(sid, n):
    """True when n sits far outside the run's own numbering - the only reason to doubt a number."""
    p = peers.get(sid)
    if not p or n is None:
        return False
    med, lo, hi, cnt = p
    if cnt < 3:
        return False           # too few peers to call anything an outlier
    span = max(hi - lo, 1.0)
    return n > hi + span * 2 or n < lo - span * 2
rows = list(con.execute("""
 SELECT i.Id, i.FileName, coalesce(i.PageCount,0), cd.IssueNo, cd.IsCollection, cd.Format, cd.IssueSource,
        i.SeriesId, i.Path
 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
 WHERE coalesce(i.IsExcluded,0) = 0"""))
print(f"{len(rows):,} comic items\n")

RX_YEAR_IN_NAME = re.compile(r"(?:\(|\b)(19\d{2}|20\d{2})(?:\)|\b)")
RX_LEAD_NUM = re.compile(r"^\s*(\d{1,3})[\s._-]")
RX_HASH = re.compile(r"#\s*(\d{1,4})\b")
RX_NUMERIC = re.compile(r"-?\d+(\.\d+)?")
SEP = chr(92)
# folders where a 250+pp file is normally NOT a collection of issues: a year of daily strips, an original
# graphic novel, a manga volume. Established by reading the population, not assumed.
NOT_A_COLLECTION = {"Webcomics and Strips", "Graphic Novels", "Manga", "FirstSecond", "Drawn & Quarterly",
                    "Top Shelf", "Fantagraphics", "Misc", "Europe Comics", "Cinebook", "NBM", "Humanoids",
                    "SelfMadeHero", "Nobrow"}
HITS = {}


def hit(key, item, why):
    HITS.setdefault(key, []).append((item[0], why, item[1][:64]))


for it in rows:
    iid, fn, pages, no, iscol, fmt, src, sid, path = it
    n = None
    if no is not None:
        try:
            n = float(str(no))
        except ValueError:
            n = None

    # 1. the YEAR coordinate — the number is a year AND that year appears in the file's own name
    if n is not None and 1900 <= n <= 2100 and float(int(n)) == n and outlier(sid, n):
        if str(int(n)) in (fn or ""):
            hit("issueNo is a YEAR in the filename AND an outlier on its own shelf", it, f"#{no}")

    # 2. the chronology SORT PREFIX — the number equals the filename's leading number, and the name also
    #    carries a different number after the title (the real issue)
    m = RX_LEAD_NUM.match(fn or "")
    if n is not None and m and float(m.group(1)) == n:
        rest = (fn or "")[m.end():]
        others = [int(x) for x in re.findall(r"(?<![\d.])(\d{1,4})(?![\d.])", rest)
                  if 1900 > int(x) != int(n)]
        if others and outlier(sid, n):
            hit("issueNo is the filename's LEADING sort prefix AND an outlier", it, f"#{no} vs {others[:4]}")

    # 3. a SUB-LINE number: the name has "Word #N" and the file is a big book
    if n is not None and pages >= 150 and iscol == 0:
        mm = RX_HASH.search(fn or "")
        if mm and float(mm.group(1)) == n:
            hit("big book numbered from a '#N' inside its own title", it, f"#{no} {pages}pp")

    # 4. absurd values
    # 1000000 is DC One Million and is REAL; so is any high number the shelf's own ladder supports.
    if n is not None and n != 1000000 and (n < 0 or n > 5000) and outlier(sid, n):
        hit("issueNo out of any plausible range for its own shelf", it, f"#{no}")

    # 5. flag, both directions.
    #    The 250pp test must be CATEGORY-AWARE or it is meaningless. A 365pp `Dennis The Menace (2011)` is
    #    one year of daily strips, an OGN is one book with no constituent issues, and a manga volume is the
    #    unit itself - `IsCollection = 0` is CORRECT for all three, and they were 906 of the 1,359 this
    #    check first reported. What is genuinely suspect is 250+ pages in a FLOPPY publisher's folder,
    #    where that size normally means a collection of numbered issues. Even there it is a lead: Nintendo
    #    Power really is a 270pp magazine issue, and `G.O.D.S. - Infinity Comic 001` is one issue at 268pp
    #    because a mobile rip inflates page count about fivefold (PLAN.md 14.12).
    if iscol == 1 and 0 < pages < 8:
        hit("flagged a COLLECTION but under 8 pages (variant-cover class)", it, f"{pages}pp")
    if iscol == 0 and pages >= 250:
        cat = (path or "").split(SEP)[5] if len((path or "").split(SEP)) > 5 else ""
        if cat in NOT_A_COLLECTION:
            hit("LEAD 250+pp single issue in a strip/OGN/manga folder (usually correct)", it, f"{pages}pp {cat}")
        else:
            hit("LEAD 250+pp single issue in a floppy publisher's folder", it, f"{pages}pp {cat}")

    # 6b. An issue number that is not a number cannot be compared, so containment can never place the file.
    #     These are NOT parse errors: `Flash v1 217 GL only` is a PARTIAL RIP - the Green Lantern story cut
    #     out of Flash #217 - and `18 (GL I Only)`, `22 (Edit)` and the 470 GCD `(X Story)` designations say
    #     the same thing. The leading number IS the issue; the parenthetical says the file is one story from
    #     it. Converting them would buy a coordinate and LOSE that fact, so it is a schema question (should
    #     ComicDetail carry a separate partial/story marker?) and not a data fix. A lead, deliberately.
    if no is not None and not RX_NUMERIC.fullmatch(str(no).strip()):
        hit("LEAD issueNo is not a number, so no range can reach this file (partial rips)", it, f"{no!r}")

    # 6. format contradicts the flag
    # A European HARDCOVER ALBUM really is about fifty-six pages - `Butterscotch 2 (2010 HC)` at 56pp and
    # `Click - Book 01 (HC)` at 55pp are correctly Format = Hardcover, and it was the page threshold that
    # was wrong about them, not the data. Only Tpb/Omnibus on a thin file, or an HC below album size.
    if iscol == 0 and ((fmt in (1, 3) and 0 < pages < 60) or (fmt == 2 and 0 < pages < 45)):
        hit("Format is TPB/HC/Omnibus on a single issue too thin to be one", it, f"fmt={fmt} {pages}pp")

# 7. one coordinate twice in one FOLDER (a folder is one run; a repeat there is a parse error, not a dupe)
folder_dupe = con.execute("""
 SELECT count(*) FROM (
   SELECT substr(i.Path, 1, length(i.Path) - length(i.FileName)) AS dir, cd.IssueNo
   FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
   WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IssueNo IS NOT NULL AND cd.IsCollection = 0
     AND coalesce(i.PageCount,0) >= 8
   GROUP BY dir, cd.IssueNo HAVING count(*) > 1)""").fetchone()[0]

order = sorted(HITS.items(), key=lambda kv: -len(kv[1]))
total = sum(len(v) for k, v in HITS.items() if not k.startswith("LEAD"))
for key, items in order:
    print(f"  {len(items):>6}  {key}")
    if VERBOSE:
        for iid, why, fn in items[:LIMIT]:
            print(f"            {iid:<8} {why:<22} {fn}")
        if len(items) > LIMIT:
            print(f"            ... and {len(items)-LIMIT} more")
print(f"  {folder_dupe:>6}  one issue number used twice inside a single FOLDER (informational)")
print(f"\n{total} suspect row(s) across {len(HITS)} class(es)")
