"""A conflated shelf has one of two causes, and they need opposite fixes. Which is which?

  A. THE PARSE conflated them — every run produced the SAME ParsedSeriesKey, so the resolver never had a
     chance to tell them apart. Green Lantern: `_Completed Series\\02 Green Lantern v2 (1960)`,
     `\\03 Green Lantern v3 (1990)` and `\\10 Green Lantern v7 (2023)` all parse to "Green Lantern (1960)"
     because the series name comes from ComicInfo, which says "Green Lantern" for all of them.
     -> fix: books-series-split, giving each run its own key.

  B. A PROVIDER LINK folded them — the keys ARE distinct, and a bad ComicVine match points nine of them at
     one volume, so the resolver's canonical key is `cv:<id>` for all of them. Fairy Tail: nine keys, from
     "Fairy Tail - Blue Mistral" to "Fairy Tail Zero", every one linked to volume 46777.
     -> fix: books-series-clearlink on the wrong keys.

Getting this backwards would re-key rows whose keys were already right, or clear a link that was carrying
no weight. So count them before touching anything. Read-only.
"""
import collections
import sqlite3
import sys

HOT = sys.argv[1] if len(sys.argv) > 1 else r"F:\Work\MovieTheater\data\books\v2\books.db"
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

keys_by_series = collections.defaultdict(collections.Counter)
for sid, key in con.execute("""
        SELECT i.SeriesId, coalesce(cd.ParsedSeriesKey, '')
        FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0 AND i.SeriesId IS NOT NULL"""):
    keys_by_series[sid][key] += 1

cv = collections.defaultdict(dict)
for key, prov, pk, status in con.execute(
        "SELECT ParsedKey, Provider, ProviderKey, Status FROM SeriesKeyLink WHERE ProviderKey IS NOT NULL"):
    cv[key][prov] = (pk, status)

names = {r[0]: r[1] for r in con.execute("SELECT Id, coalesce(DisplayNameOverride, Name) FROM Series")}

multi_key, single_key = [], []
for sid, keys in keys_by_series.items():
    if len(keys) > 1:
        # do the distinct keys share one provider id? that is what folded them
        ids = {cv.get(k, {}).get(0, (None,))[0] for k in keys}
        ids.discard(None)
        multi_key.append((sid, keys, len(ids) == 1 and len(keys) > 1))
    else:
        single_key.append((sid, keys))

folded = [m for m in multi_key if m[2]]
print(f"series holding MORE THAN ONE ParsedSeriesKey: {len(multi_key)}")
print(f"   ...of which every key points at ONE provider volume (cause B, clearlink): {len(folded)}")
print(f"      files in them: {sum(sum(k.values()) for _, k, _ in folded)}")
print()
print("the 20 biggest cause-B shelves:")
for sid, keys, _ in sorted(folded, key=lambda m: -sum(m[1].values()))[:20]:
    print(f"  S{sid:<8} {sum(keys.values()):>5} files  {len(keys):>2} keys   {names.get(sid, '?')[:44]}")
    for k, n in keys.most_common(4):
        print(f"              {n:>5}  {k[:70]}")
