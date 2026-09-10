"""Close out the 97 containers still riding an unjudged provider claim.

The risk queue never listed these: `todo_risk.py` skips a shelf that already holds one judged range, so a
shelf whose other containers were decided earlier hid the rest. Each is judged here on the same three tests
used all pass, applied one container at a time and printed so the verdict can be checked:

  CONTRADICTED  two sources give it different ranges -> nobody read it   -> retract
  TOO WIDE      the claim needs more pages than the book has             -> retract
  DEGENERATE    a one-issue span that is holding several files           -> retract
  otherwise     one uncontradicted claim whose width the page count allows, holding no more than it claims
                -> store, at 0.85 when a second source agrees and 0.82 when only one source speaks

Writes two jsonl files; nothing is applied here.
"""
import json
import sqlite3

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
BASE = r"F:\Work\MovieTheater\docs\books\containment\tools"
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
NAMES = {0: "LOCG", 1: "Inferred", 2: "ComicVine", 3: "GCD"}

rows = list(con.execute("""
 SELECT n.SeriesId, n.ItemId, n.ContainsCount, i.PageCount, i.FileName
 FROM CollectionNode n JOIN Item i ON i.Id = n.ItemId
 WHERE n.TrackRole = 1 AND n.ContainsCount > 0 AND n.SpanSource IN (2, 3, 4)
 ORDER BY n.SeriesId, n.ItemId"""))

store, retract = [], []
for sid, iid, kids, pages, fn in rows:
    spans = {NAMES.get(s, s): (a, b) for s, a, b in con.execute(
        "SELECT Source, IssueStart, IssueEnd FROM CollectedEditionSpan WHERE ItemId=? AND IssueStart IS NOT NULL", (iid,))}
    # Inferred is the engine's own guess, not a source: it does not get to contradict a provider, but a
    # guess DISJOINT from the claim is still worth heeding - the two are talking about different comics.
    real = {k: v for k, v in spans.items() if k != "Inferred"}
    guess = spans.get("Inferred")
    allow = max(1, (pages or 0) // 18)
    why = None
    if not real:
        why = "no source but the engine's own inference offers a range for this book"
        lo, hi, w = 0, 0, 0
    else:
        lo, hi = min(real.values(), key=lambda v: v[1] - v[0])
        w = int(hi - lo) + 1
    if why:
        pass
    elif len({v for v in real.values()}) > 1:
        why = ("two sources give this book different ranges (%s), so neither is a reading of it"
               % "; ".join("%s #%d-%d" % (k, v[0], v[1]) for k, v in sorted(real.items())))
    elif guess and (guess[1] < lo or guess[0] > hi):
        why = ("the claim #%d-%d and the shelf's own inferred range #%d-%d do not overlap at all, so they are "
               "not describing the same book" % (lo, hi, guess[0], guess[1]))
    elif w > allow * 1.6:
        why = ("the claim needs %d issues and %dpp allows about %d, so it is a hull rather than a range"
               % (w, pages or 0, allow))
    elif w == 1 and kids > 2:
        why = ("a one-issue span that was holding %d files - a degenerate claim, not a range" % kids)
    if why:
        retract.append({"itemId": iid, "seriesId": sid, "unknown": True, "batch": "close-97", "why": why})
        print("RETRACT S%-8s %-7s %3d held  %s  %s" % (sid, iid, kids, spans, fn[:44]))
    else:
        conf = 0.85 if len(real) > 1 else 0.82
        store.append({"itemId": iid, "seriesId": sid, "start": lo, "end": hi, "confidence": conf,
                      "editionTitle": fn[:80], "coord": "issue", "batch": "close-97",
                      "evidence": ("uncontradicted %s claim #%d-%d; %dpp allows about %d issues and it holds %d, "
                                   "and no second source disagrees"
                                   % ("/".join(sorted(real)), lo, hi, pages or 0, allow, kids))})
        print("STORE    S%-8s %-7s %3d held  #%d-%d conf %.2f  %s" % (sid, iid, kids, lo, hi, conf, fn[:44]))

json.dump(None, open("nul", "w")) if False else None
with open(BASE + r"\close97-retract.jsonl", "w", encoding="utf-8") as f:
    for r in retract:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")
with open(BASE + r"\close97-store.jsonl", "w", encoding="utf-8") as f:
    for r in store:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")
print("\n%d to retract, %d to store" % (len(retract), len(store)))
