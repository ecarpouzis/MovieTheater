"""Just the collected-edition ids of a shelf, in file order, comma-joined — for feeding sheet2.py."""
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
for sid in (int(a) for a in sys.argv[1:] if not a.startswith("--")):
    rows = con.execute("""
        SELECT i.Id FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded,0) = 0 AND cd.IsCollection = 1
        ORDER BY i.FileName""", (sid,)).fetchall()
    print(f"S{sid} ({len(rows)}): " + ",".join(str(r[0]) for r in rows))
