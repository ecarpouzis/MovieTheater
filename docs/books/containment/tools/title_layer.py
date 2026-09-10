"""How many Series are runs of a shared TITLE, and can the title be labelled without inference?

The stack is keyed at RUN level everywhere that matters — CollectionNode, ReadingOrderEntry and
Series.CvVolumeId (ComicVine models one volume per run, and no two Series share a volume). Issue numbers
restart per run, which is exactly why a conflated shelf cannot hold a correct range. So Series = run is
right, and what is missing sits ABOVE it: the title that a set of runs belongs to.

The label is not a guess for anything the split produced: `books-series-split` writes the key
`<Title> v<N> (<Year>)`, so the title is the stem before the volume marker, exactly. For series nobody
split, the title is the name with a trailing `(YYYY)` removed. This reports how much that groups, and
what it would leave ungrouped, so the layer can be judged before it is built.

Read-only.
"""
import collections
import re
import sqlite3

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
RX_RUN = re.compile(r"^(?P<title>.+?)\s+v(?:ol(?:ume)?)?\.?\s*\d{1,3}(?:\.\d+)?\s*\((?:19|20)\d{2}\)\s*$", re.I)
RX_YEAR = re.compile(r"\s*\((?:19|20)\d{2}\)\s*$")

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
    SELECT s.Id, coalesce(s.DisplayNameOverride, s.Name), s.Franchise, s.ParsedKey,
           (SELECT count(*) FROM Item i WHERE i.SeriesId = s.Id AND coalesce(i.IsExcluded,0)=0)
    FROM Series s WHERE s.CanonicalKey NOT LIKE 'book:%'""").fetchall()


def title_of(name, parsed):
    for candidate in (parsed or "", name or ""):
        m = RX_RUN.match(candidate.strip())
        if m:
            return m.group("title").strip(), True          # exact: the split wrote this shape
    return RX_YEAR.sub("", (name or "").strip()).strip(), False


groups = collections.defaultdict(list)
exact = 0
for sid, name, franchise, parsed, nfiles in rows:
    t, is_exact = title_of(name, parsed)
    exact += is_exact
    groups[t].append((sid, name, franchise, nfiles, is_exact))

multi = {t: g for t, g in groups.items() if len(g) > 1 and t}
print(f"comic Series rows                                : {len(rows):>6}")
print(f"  named in the split's exact `<Title> v<N> (YYYY)` shape : {exact:>6}")
print(f"distinct titles                                  : {len(groups):>6}")
print(f"titles holding MORE THAN ONE run                 : {len(multi):>6}"
      f"   covering {sum(len(g) for g in multi.values())} Series and "
      f"{sum(n for g in multi.values() for _, _, _, n, _ in g)} files")
missing_fr = sum(1 for g in multi.values() for _, _, f, _, _ in g if not f)
print(f"  ...Series in them carrying NO franchise        : {missing_fr:>6}  <- what my splits dropped")

print("\nthe 12 largest multi-run titles:")
for t, g in sorted(multi.items(), key=lambda kv: -sum(x[3] for x in kv[1]))[:12]:
    fr = {x[2] for x in g if x[2]}
    print(f"  {t[:40]:<40} {len(g)} runs, {sum(x[3] for x in g):>5} files   franchise={sorted(fr) or '(none)'}")
    for sid, name, franchise, nfiles, is_exact in sorted(g, key=lambda x: -x[3])[:4]:
        print(f"      S{sid:<7} {nfiles:>5} files  {'exact' if is_exact else 'inferred':<8} {name[:46]}")
