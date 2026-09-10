"""The shelf queue: where containment actually matters, worst first.

A shelf earns a place when it holds BOTH collected editions and single issues — those are the shelves
where a range decides whether a file is redundant, and where a wrong one costs data. For each it reports
what is already decided, what a provider is currently answering for, and what is silent, so the queue can
be worked from the top down and progress is visible.

`python worklist.py [--done S1,S2,...] [--limit N]`
"""
import collections
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
DONE = set()
LIMIT = 60
for a in sys.argv[1:]:
    if a.startswith("--done="):
        DONE = {int(x) for x in a.split("=", 1)[1].split(",") if x.strip()}
    elif a.startswith("--limit="):
        LIMIT = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

rows = con.execute("""
    SELECT i.SeriesId, cd.IsCollection, n.TrackRole, n.SpanSource, n.ContainsCount,
           coalesce(s.DisplayNameOverride, s.Name)
    FROM Item i
    JOIN ComicDetail cd ON cd.ItemId = i.Id
    LEFT JOIN CollectionNode n ON n.ItemId = i.Id
    LEFT JOIN Series s ON s.Id = i.SeriesId
    WHERE i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0 AND i.SeriesId IS NOT NULL""").fetchall()

per = collections.defaultdict(lambda: dict(name=None, issues=0, cols=0, containers=0,
                                           judged=0, provider=0, silent=0))
for sid, iscol, role, ssrc, cnt, name in rows:
    d = per[sid]
    d["name"] = name
    if iscol:
        d["cols"] += 1
    else:
        d["issues"] += 1
    if role == 1:
        d["containers"] += 1
        if ssrc == 5:
            d["judged"] += 1
        elif ssrc in (2, 3, 4):
            d["provider"] += 1
        else:
            d["silent"] += 1

queue = [(sid, d) for sid, d in per.items()
         if d["issues"] > 0 and d["containers"] > 0 and sid not in DONE]
# worst first: a provider answering for a shelf is the dangerous state, silence the wasteful one
queue.sort(key=lambda kv: -(kv[1]["provider"] * 3 + kv[1]["silent"] + kv[1]["issues"] / 50))

tot = dict(shelves=len(queue),
           provider=sum(d["provider"] for _, d in queue),
           silent=sum(d["silent"] for _, d in queue),
           judged=sum(d["judged"] for _, d in queue))
alldone = [(sid, d) for sid, d in per.items() if sid in DONE]
print(f"shelves still to work: {tot['shelves']}   (containers: {tot['provider']} answered by a provider, "
      f"{tot['silent']} silent, {tot['judged']} judged)")
if alldone:
    print(f"shelves already decided: {len(alldone)} "
          f"({sum(d['judged'] for _, d in alldone)} judged containers)")
print()
print(f"  {'series':<9} {'iss':>5} {'col':>4} {'prov':>5} {'sil':>4} {'jdg':>4}  name")
for sid, d in queue[:LIMIT]:
    print(f"  S{sid:<8} {d['issues']:>5} {d['cols']:>4} {d['provider']:>5} {d['silent']:>4} {d['judged']:>4}  "
          f"{(d['name'] or '?')[:56]}")
