"""Put every ContainmentFlag back on the shelf its ITEM is actually on. Dry run by default.

    python reseat_flags.py [--apply]

`ContainmentFlag` carries both an `ItemId` and a `SeriesId`, and only the ItemId is durable: a merge or a
split moves the item, and the flag's `SeriesId` goes on pointing at the shelf the item used to be on. After
wave 2 that is why `audit_identity.py` failed four "Manual link on a shelf with an OPEN conflated-series
flag" — the flags had travelled with the fused items and were being read against the wrong shelf.

The item's current `SeriesId` is the answer, always. Nothing else here is a judgement: the flag's text, its
ReviewState and its decider are untouched, and a flag whose item has no shelf at all is reported rather than
moved, because there is nowhere correct to put it.

TOOLS_TODO 14. Run it after every `books-resolve --series` that merged or split anything.
"""
import json
import os
import sqlite3
import sys
import time

import idbase

APPLY = "--apply" in sys.argv
con = idbase.open_hot()

rows = con.execute("""
    SELECT f.Id, f.ItemId, f.SeriesId, i.SeriesId, f.Flag, f.ReviewState,
           coalesce(s1.Name,'(gone)'), coalesce(s2.Name,'(none)'), substr(i.FileName,1,52)
    FROM ContainmentFlag f
    LEFT JOIN Item i ON i.Id = f.ItemId
    LEFT JOIN Series s1 ON s1.Id = f.SeriesId
    LEFT JOIN Series s2 ON s2.Id = i.SeriesId
    WHERE f.ItemId IS NOT NULL""").fetchall()

move, orphan, ok = [], [], 0
for fid, iid, was, now, flag, state, wasname, nowname, fn in rows:
    if now is None:
        orphan.append((fid, iid, was, flag, fn))
    elif was == now:
        ok += 1
    else:
        move.append((fid, iid, was, now, flag, state, wasname, nowname, fn))

print(f"{len(rows):,} flag(s) with an item: {ok:,} already seated correctly, {len(move):,} to reseat, "
      f"{len(orphan):,} whose item is on no shelf")
for fid, iid, was, now, flag, state, wasname, nowname, fn in move[:40]:
    print(f"   flag {fid:<6} item {iid:<8} {flag:<18} [{state or 'Pending'}]  "
          f"S{was}({wasname[:22]}) -> S{now}({nowname[:22]})   {fn}")
if len(move) > 40:
    print(f"   ... and {len(move)-40:,} more")
for fid, iid, was, flag, fn in orphan[:10]:
    print(f"   ORPHAN flag {fid} item {iid} {flag}: the item is on no shelf; left on S{was}   {fn}")

if not move:
    print("\nnothing to reseat")
    raise SystemExit(0)
if not APPLY:
    print("\n(dry run — nothing written. Re-run with --apply.)")
    raise SystemExit(0)

STAMP = time.strftime("%Y%m%d-%H%M%S")
os.makedirs(idbase.UNDO, exist_ok=True)
up = os.path.join(idbase.UNDO, f"reseat-flags-{STAMP}.jsonl")
with open(up, "w", encoding="utf-8") as f:
    for fid, iid, was, now, flag, *_ in move:
        f.write(json.dumps({"table": "ContainmentFlag", "key": [fid], "was": {"SeriesId": was},
                            "now": {"SeriesId": now}, "itemId": iid, "flag": flag}) + "\n")
w = sqlite3.connect(idbase.HOT)
w.executemany("UPDATE ContainmentFlag SET SeriesId = ? WHERE Id = ?", [(now, fid) for fid, _i, _w, now, *_ in move])
w.commit()
w.close()
print(f"\nreseated {len(move):,} flag(s); undo -> {up}")
