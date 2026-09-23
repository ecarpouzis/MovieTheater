"""Pending conflated / overlap flags a split has probably made STALE. READ-ONLY: it lists, the lead dismisses.

`python stale_flags_after_split.py [--from P-001 P-002 ...] [--cursor 0] [--limit 200] [--all] [--include-dismissed]`
`python stale_flags_after_split.py --population [...]`   every flagged shelf, split or not (noisy — see below)

Why (P-001 pilot, TOOLS_TODO 28f). A shelf a split leaves holding ONE run still carries the item-level
`conflated-series` / `overlap-in-series` ContainmentFlag it had before the split (the flag rides on the item,
and `reseat_flags` only moves it to the item's new shelf). The checker then refuses every `S` on that shelf,
so the identity reader has to write `R` for a shelf that is now clean (R-030, S102475 Dead Body Road: Bad
Blood). This lists each such flag with the evidence that the shelf is one run now, so the lead can look and
dismiss it — nothing here writes, and nothing here decides: one parsed key is a strong hint, not a proof
(two runs can share a key; that is what the split lane exists for).

A flag is listed when it is Pending (ReviewState NULL / '' / Pending / Open), its Flag is `conflated-series`
or `overlap-in-series`, and the shelf it sits on holds ONE live comic file, or files that all carry ONE
ParsedSeriesKey. The scope is the shelves the P- files touched — each `split: true` source shelf (the kept half) and every
shelf now holding an item that carries one of the file's keys (the new halves, joins included): the files named
by `--from`, else every `decisions/P-*.jsonl`. `--population` examines every flagged shelf instead, where one key
is a WEAK hint (S5909 Dungeon: five cycles, one key "Dungeon") — use it to audit, never to dismiss in bulk.
`--include-dismissed` lists answered flags too (to audit a past dismissal, e.g. flag 60 on S102475).

Chunked by shelf id with a cursor (global rule): each chunk prints `{processed, remaining, nextCursor, counts}`;
`--all` drives the chunks to the end. Exit 0 always (a report, not a gate); 2 on a usage error.
"""
import json
import os
import sys
from collections import Counter, defaultdict

import idbase

KINDS = ("conflated-series", "overlap-in-series")


def touched_by(con, names):
    """The shelves the named P- files touched, read off the P- files and the live tables (see the docstring)."""
    import check_splits
    out, keys = set(), set()
    for n in names:
        p = check_splits.resolve_split_file(n)
        for raw in open(p, encoding="utf-8"):
            t = raw.strip()
            if not t:
                continue
            o = json.loads(t)
            if "shelf" in o and o.get("split") is True:
                out.add(o["shelf"])
            elif "key" in o:
                keys.add(o["key"])
    for k in keys:
        out |= {r[0] for r in con.execute("""SELECT DISTINCT i.SeriesId FROM Item i JOIN ComicDetail cd
                                             ON cd.ItemId = i.Id WHERE cd.ParsedSeriesKey = ?
                                             AND i.SeriesId IS NOT NULL""", (k,))}
    return out


def flagged(con, scope=None, dismissed=False):
    """{sid: [flag row, ...]} of the open conflated / overlap flags, by the shelf they sit on."""
    by = defaultdict(list)
    for fid, iid, sid, flag, detail, source, state in con.execute(f"""
            SELECT Id, ItemId, SeriesId, Flag, Detail, Source, ReviewState FROM ContainmentFlag
            WHERE Flag IN ({','.join('?' * len(KINDS))}) AND SeriesId IS NOT NULL""", KINDS):
        if state not in idbase.OPEN_FLAG_STATES and not dismissed:
            continue
        if scope is not None and sid not in scope:
            continue
        by[sid].append({"id": fid, "itemId": iid, "flag": flag, "detail": detail or "", "source": source or "",
                        "state": state or "Pending"})
    return by


def chunk(con, by, cursor, limit):
    """One bounded chunk of shelves (id > cursor). -> (rows, counts, nextCursor, remaining)."""
    ids = sorted(s for s in by if s > cursor)[:limit]
    rows, counts = [], Counter()
    for sid in ids:
        items = con.execute("""SELECT i.Id, coalesce(cd.ParsedSeriesKey,''), i.FileName FROM Item i
                               LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
                               WHERE i.SeriesId = ? AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0""",
                            (sid,)).fetchall()
        keys = Counter(k for _i, k, _f in items)
        name = (con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,))
                .fetchone() or ("(gone)",))[0]
        counts["shelves examined"] += 1
        counts["flags examined"] += len(by[sid])
        if not items:
            counts["shelf holds no files (reseat_flags first)"] += 1
            continue
        if len(items) > 1 and len(keys) > 1:
            counts["still mixed (2+ keys) — not listed"] += 1
            continue
        why = (f"1 file ({items[0][2][:70]})" if len(items) == 1
               else f"{len(items)} files, all key \"{next(iter(keys))}\"")
        for f in by[sid]:
            counts[f"stale candidate: {f['flag']}"] += 1
            on_item = "" if f["itemId"] is None else (
                "  (flag item is ON this shelf)" if any(i == f["itemId"] for i, _k, _f in items)
                else "  (flag item is NOT on this shelf — run reseat_flags)")
            rows.append(f"   S{sid:<7} {name[:48]!r}: {why}\n"
                        f"            flag {f['id']} {f['flag']} [{f['state']}] item {f['itemId']}{on_item}"
                        f" source={f['source']}\n"
                        f"            detail: {f['detail'][:200]}")
    remaining = sum(1 for s in by if s > (ids[-1] if ids else cursor))
    return rows, counts, (ids[-1] if ids else None), remaining


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = sys.argv[1:]
    if any(x in ("-h", "--help") for x in a):
        print(__doc__)
        return 0

    def opt(name, default):
        return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else default

    known = {"--from", "--cursor", "--limit", "--all", "--population", "--include-dismissed"}
    bad = [x for x in a if x.startswith("--") and x not in known]
    if bad:
        print(f"unknown option(s) {bad}\n{__doc__}")
        return 2
    con = idbase.open_hot()                 # file:…?mode=ro — this script cannot write
    names = []
    if "--from" in a:
        k = a.index("--from") + 1
        while k < len(a) and not a[k].startswith("--"):
            names.append(a[k])
            k += 1
    if not names and "--population" not in a:
        names = sorted(os.path.splitext(f)[0] for f in os.listdir(idbase.DECISIONS)
                       if f.startswith("P-") and f.endswith(".jsonl"))
    scope = touched_by(con, names) if names and "--population" not in a else None
    by = flagged(con, scope, "--include-dismissed" in a)
    cursor, limit = int(opt("--cursor", 0)), int(opt("--limit", 200))
    total, listed = Counter(), 0
    print(f"open conflated/overlap flags on {len(by)} shelf/shelves"
          + (f" touched by {', '.join(names)} ({len(scope)} shelves)" if scope is not None else " (whole population)"))
    while True:
        rows, counts, nxt, remaining = chunk(con, by, cursor, limit)
        total.update(counts)
        for r in rows:
            print(r)
        listed += len(rows)
        print({"processed": counts["shelves examined"], "remaining": remaining, "nextCursor": nxt,
               "counts": dict(counts)})
        if "--all" not in a or nxt is None or remaining == 0:
            break
        cursor = nxt
    print(f"\n{listed} flag(s) listed as probably stale; totals {dict(total)}")
    if listed:
        print("READ-ONLY: nothing was changed. Verify each shelf really is one run, then dismiss the flag by hand "
              "(ReviewState='Dismissed', a Note naming the split) — the way flag 60 on S102475 was dismissed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
