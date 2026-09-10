"""After a containment rebuild: does any container now hold MORE than the book could contain?

The point of every guard in this pass is that over-claiming loses files and under-claiming only fails to
save space. So the check after a change is not "did nesting go up" — it is "did anything nest more issues
than the book prints".

For each container with a judged range, compare the DISTINCT issue coordinates it now nests against the
size of its range. Duplicates are expected and fine: a shelf holding three rips of #7 nests three files for
one coordinate. More distinct coordinates than the range holds is not fine, and is listed.

Read-only.
"""
import collections
import sqlite3

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

rows = con.execute("""SELECT n.ParentItemId, s.IssueStart, s.IssueEnd, cd.IssueNo, i.FileName
                      FROM CollectionNode n
                      JOIN CollectedEditionSpan s ON s.ItemId = n.ParentItemId AND s.Source = 3
                      JOIN Item i ON i.Id = n.ItemId
                      JOIN ComicDetail cd ON cd.ItemId = i.Id
                      WHERE n.ParentItemId IS NOT NULL AND s.IssueStart IS NOT NULL""").fetchall()

by_parent = collections.defaultdict(lambda: [None, None, set(), 0])
for pid, a, b, no, fn in rows:
    e = by_parent[pid]
    e[0], e[1] = a, b
    e[3] += 1
    try:
        e[2].add(float(no))
    except (TypeError, ValueError):
        pass

over = []
for pid, (a, b, coords, files) in by_parent.items():
    size = int(b - a + 1)
    outside = {c for c in coords if c < a or c > b}
    if len(coords) > size or outside:
        over.append((pid, a, b, size, len(coords), len(outside), files))

print(f"{len(by_parent)} container(s) hold contents; {sum(v[3] for v in by_parent.values())} files nested")
print(f"containers holding MORE distinct coordinates than their range, or coordinates OUTSIDE it: {len(over)}")
for pid, a, b, size, got, outside, files in sorted(over, key=lambda t: -t[4])[:15]:
    fn = con.execute("SELECT FileName FROM Item WHERE Id = ?", (pid,)).fetchone()[0]
    print(f"   {pid:<8} claims #{a:g}-{b:g} ({size}) but nests {got} distinct "
          f"({outside} outside), {files} files  {fn[:48]}")
