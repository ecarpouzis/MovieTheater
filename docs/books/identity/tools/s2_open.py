"""S.2 tier one (PLAN §12 "S.2 order"): the shelves that are OPEN BY CONSTRUCTION — every winning 0.7, every winning
`R` — as a sheet for `next_batch.py --s2-file`.

`python s2_open.py [--cursor 0] [--limit 2000] [--all] [--out s2-open.tsv]`

One row per shelf: `S<sid>  <signal>  <detail>`, signal `07` (an S held for review at 0.7) or `R` (refused), the
detail naming the winning batch and the refusal's F flags. `F split-needed` shelves are LEFT OUT (they belong to the
split lane, `next_batch --splits`) and so are `F not-a-run` shelves (a ruling, not an open question — L-185). The
containment gap is a separate sheet (its shelves are accepted; what is open is their books' ranges).

`next_batch --s2-file` skips every shelf an earlier S.2 batch already emitted (state.json `s2`), so the R- batches
of the re-probe lane (R-061..) are not handed out twice. READ-ONLY; chunked by shelf id with a cursor, each chunk
printing `{processed, remaining, nextCursor, counts}`; `--all` drives it to the end, `--cursor` resumes (appending).
"""
import os
import sys
from collections import Counter

import idbase

SKIP_FLAGS = {"split-needed", "not-a-run"}


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = sys.argv[1:]

    def opt(name, default=None):
        return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else default

    cursor, limit, out = int(opt("--cursor", 0)), int(opt("--limit", 2000)), opt("--out")
    ev = idbase.Evidence()
    decides, winner, _s, _d = idbase.scan_decisions()
    rows, skipped = {}, Counter()
    for sid, w in winner.items():
        if sid not in ev.shelf_set:
            continue
        rec = decides[w]
        kind, conf = rec["kinds"].get(sid), rec["confs"].get(sid)
        flags = sorted({t.split("=", 1)[0] for t in rec.get("flags_full", {}).get(sid, ())})
        if kind == "R":
            if SKIP_FLAGS & set(flags):
                skipped["R + " + "/".join(sorted(SKIP_FLAGS & set(flags)))] += 1
                continue
            sig = "R"
        elif kind == "S" and conf == "0.7":
            sig = "07"
        else:
            continue
        batch = os.path.splitext(os.path.basename(w))[0]
        rows[sid] = (sig, f"{batch}: {kind}{' ' + conf if conf else ''}" + (f", F {', '.join(flags)}" if flags else ""))
    pop = sorted(rows)
    print(f"population: {len(pop):,} open shelves {dict(Counter(r[0] for r in rows.values()))}; left out {dict(skipped)}")
    sink = open(out, "a" if cursor else "w", encoding="utf-8") if out else None
    if sink and not cursor:
        sink.write("# shelf\tsignal\tdetail   (s2_open.py: every winning 0.7 and R; feed: next_batch.py --s2-file)\n")
    while True:
        chunk = [s for s in pop if s > cursor][:limit]
        counts = Counter()
        for sid in chunk:
            sig, detail = rows[sid]
            counts[sig] += 1
            if sink:
                sink.write(f"S{sid}\t{sig}\t{detail}\n")
        nxt = chunk[-1] if chunk else None
        remaining = sum(1 for s in pop if s > (nxt if nxt is not None else cursor))
        print({"processed": len(chunk), "remaining": remaining, "nextCursor": nxt, "counts": dict(counts)})
        if sink:
            sink.flush()
        if "--all" not in a or not chunk or remaining == 0:
            break
        cursor = nxt
    if sink:
        sink.close()


if __name__ == "__main__":
    main()
