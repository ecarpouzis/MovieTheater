"""Census of the comic series insights (the ratings layer): who wrote the current row, how confident, how the ratings
are distributed, and how many file-holding comic shelves have none. Read-only.
usage: python scripts/books/rating_census.py [--db data/books/v2/books.db]
"""
import argparse, collections, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db")
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
shelves = c.execute("""SELECT s.Id, count(i.Id) FROM Series s JOIN Item i ON i.SeriesId=s.Id
    WHERE i.IsExcluded=0 AND i.Kind=0 GROUP BY s.Id""").fetchall()
files = dict(shelves)
print("file-holding comic shelves:", len(files), " comic items:", sum(files.values()))
cur = {r[0]: r[1:] for r in c.execute("""SELECT SubjectId, ModelId, Rank, Confidence, Rating, Id FROM Insight
    WHERE SubjectKind=1 AND IsCurrent=1""")}
have = [s for s in files if s in cur]
print("with a current insight:", len(have), f"({sum(files[s] for s in have)} items)",
      " without:", len(files) - len(have), f"({sum(n for s, n in files.items() if s not in cur)} items)")
print("\ncurrent insight by model (shelves / items):")
for m, n in collections.Counter(cur[s][0] for s in have).most_common(15):
    print(f"   {n:6}  {sum(files[s] for s in have if cur[s][0] == m):7}  {m}")
print("\nconfidence:", dict(collections.Counter(cur[s][2] for s in have)))
rated = [cur[s][3] for s in have if cur[s][3]]
print("rated:", len(rated), " null rating:", len(have) - len(rated))
bands = collections.Counter((r // 10) * 10 for r in rated)
print("rating bands:", dict(sorted(bands.items())))
print("clone-band insights (>=20M) current:", sum(1 for s in have if cur[s][4] >= 20_000_000))
# how many current insights pre-date the identity pass AND sit on shelves the identity pass changed
lib = c.execute("SELECT count(*) FROM Rating WHERE TargetKind=1 AND Source=1").fetchone()
print("\nRating rows by (TargetKind, Source):", c.execute("SELECT TargetKind, Source, count(*) FROM Rating GROUP BY 1,2").fetchall())
# shelves with an identity (CV/GCD) and their size
ided = c.execute("SELECT count(*) FROM Series WHERE CvVolumeId IS NOT NULL").fetchone()[0]
print("shelves with a CV volume:", ided)
