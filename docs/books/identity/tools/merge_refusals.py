"""After an identity merge, keep the containment coverage contract true. Dry run by default.

    python merge_refusals.py --snapshot wave2-before.json          BEFORE books-resolve --series
    python merge_refusals.py --from wave2-before.json --wave 2     after it: what would be written
    python merge_refusals.py --from wave2-before.json --wave 2 --apply

Wave 1 landed green and then `audit_containment.py` stopped the chain on Kick-Ass and Super Friends. The
mechanism is worth stating because it will happen every wave: linking two shelves to one CV volume merges
them (PLAN §6.5), the merge carries the loser's COLLECTED EDITIONS onto the survivor, and two invariants
break at once —

  * the survivor's decision file in `docs/books/containment/decisions/` no longer decides every edition on
    its shelf, so `check_decisions.py`/`pass2.py` refuse to expand it (that refusal IS the coverage
    guarantee — containment PLAN §8);
  * an arriving edition that carries an unjudged provider span has that claim ARMED against a run nobody
    measured it against. Super Friends' Vol. 01 brought LOCG's #0-219 — a hull over a 47-issue run — which
    at once swallowed all 76 issue files under one book.

The containment doctrine's answer to both is the same and it is not a guess: refuse, in writing, until the
book is read. So this writes `u <itemId> …` refusal lines, in the ADDENDUM shape the lead wrote by hand for
S10310 and S65789, and nothing else. It never writes a RANGE — a guessed range is how a file lands in a
book that does not contain it.

"Moved" is measured, not inferred: the snapshot taken before the resolve is compared with the live table
after it. Anything else would have to model what `SeriesResolver` does, and it is the resolver's answer
that matters.
"""
import json
import os
import sys
import time

import idbase

CONT = os.path.abspath(os.path.join(idbase.ROOT, os.pardir, "containment"))
DEC = os.path.join(CONT, "decisions")

APPLY = "--apply" in sys.argv
opt = {}
for k, a in enumerate(sys.argv):
    if a.startswith("--") and k + 1 < len(sys.argv) and not sys.argv[k + 1].startswith("--"):
        opt[a[2:]] = sys.argv[k + 1]
WAVE = opt.get("wave", "?")
STAMP = time.strftime("%Y-%m-%d")

con = idbase.open_hot()


def collected_editions():
    """itemId -> (seriesId, fileName) for every non-excluded collected edition. This is exactly the set
    `pass2.load_packets()` builds its coverage from, so the two agree by construction."""
    return {r[0]: (r[1], r[2]) for r in con.execute("""
        SELECT i.Id, i.SeriesId, i.FileName FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE cd.IsCollection = 1 AND coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL""")}


if "snapshot" in opt:
    path = opt["snapshot"]
    now = {str(iid): sid for iid, (sid, _fn) in collected_editions().items()}
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"takenAt": time.strftime("%Y-%m-%d %H:%M:%S"), "editions": now}, f)
    print(f"snapshot: {len(now):,} collected edition(s) -> {path}")
    raise SystemExit(0)

if "from" not in opt:
    raise SystemExit(__doc__.strip().splitlines()[2].strip())
with open(opt["from"], encoding="utf-8") as f:
    before = {int(k): v for k, v in json.load(f)["editions"].items()}

after = collected_editions()
moved = {iid: (before[iid], sid) for iid, (sid, _fn) in after.items()
         if iid in before and before[iid] != sid}
gone = sorted(set(before) - set(after))
arrived = sorted(set(after) - set(before))
print(f"{len(before):,} edition(s) before, {len(after):,} after; MOVED {len(moved):,}; "
      f"no longer a collected edition {len(gone):,}; new {len(arrived):,}")
if not moved:
    print("nothing moved — no containment refusal is owed")
    raise SystemExit(0)

# what each arriving edition brings with it: an unjudged provider span is the armed claim
armed = {r[0]: r[1] for r in con.execute("""
    SELECT s.ItemId, count(*) FROM CollectedEditionSpan s
    WHERE s.Source <> 3 AND s.IssueStart IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan j WHERE j.ItemId = s.ItemId AND j.Source = 3)
    GROUP BY s.ItemId""")}
names = {r[0]: (r[1], r[2]) for r in con.execute(
    "SELECT Id, coalesce(DisplayNameOverride, Name), CanonicalKey FROM Series")}

by_survivor = {}
for iid, (was, now) in sorted(moved.items()):
    by_survivor.setdefault(now, []).append((iid, was))

plan = []
for surv, items in sorted(by_survivor.items()):
    path = os.path.join(DEC, f"S{surv}.txt")
    exists = os.path.isfile(path)
    on_shelf = sorted(i for i, (sid, _f) in after.items() if sid == surv)
    already = set()
    if exists:
        for raw in open(path, encoding="utf-8"):
            t = raw.strip().split()
            if len(t) >= 2 and t[0] in ("S", "u") and t[1].isdigit():
                already.add(int(t[1]))
    # When the survivor has no file at all, the addendum cannot be an addendum: the coverage contract
    # demands EVERY edition on the shelf be decided exactly once, so a new file refuses all of them.
    targets = [i for i in (on_shelf if not exists else [i for i, _w in items]) if i not in already]
    if not targets:
        continue
    lines = []
    if exists:
        lines.append("")
        lines.append(f"# ADDENDUM (identity pass, wave {WAVE}, {STAMP}): the identity reading attributed the "
                     f"shelf(s) below to the run this shelf IS, and the resolve merged their collected")
        lines.append(f"# editions in. The coverage contract says every collected edition on a shelf is decided "
                     f"exactly once, so they are refused here until read.")
    else:
        lines.append(f"# S{surv} {names.get(surv, ('?',))[0]} — created by the identity pass, wave {WAVE}, {STAMP}.")
        lines.append(f"# This shelf had no containment decision file and the identity merge moved "
                     f"{len(items)} collected edition(s) onto it, so the file must now cover ALL "
                     f"{len(on_shelf)} of its editions.")
        lines.append("# Nothing here is a range. Every line is a refusal until the book is read "
                     "(containment PLAN §8, §10.3).")
    moved_from = {i: w for i, w in items}
    for iid in targets:
        why = (f"arrived by the wave-{WAVE} identity merge from S{moved_from[iid]}"
               if iid in moved_from else "was already on this shelf when the merge arrived")
        if armed.get(iid):
            why += (f"; it carries {armed[iid]} unjudged provider span(s), which the merge would ARM "
                    f"against a run they were never measured against")
        why += "; no range read from the book"
        lines.append(f"u {iid} {why}")
    plan.append((path, exists, lines, targets))

print()
tot = 0
for path, exists, lines, targets in plan:
    tot += len(targets)
    print(f"  {'APPEND to' if exists else 'CREATE   '} {os.path.relpath(path, CONT)}   "
          f"{len(targets)} refusal line(s)")
    for l in lines:
        print(f"      {l[:150]}")
print(f"\n{len(plan)} file(s), {tot} refusal line(s) for {len(moved)} moved edition(s)")

if not APPLY:
    print("\n(dry run — nothing written. Re-run with --apply, then check_decisions.py.)")
    raise SystemExit(0)

for path, exists, lines, _t in plan:
    with open(path, "a" if exists else "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{'appended to' if exists else 'created'} {path}")
print(f"\napplied. Now run: python {os.path.join(CONT, 'tools', 'check_decisions.py')}")
