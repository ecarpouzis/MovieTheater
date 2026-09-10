"""One shelf, one screen: every collected edition with everything that has an opinion about it.

`python worksheet.py <sid> [sid...]`

Per edition — our file (pages, MB, volume ordinal), the LOCG RECORD's own self-description (title,
format, page count) so a mis-link is visible before its span is believed, LOCG's "Collects ..." sentence
verbatim, and the GCD / ComicVine / LOCG spans side by side. Then the shelf's issue ladder with page
counts, and which files each claim would swallow.

Nothing here decides anything. It is the packet §8 asks for, assembled from what is on this box.
"""
import collections
import os
import re
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prose import page  # noqa: E402

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
PREFIX = "\\\\Library\\Public\\5 - Comics\\"
SRC = {0: "LOCG", 1: "GCD", 2: "CV", 3: "CUR"}
NSRC = {0: "-", 1: "inferred", 2: "CV", 3: "GCD", 4: "LOCG", 5: "curated"}
RXN = re.compile(r"^\s*(\d{1,5})(?:\.(\d+))?\s*$")


def num(s):
    m = RXN.match(str(s) if s is not None else "")
    return None if not m else float(m.group(0))


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
for sid in (int(a) for a in sys.argv[1:] if not a.startswith("--")):
    name = con.execute("SELECT coalesce(DisplayNameOverride,Name) FROM Series WHERE Id=?", (sid,)).fetchone()
    rows = con.execute("""SELECT i.Id, i.Path, i.FileName, i.PageCount, i.FileSize, cd.IsCollection,
                                 cd.IssueNo, cd.VolumeNo, cd.Year, cd.FormatRaw
                          FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
                          WHERE i.SeriesId=? AND coalesce(i.IsExcluded,0)=0""", (sid,)).fetchall()
    spans = collections.defaultdict(dict)
    for iid, src, a, b, t, note in con.execute(
            """SELECT s.ItemId, s.Source, s.IssueStart, s.IssueEnd, s.EditionTitle, s.Note
               FROM CollectedEditionSpan s JOIN Item i ON i.Id=s.ItemId WHERE i.SeriesId=?""", (sid,)):
        spans[iid][src] = (a, b, t, note)
    nodes = {r[0]: r[1:] for r in con.execute(
        "SELECT ItemId, SpanStart, SpanEnd, SpanSource, ContainsCount FROM CollectionNode WHERE SeriesId=?", (sid,))}
    locg = {r[0]: str(r[1]).strip() for r in con.execute(
        """SELECT l.ItemId, l.ProviderKey FROM ItemProviderLink l JOIN Item i ON i.Id=l.ItemId
           WHERE i.SeriesId=? AND l.Provider=2 AND l.ProviderKey IS NOT NULL""", (sid,))}

    cols = [r for r in rows if r[5]]
    iss = [r for r in rows if not r[5]]
    ladder = sorted((num(r[6]), r[0], r[3]) for r in iss if num(r[6]) is not None)
    real = [x for x in ladder if (x[2] or 0) >= 10]
    print(f"\n{'=' * 118}\n== S{sid}  {name[0] if name else '?'} — {len(cols)} collected editions, "
          f"{len(iss)} issue files ({len(real)} of them >=10pp)")
    for f, c in collections.Counter(os.path.dirname(r[1] or "") for r in rows).most_common():
        print(f"   [{c:>4}] {f[len(PREFIX):] if f.startswith(PREFIX) else f}")

    for iid, path, fn, pc, fs, _, ino, vol, year, fmt in sorted(cols, key=lambda r: (r[7] or 0, r[2])):
        nd = nodes.get(iid)
        live = f"{NSRC.get(nd[2], '?')} {nd[0]}-{nd[1]} contains={nd[3]}" if nd and nd[0] is not None else "-"
        print(f"\n  [{iid:>7}] vol={str(vol):>4} {str(pc):>5}pp {round((fs or 0) / 1048576):>4}MB "
              f"{(fmt or '')[:12]:<12} {fn[:74]}")
        print(f"          live node: {live}")
        lid = locg.get(iid)
        if lid:
            title, lfmt, lpages, col = page(lid)
            print(f"          locg {lid:<9} [{lfmt[:26]}] {title[:60]}")
            if col:
                print(f"            COLLECTS: {col[:400]}")
        for src in sorted(spans.get(iid, {})):
            a, b, t, note = spans[iid][src]
            rng = "-" if a is None else f"#{a:g}-{b:g}"
            hit = [] if a is None else [x for x in real if a <= x[0] <= b]
            print(f"          {SRC[src]:>7}: {rng:<12} {(t or '')[:40]:<40} swallows {len(hit):>3} files"
                  f"{('  | ' + note[:90]) if note else ''}")

    print(f"\n  -- LADDER: {len(ladder)} numbered issue files --")
    if ladder:
        cnt = collections.Counter(x[0] for x in ladder)
        dup = sorted(k for k, v in cnt.items() if v > 1)
        have = sorted({x[0] for x in ladder})
        runs, s = [], None
        for i, v in enumerate(have):
            if s is None:
                s = v
            if i + 1 == len(have) or have[i + 1] != v + 1:
                runs.append((s, v))
                s = None
        print(f"     present: " + ", ".join(f"{a:g}-{b:g}" if a != b else f"{a:g}" for a, b in runs[:40]))
        if dup:
            print(f"     numbers used more than once: {[f'{d:g}' for d in dup[:30]]}")
    unn = [r for r in iss if num(r[6]) is None]
    if unn:
        print(f"     {len(unn)} issue files carry no usable number, e.g. "
              + "; ".join(r[2][:44] for r in unn[:4]))
