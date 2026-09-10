"""What a de-duplication would DO to this shelf, and on whose word.

`python shelf_risk.py <seriesId> [<seriesId> ...] [--limit=N]`

For every collected edition on the shelf: the file as it is on disk (pages, MB, the volume ordinal the
filename carries), the range the LIVE node asserts and WHO said it, every provider's competing claim,
and — the part that matters — the issue FILES we hold that fall inside that range, because those are
the files a de-duplication would call redundant. Then the issue ladder itself, so the tiling is visible.
"""
import collections
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
PREFIX = "\\\\Library\\Public\\5 - Comics\\"
SRC = {0: "LOCG", 1: "GCD", 2: "CV", 3: "CURATED"}
NSRC = {0: "none", 1: "inferred", 2: "CV", 3: "GCD", 4: "LOCG", 5: "curated"}
RX = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")


def num(s):
    m = RX.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


args = [a for a in sys.argv[1:] if not a.startswith("--")]
limit = 400
for a in sys.argv[1:]:
    if a.startswith("--limit"):
        limit = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
for sid in (int(a) for a in args):
    name = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id=?", (sid,)).fetchone()
    rows = con.execute("""
        SELECT i.Id, i.Path, i.FileName, i.PageCount, i.FileSize, cd.IsCollection, cd.IssueNo,
               cd.VolumeNo, cd.Year, cd.FormatRaw, cd.Publisher
        FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
        WHERE i.SeriesId=? AND coalesce(i.IsExcluded,0)=0
        ORDER BY i.Path, i.FileName""", (sid,)).fetchall()
    nodes = {r[0]: r[1:] for r in con.execute("""
        SELECT ItemId, SpanStart, SpanEnd, SpanSource, SpanLabel, ContainsCount, TrackRole, ParentItemId
        FROM CollectionNode WHERE SeriesId=?""", (sid,))}
    spans = collections.defaultdict(dict)
    for iid, src, a, b, t, note, conf in con.execute("""
            SELECT s.ItemId, s.Source, s.IssueStart, s.IssueEnd, s.EditionTitle, s.Note, s.Confidence
            FROM CollectedEditionSpan s JOIN Item i ON i.Id=s.ItemId WHERE i.SeriesId=?""", (sid,)):
        spans[iid][src] = (a, b, t, note, conf)

    cols = [r for r in rows if r[5]]
    iss = [r for r in rows if not r[5]]
    ladder = sorted((num(r[6]), r[0], r[3], r[2]) for r in iss if num(r[6]) is not None)
    print(f"\n{'=' * 118}\n== S{sid}  {name[0] if name else '?'}   "
          f"{len(cols)} collected editions, {len(iss)} issue files "
          f"({sum(1 for r in iss if num(r[6]) is None)} unnumbered)")

    folders = collections.Counter(os.path.dirname(r[1] or "") for r in rows)
    for f, n in folders.most_common():
        print(f"   [{n:>4}] {f[len(PREFIX):] if f.startswith(PREFIX) else f}")

    print(f"\n  -- COLLECTED EDITIONS --")
    for iid, path, fn, pc, fs, _, ino, vol, year, fmt, pub in sorted(cols, key=lambda r: (r[7] or 0, r[2])):
        nd = nodes.get(iid)
        live = "-"
        if nd and nd[0] is not None:
            live = f"{NSRC.get(nd[2], nd[2])} {int(nd[0])}-{int(nd[1])} contains={nd[4]}"
        print(f"    [{iid:>7}] vol={str(vol):>4} #{str(ino):>6} {str(pc):>5}pp "
              f"{round((fs or 0) / 1048576, 1):>6}MB {fmt or ''} | {fn[:78]}")
        print(f"              live: {live}")
        for src in sorted(spans.get(iid, {})):
            a, b, t, note, conf = spans[iid][src]
            aa = "" if a is None else f"#{a:g}-{b:g}"
            print(f"              {SRC[src]:>8}: {aa:<12} conf={conf} {(t or '')[:44]}"
                  f"{('  NOTE: ' + note[:120]) if note else ''}")
        if nd and nd[0] is not None:
            inside = [x for x in ladder if nd[0] <= x[0] <= nd[1]]
            if inside:
                print(f"              would swallow {len(inside)} files: "
                      + ", ".join(f"#{x[0]:g}({x[2]}pp)" for x in inside[:26]))

    print(f"\n  -- ISSUE LADDER ({len(ladder)} numbered) --")
    if ladder:
        cnt = collections.Counter(x[0] for x in ladder)
        dupes = sorted(k for k, v in cnt.items() if v > 1)
        lo, hi = ladder[0][0], ladder[-1][0]
        have = {x[0] for x in ladder}
        gaps = [n for n in range(int(lo), int(hi) + 1) if n not in have]
        print(f"     range #{lo:g}..#{hi:g}   {len(have)} distinct   "
              f"dupes={dupes[:20]}   missing={gaps[:40]}{'...' if len(gaps) > 40 else ''}")
        shown = 0
        for n, iid, pc, fn in ladder:
            if shown >= limit:
                print(f"     ... {len(ladder) - shown} more")
                break
            print(f"     #{n:<8g} [{iid:>7}] {str(pc):>4}pp {fn[:80]}")
            shown += 1
    unn = [r for r in iss if num(r[6]) is None]
    if unn:
        print(f"\n  -- UNNUMBERED ISSUE FILES ({len(unn)}) --")
        for r in unn[:40]:
            print(f"     [{r[0]:>7}] #{str(r[6]):>10} {str(r[3]):>4}pp {r[2][:80]}")
