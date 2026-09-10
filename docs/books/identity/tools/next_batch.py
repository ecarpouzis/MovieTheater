"""Hand the reader the next batch of shelves, sized by what a reader actually pays for: LINES. Resumable.

`python next_batch.py [--tier A|B|C|D] [--lines 1400]`
`python next_batch.py --redo A-002 A-005 ...`                     regenerate emitted batches, same ids
`python next_batch.py --revisit 17809 1448 [--note "why"]`        re-read named shelves after a ruling changed
`python next_batch.py --revisit-file revisit.txt [--note "why"]`  the same, taking the sids from a sheet

A batch of 120 tier-A shelves can be 400 lines or 4,000 depending on how many of them are 60-file runs that
do not fold, so a shelf count is not a workload. This fills a batch to ~1,400 packet lines, subject to a
per-tier shelf ceiling (A 150 · B 100 · C 60 · D 120), and gives any shelf whose own packet exceeds ~300
lines a batch to itself — PLAN §7-S: "a conflated 1,700-file shelf is as long as it needs to be and gets a
batch of its own."

Two things make it safe to run in a loop for ~400 batches. First, the ids of every emitted batch are written
into `state.json`, and emission SKIPS any shelf already emitted — so a re-run after a crash, or a run whose
ordering shifted because a landing merged two shelves, cannot hand the same shelf out twice or drop one.
Second, every id is re-validated against the live DB at emission (PLAN §7-S): a shelf a
`books-resolve --series` merged away is skipped here and re-enters through the coverage partition.

Ordering inside a tier is by the shelf's dominant folder, then by id, because PLAN §4.6 says the folder is
context: a reader who has just read `DC\\Batman (1940)` is the right reader for `DC\\Batman Annual (1961)`.
"""
import os
import sys
import time
from collections import Counter

import idbase
from idbase import Evidence
import identity_packet

# B was 100 when a packet was 12 lines. Compaction put a clean one-leg shelf at 5-8, so 100 shelves no
# longer fills a batch and the reader pays a round trip for half a workload.
CEILING = {"A": 150, "B": 150, "C": 60, "D": 120}
TARGET_LINES = 1400
SOLO_LINES = 300

# --redo takes a LIST: after a packet-shape change every pending batch is regenerated in one call, and a
# batch that has already been read is regenerated with the ids it was emitted with, so a decision file
# written against it still covers it exactly.
opt, redo, revisit = {}, [], []
k = 1
while k < len(sys.argv):
    a = sys.argv[k]
    if a in ("--redo", "--revisit"):
        bucket = redo if a == "--redo" else revisit
        k += 1
        while k < len(sys.argv) and not sys.argv[k].startswith("--"):
            bucket.append(sys.argv[k])
            k += 1
        continue
    if a.startswith("--") and k + 1 < len(sys.argv) and not sys.argv[k + 1].startswith("--"):
        opt[a[2:]] = sys.argv[k + 1]
        k += 2
        continue
    k += 1
tier = (opt.get("tier") or "A").upper()
target = int(opt.get("lines") or TARGET_LINES)

os.makedirs(idbase.BATCHES, exist_ok=True)
st = idbase.load_state()
st.setdefault("cursors", {"A": 0, "B": 0, "C": 0, "D": 0})
st.setdefault("emitted", [])

ev = Evidence()
ctx = identity_packet.Ctx(ev)


def render(sid):
    return identity_packet.packet(sid, ev, ctx)


def write_batch(name, blocks, ids):
    lines = []
    for b in blocks:
        lines += b + [""]
    with open(os.path.join(idbase.BATCHES, name + ".txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    with open(os.path.join(idbase.BATCHES, name + ".ids"), "w", encoding="utf-8") as f:
        f.write("\n".join(str(s) for s in ids) + "\n")
    return len(lines)


if revisit or opt.get("revisit-file"):
    # A revisit re-decides shelves that were already read, because the RULING changed — not because the
    # packets were wrong. So it emits fresh packets for exactly the named shelves and touches neither the
    # tier cursor nor the emitted-ids set: those shelves are still spoken for by their original batch, and
    # the original decision file stays on disk as the audit trail (the R- file supersedes it at apply time).
    sids, source = [], "--revisit"
    rf = opt.get("revisit-file")
    if rf:
        source = os.path.basename(rf)
        path = rf if os.path.isfile(rf) else os.path.join(idbase.ROOT, rf)
        if not os.path.isfile(path):
            raise SystemExit(f"no such revisit file: {rf}")
        for raw in open(path, encoding="utf-8"):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            head = line.split()[0].lstrip("S")
            if head.isdigit():
                sids.append(int(head))
    sids += [int(x) for x in revisit]
    seen, ordered = set(), []
    for s in sids:                                   # first mention wins; the file's order is the lead's
        if s not in seen:
            seen.add(s)
            ordered.append(s)
    gone = [s for s in ordered if s not in ev.shelf_set]
    ids = [s for s in ordered if s in ev.shelf_set]
    if not ids:
        raise SystemExit(f"none of the {len(ordered)} named shelf/shelves is a file-holding comic shelf today")
    st.setdefault("revisits", [])
    name = f"R-{1 + len(st['revisits']):03d}"
    note = opt.get("note") or f"re-read after a sharpened ruling (source: {source})"
    n = write_batch(name, [render(s) for s in ids], ids)
    st["revisits"].append({"batch": name, "ids": ids, "lines": n, "note": note, "source": source,
                           "at": time.strftime("%Y-%m-%d %H:%M:%S")})
    idbase.save_state(st)
    print({"batch": name, "shelves": len(ids), "lines": n, "gone": len(gone), "note": note})
    if gone:
        print(f"   skipped (no longer a file-holding comic shelf): {gone}")
    for sid in ids:
        t, why, detail = ev.tier(sid)
        print(f"   S{sid:<7} [tier {t}] {ev.series[sid]['name'][:46]:<46} {why}{(' — ' + detail) if detail else ''}")
    raise SystemExit(0)

if redo:
    for name in redo:
        rec = next((b for b in st["emitted"] if b["batch"] == name), None)
        if rec is None:
            raise SystemExit(f"no batch named {name} in state.json")
        ids = [s for s in rec["ids"] if s in ev.shelf_set]
        gone = [s for s in rec["ids"] if s not in ev.shelf_set]
        was = rec.get("lines")
        n = write_batch(name, [render(s) for s in ids], ids)
        rec["lines"] = n
        print({"tier": rec["tier"], "batch": name, "shelves": len(ids), "lines": n, "was": was,
               "gone since emission": len(gone)})
    idbase.save_state(st)
    raise SystemExit(0)

if tier not in CEILING:
    raise SystemExit(f"tier must be one of {sorted(CEILING)}")

# dominant folder per shelf, one sweep over the items
folders = {}
for sid, path in ev.con.execute("""SELECT i.SeriesId, i.Path FROM Item i
                                   WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL"""):
    if sid in ev.shelf_set:
        folders.setdefault(sid, Counter())[idbase.short_path(os.path.dirname(path or ""))] += 1
dom = {sid: c.most_common(1)[0][0] for sid, c in folders.items()}

pool = sorted((s for s in ev.shelves if ev.tier(s)[0] == tier), key=lambda s: (dom.get(s, ""), s))
done = {s for b in st["emitted"] for s in b["ids"]}
todo = [s for s in pool if s not in done]
if not todo:
    print({"tier": tier, "batch": None, "shelves": 0, "lines": 0, "remaining": 0})
    raise SystemExit(0)

take, blocks, total = [], [], 0
for sid in todo[:CEILING[tier] + 8]:
    block = render(sid)
    if len(block) + 1 > SOLO_LINES:
        # A packet this long is a batch. If the batch already has shelves, it closes here and the big
        # shelf leads the next one — it is never appended to someone else's workload.
        if take:
            break
        take, blocks, total = [sid], [block], len(block) + 1
        break
    if take and (total + len(block) + 1 > target or len(take) >= CEILING[tier]):
        break
    take.append(sid)
    blocks.append(block)
    total += len(block) + 1

n = 1 + sum(1 for b in st["emitted"] if b["tier"] == tier)
name = f"{tier}-{n:03d}"
written = write_batch(name, blocks, take)
st["emitted"].append({"batch": name, "tier": tier, "ids": take, "lines": written,
                      "at": time.strftime("%Y-%m-%d %H:%M:%S")})
st["cursors"][tier] = st["cursors"].get(tier, 0) + len(take)
idbase.save_state(st)
print({"tier": tier, "batch": name, "shelves": len(take), "lines": written,
       "remaining": len(todo) - len(take)})
