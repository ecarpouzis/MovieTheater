"""Which of the rescan's new collections still lack a judged range, split by whether a range would matter now.
A range nests issues only on the collection's OWN shelf, so a collection whose shelf holds live numbered issues is
where a missing range leaves issues loose; one on an issue-less shelf changes nothing today. Read-only.
usage: python containment_priority.py --first-new-id 245531 [--out <tsv>]
"""
import argparse, csv, sqlite3

ap = argparse.ArgumentParser(); ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--first-new-id", type=int, required=True)
ap.add_argument("--out")
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
rows = c.execute("""
SELECT i.Id, i.SeriesId, s.Name, i.FileName, i.PageCount,
       (SELECT count(*) FROM Item x JOIN ComicDetail xd ON xd.ItemId = x.Id
        WHERE x.SeriesId = i.SeriesId AND x.IsExcluded = 0 AND xd.IsCollection = 0 AND xd.IssueNo GLOB '[0-9]*') AS issues,
       (SELECT group_concat(xd.IssueNo, ',') FROM (SELECT xd.IssueNo FROM Item x JOIN ComicDetail xd ON xd.ItemId = x.Id
        WHERE x.SeriesId = i.SeriesId AND x.IsExcluded = 0 AND xd.IsCollection = 0 AND xd.IssueNo GLOB '[0-9]*'
        ORDER BY CAST(xd.IssueNo AS REAL) LIMIT 40) xd) AS nums
FROM Item i JOIN ComicDetail d ON d.ItemId = i.Id JOIN Series s ON s.Id = i.SeriesId
WHERE i.Id >= ? AND i.IsExcluded = 0 AND d.IsCollection = 1
  AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan sp WHERE sp.ItemId = i.Id AND sp.Source = 3 AND sp.IssueStart IS NOT NULL)
ORDER BY issues DESC, s.Name""", (a.first_new_id,)).fetchall()
matters = [r for r in rows if r[5] > 0]
print({"new collections without a judged range": len(rows), "on a shelf with live numbered issues": len(matters)})
if a.out:
    with open(a.out, "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f, delimiter="\t", lineterminator="\n")
        w.writerow(["ItemId", "SeriesId", "Shelf", "File", "Pages", "LiveIssues", "IssueNumbers"]); w.writerows(matters)
