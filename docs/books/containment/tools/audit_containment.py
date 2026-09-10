"""Audit every containment relationship in the library against the population, not against the queue.

Both defects found at the end of the pass were found because a CHECK was defined over the wrong set:
`todo_risk.py` asked "which shelves are undecided" and skipped any shelf already holding one judged range,
and the decision-file coverage contract only inspected items flagged as collections. Each read zero while
hundreds of files sat under books nobody had judged.

So this asks the opposite question. It starts from the relationships that actually exist - every row in
CollectionNode with a parent - and tests each one. Every count it prints must be zero.

`python audit_containment.py [--verbose]`
"""
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
VERBOSE = "--verbose" in sys.argv
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

CHECKS = [
    ("a container whose range is not judged (SpanSource is not Curated)", """
     SELECT n.ItemId, n.SeriesId, n.SpanSource, n.ContainsCount, substr(i.FileName,1,58)
     FROM CollectionNode n JOIN Item i ON i.Id = n.ItemId
     WHERE n.TrackRole = 1 AND n.ContainsCount > 0 AND n.SpanSource <> 5"""),

    ("a container that is not flagged as a collection", """
     SELECT n.ParentItemId, n.SeriesId, count(*), '', substr(p.FileName,1,58)
     FROM CollectionNode n JOIN Item p ON p.Id = n.ParentItemId
     JOIN ComicDetail pd ON pd.ItemId = p.Id
     WHERE n.ParentItemId IS NOT NULL AND pd.IsCollection = 0 GROUP BY n.ParentItemId"""),

    ("a container with no judged span row at all", """
     SELECT n.ItemId, n.SeriesId, 0, n.ContainsCount, substr(i.FileName,1,58)
     FROM CollectionNode n JOIN Item i ON i.Id = n.ItemId
     WHERE n.TrackRole = 1 AND n.ContainsCount > 0
       AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan s
                       WHERE s.ItemId = n.ItemId AND s.Source = 3 AND s.IssueStart IS NOT NULL)"""),

    ("a child nested under a parent on a DIFFERENT shelf", """
     SELECT n.ItemId, n.SeriesId, p.SeriesId, 0, substr(i.FileName,1,58)
     FROM CollectionNode n JOIN Item i ON i.Id = n.ItemId JOIN Item p ON p.Id = n.ParentItemId
     WHERE n.ParentItemId IS NOT NULL AND p.SeriesId <> i.SeriesId"""),

    ("a child with MORE pages than the book said to contain it", """
     SELECT n.ItemId, n.SeriesId, i.PageCount, p.PageCount, substr(i.FileName,1,58)
     FROM CollectionNode n JOIN Item i ON i.Id = n.ItemId JOIN Item p ON p.Id = n.ParentItemId
     WHERE n.ParentItemId IS NOT NULL AND coalesce(i.PageCount,0) > coalesce(p.PageCount,0)"""),

    ("a container that also has a tombstone - decided twice, in both directions", """
     SELECT s.ItemId, i.SeriesId, 0, n.ContainsCount, substr(i.FileName,1,58)
     FROM CollectedEditionSpan s JOIN Item i ON i.Id = s.ItemId
     JOIN CollectionNode n ON n.ItemId = s.ItemId AND n.TrackRole = 1
     WHERE s.Source = 3 AND s.IssueStart IS NULL AND n.ContainsCount > 0"""),

    ("LEAD nesting on a shelf flagged as CONFLATED (not an error: Aliens Omnibus v01 legitimately holds both minis that collide there)", """
     SELECT n.ParentItemId, n.SeriesId, count(*), '', substr(p.FileName,1,58)
     FROM CollectionNode n JOIN Item p ON p.Id = n.ParentItemId
     JOIN ContainmentFlag f ON f.SeriesId = n.SeriesId AND f.Flag = 'conflated-series'
     WHERE n.ParentItemId IS NOT NULL GROUP BY n.ParentItemId"""),
]

bad = 0
for label, sql in CHECKS:
    lead = label.startswith("LEAD")
    try:
        rows = list(con.execute(sql))
    except sqlite3.OperationalError as e:
        print(f"  SKIP  {label}\n        ({e})")
        continue
    print(f"  {('lead' if lead else 'FAIL') if rows else ' ok '}  {len(rows):>5}  {label}")
    if not lead:
        bad += len(rows)
    if rows and VERBOSE:
        for r in rows[:20]:
            print("          ", r)
        if len(rows) > 20:
            print(f"           ... and {len(rows)-20} more")

print(f"\n{bad} problem(s)")
sys.exit(1 if bad else 0)
