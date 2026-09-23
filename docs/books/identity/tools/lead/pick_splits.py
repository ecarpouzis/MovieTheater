"""The lead's split-batch picker: split-needed shelves not yet handed out, smallest first. Read-only.

    python pick_splits.py <min files> <max files> <cap>     -> prints a count line, then a comma list for --only

`next_batch.py --splits` hands shelves out in its own order, which starts with the biggest (Judge Dredd, 1,784 lines),
so the lead picks by size and passes `--only <list>`, sizing with `--dry-run` to the ~1,400-line budget. Shelves
already emitted in a P- batch are skipped HERE; `next_batch --splits` itself re-admits a shelf re-flagged split-needed
after its last P- packet (TOOLS_TODO 39b) — add those by hand to the --only list (it prints them as "re-admitted").
S19456 (Ra's al Ghul) waits on an issue-number fix, not a split — leave it out.
"""
import json
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import idbase  # noqa: E402

decides, winner, _, _ = idbase.scan_decisions()
c = sqlite3.connect(r"file:F:\Work\MovieTheater\data\books\v2\books.db?mode=ro", uri=True)
size = dict(c.execute("SELECT SeriesId, count(*) FROM Item WHERE SeriesId IS NOT NULL GROUP BY SeriesId"))
st = json.load(open(os.path.join(idbase.IDENTITY if hasattr(idbase, "IDENTITY") else
                                 os.path.dirname(idbase.DECISIONS), "state.json"), encoding="utf-8"))
done = set()
sp = st.get("splits", [])
for b in (sp if isinstance(sp, list) else sp.get("batches", [])):
    done.update(b.get("ids", []))
sids = [s for s, f in winner.items() if "split-needed" in decides[f]["flags"].get(s, []) and s in size and s not in done]
small = sorted((size[s], s) for s in sids)
lo, hi, cap = int(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3])
pick = [s for n, s in small if lo <= n <= hi][:cap]
print(len(sids), "not yet handed out;", len(pick), "picked")
print(",".join(map(str, pick)))
