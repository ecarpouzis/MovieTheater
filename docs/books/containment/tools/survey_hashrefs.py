"""A `#N` inside brackets is a CROSS-REFERENCE, not this file's number.

`ExtractIssueNo` takes the RIGHTMOST explicit `#N`, which is right when a name repeats its own number and
wrong the moment the name cites another publication:

    2000AD #1002b (JDMeg. #3.20-3.25) Judge Dredd (America II) - Fading of the Light   ->  3
    2000AD #1033 Judge Dredd - He Came from Outer Space                                -> 1033

Both files are progs of the same run. The first got 3 because the Judge Dredd Megazine's volume.issue
citation sits to the right of the prog number, and hundreds of Megazine citations start "3.", so hundreds
of files collapsed onto issue 3. Reading the shelf shows it at a glance; no provider was ever going to.

This SURVEYS the shape across the library and writes nothing.
"""
import collections
import re
import sqlite3
import sys

HOT = sys.argv[1] if len(sys.argv) > 1 else r"F:\Work\MovieTheater\data\books\v2\books.db"

RX_HASHNUM = re.compile(r"#\s*0*(\d+)")
RX_NUM = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")
EXT = re.compile(r"\.(cbr|cbz|cb7|cbt|pdf|epub)$", re.I)


def depth_map(s):
    """Bracket depth at each character position: ( [ { all count."""
    out, d = [], 0
    for ch in s:
        if ch in "([{":
            d += 1
            out.append(d)
        elif ch in ")]}":
            out.append(d)
            d = max(0, d - 1)
        else:
            out.append(d)
    return out


def outermost_hash(stem):
    """The rightmost `#N` that is NOT inside brackets, and the rightmost overall."""
    depth = depth_map(stem)
    free, any_ = None, None
    for m in RX_HASHNUM.finditer(stem):
        any_ = m.group(1)
        if depth[m.start()] == 0:
            free = m.group(1)
    return free, any_


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
    SELECT i.Id, i.SeriesId, i.FileName, cd.IssueNo, cd.IsCollection, cd.IssueSource
    FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
    WHERE i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0""").fetchall()

affected = []
for iid, sid, fn, ino, iscol, isrc in rows:
    stem = EXT.sub("", fn or "")
    free, any_ = outermost_hash(stem)
    if free is None or any_ is None or free == any_:
        continue                       # no bracketed reference, or it agrees
    m = RX_NUM.match(str(ino) if ino is not None else "")
    stored = str(int(float(m.group(0)))) if m else None
    if stored is None or stored == str(int(free)):
        continue                       # already right
    affected.append((iid, sid, stored, str(int(free)), iscol, isrc, fn))

by_series = collections.Counter(a[1] for a in affected)
by_source = collections.Counter(a[5] for a in affected)
print(f"files whose stored number came from a BRACKETED cross-reference: {len(affected)}")
print(f"  across {len(by_series)} series")
print(f"  by IssueSource (1=ComicInfo 3=filename): {dict(by_source)}")
print(f"  of them collected editions: {sum(1 for a in affected if a[4])}")

names = {r[0]: r[1] for r in con.execute("SELECT Id, coalesce(DisplayNameOverride, Name) FROM Series")}
print("\n--- the shelves it hits ---")
for sid, n in by_series.most_common(20):
    print(f"  S{sid:<7} {n:>5} files   {names.get(sid, '?')}")

print("\n--- a sample of the change ---")
for iid, sid, stored, free, iscol, isrc, fn in affected[:20]:
    print(f"  [{iid:>7}] S{sid:<7} {stored:>6} -> {free:<6} {fn[:74]}")
