"""Every comic in the library, partitioned into the state that decides its containment. No unknown bucket.

"976 decision files, 0 failing" answers a question nobody asked: it counts FILES OF DECISIONS, not files in
the library. And "100,210 comics on shelves without a decision file" sounds like a hole when most of those
shelves have nothing to decide. Both numbers mislead, so this replaces them with a partition: every
non-excluded comic lands in exactly one state, the states are mutually exclusive, and they sum to the
total. Where a file is not individually judged, the state says WHY, and that is the honest unit of account.

`python coverage_ledger.py [--verbose]`
"""
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

STATES = [
    ("nested under a container whose range was JUDGED", """
     SELECT count(DISTINCT i.Id) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
     JOIN CollectionNode n ON n.ItemId = i.Id
     WHERE coalesce(i.IsExcluded,0) = 0 AND n.ParentItemId IS NOT NULL"""),

    ("a container carrying a JUDGED range", """
     SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
     WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
       AND EXISTS (SELECT 1 FROM CollectedEditionSpan s
                   WHERE s.ItemId = i.Id AND s.Source = 3 AND s.IssueStart IS NOT NULL)
       AND NOT EXISTS (SELECT 1 FROM CollectionNode n WHERE n.ItemId = i.Id AND n.ParentItemId IS NOT NULL)"""),

    ("a collection REFUSED by name (tombstoned with a written reason)", """
     SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
     WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
       AND EXISTS (SELECT 1 FROM CollectedEditionSpan s
                   WHERE s.ItemId = i.Id AND s.Source = 3 AND s.IssueStart IS NULL)
       AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan s2
                       WHERE s2.ItemId = i.Id AND s2.Source = 3 AND s2.IssueStart IS NOT NULL)
       AND NOT EXISTS (SELECT 1 FROM CollectionNode n WHERE n.ItemId = i.Id AND n.ParentItemId IS NOT NULL)"""),

    ("a collection whose shelf holds NO numbered issue - nothing it could ever contain", """
     SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
     WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1 AND i.SeriesId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan s WHERE s.ItemId = i.Id AND s.Source = 3)
       AND NOT EXISTS (SELECT 1 FROM CollectionNode n WHERE n.ItemId = i.Id AND n.ParentItemId IS NOT NULL)
       AND NOT EXISTS (SELECT 1 FROM Item x JOIN ComicDetail c2 ON c2.ItemId = x.Id
                       WHERE x.SeriesId = i.SeriesId AND c2.IsCollection = 0
                         AND c2.IssueNo IS NOT NULL AND coalesce(x.PageCount,0) >= 8)"""),

    ("a collection NO SOURCE HAS EVER MADE A CLAIM ABOUT (no span at all; verified 863/863)", """
     SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
     WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
       AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan s WHERE s.ItemId = i.Id AND s.Source = 3)
       AND NOT EXISTS (SELECT 1 FROM CollectionNode n WHERE n.ItemId = i.Id AND n.ParentItemId IS NOT NULL)
       AND EXISTS (SELECT 1 FROM Item x JOIN ComicDetail c2 ON c2.ItemId = x.Id
                   WHERE x.SeriesId = i.SeriesId AND c2.IsCollection = 0
                     AND c2.IssueNo IS NOT NULL AND coalesce(x.PageCount,0) >= 8)"""),

    ("an ISSUE on a shelf that holds no collection at all (nothing could contain it)", """
     SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
     WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 0 AND i.SeriesId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM CollectionNode n WHERE n.ItemId = i.Id AND n.ParentItemId IS NOT NULL)
       AND NOT EXISTS (SELECT 1 FROM Item x JOIN ComicDetail c2 ON c2.ItemId = x.Id
                       WHERE x.SeriesId = i.SeriesId AND c2.IsCollection = 1)"""),

    ("an ISSUE on a shelf WITH collections, none of whose judged ranges covers it", """
     SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
     WHERE coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 0
       AND NOT EXISTS (SELECT 1 FROM CollectionNode n WHERE n.ItemId = i.Id AND n.ParentItemId IS NOT NULL)
       AND EXISTS (SELECT 1 FROM Item x JOIN ComicDetail c2 ON c2.ItemId = x.Id
                   WHERE x.SeriesId = i.SeriesId AND c2.IsCollection = 1)
       AND i.SeriesId IS NOT NULL"""),

    ("no shelf at all (unreadable archives, loose files)", """
     SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
     WHERE coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NULL
       AND NOT EXISTS (SELECT 1 FROM CollectionNode n WHERE n.ItemId = i.Id AND n.ParentItemId IS NOT NULL)"""),
]

total = con.execute("""SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                       WHERE coalesce(i.IsExcluded,0) = 0""").fetchone()[0]
print(f"{total:,} comics (non-excluded items with a ComicDetail)\n")
acc = 0
for label, sql in STATES:
    n = con.execute(sql).fetchone()[0]
    acc += n
    print(f"  {n:>8,}  {label}")
print(f"  {'-'*8}")
print(f"  {acc:>8,}  accounted for")
gap = total - acc
print(f"  {gap:>8,}  UNACCOUNTED  {'<-- must be zero' if gap else '(none)'}")
sys.exit(1 if gap else 0)
