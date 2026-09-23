"""Hand the reader the next batch of shelves, sized by what a reader actually pays for: LINES. Resumable.

`python next_batch.py --tier A|B|C|D [--lines 1400]`            (the tier is REQUIRED: a bare run emits nothing)
`python next_batch.py --redo A-002 A-005 ...`                     regenerate emitted batches, same ids
`python next_batch.py --revisit 17809 1448 [--note "why"]`        re-read named shelves after a ruling changed
`python next_batch.py --revisit-file revisit.txt [--note "why"]`  the same, taking the sids from a sheet (a
                                   `check_splits --landed` sheet also puts `split run:` / `split:` lines in the packets)
`python next_batch.py --items [--books 150] [--dry-run]`          an `X-NNN` batch of BOOKS on decided shelves
`python next_batch.py --s2-file triage.tsv [--shelves 60]`        the S.2 pass: triaged 0.9s, as `R-NNN` batches
`python next_batch.py --splits [--shelves 40] [--only 9845,6791]` the split lane: `F split-needed` shelves, `P-NNN`
any of the above + `--out DIR`                                  write the batch into DIR and leave state.json alone
any of the above + `--dry-run`                                    the same, into a throwaway temp directory
`--help` prints this and exits; an unknown option exits with nothing written.

Every batch file opens with a `## Conventions for this batch` block (TOOLS_TODO 20): the LEDGER.md entries whose
tags match the batch's shelves (`ledger.py`). The brief no longer carries the ledger, so the batch has to.

`--out DIR` exists so the tools can be exercised against the live population without emitting anything: the
files land in DIR, state.json is not written, and no cursor moves — the next real emission is unaffected.

`--s2-file` (PLAN §12, TOOLS_TODO 23) takes `triage_09.py`'s sheet — one row per (shelf, signal) — and hands the
shelves out in folder order, `S2_CEILING` per batch, resumably (state.json `s2`: the shelves already emitted).
They are named `R-NNN`, not `S2-NNN`: a decision file supersedes an earlier one ONLY when idbase.revisit_rank
reads an `R-` name, so an `S2-` file re-deciding a 0.9 shelf would be a duplicate decision to the checker.

`--splits` (TOOLS_TODO 27) hands out the shelves whose WINNING decision carries `F split-needed` — refused until
split, and not fixable by another reading — as `P-NNN` batches: one split packet per shelf (`splitbase.packet`:
the winning R/S + F + N lines, the shelves the F line names, and every item id grouped by title x folder), filled
to ~1,400 lines, `SPLIT_CEILING` shelves at most, a packet over 300 lines alone. State lives in state.json
`splits` exactly as `s2` does. The reader answers in `decisions/P-NNN.jsonl` (checked by `check_splits.py`), not in
a `.txt`, because the answer IS the input of `books-series-split` — `scan_decisions` reads only `.txt`, so a P-
file can never be mistaken for a shelf decision. `--only` names the shelves (they must be in the population).

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
import ledger

# B was 100 when a packet was 12 lines. Compaction put a clean one-leg shelf at 5-8, so 100 shelves no
# longer fills a batch and the reader pays a round trip for half a workload.
CEILING = {"A": 150, "B": 150, "C": 60, "D": 120}
TARGET_LINES = 1400
SOLO_LINES = 300
# The ITEM pass is sized in BOOKS, not lines: a book is two lines and the reader's cost is per book.
BOOK_CEILING = 150
# S.2 shelves were each triaged IN because something about them is wrong or unproven — tier C's reading, so
# tier C's ceiling.
S2_CEILING = 60
# A split shelf costs more per shelf than a reading (the answer is per ITEM), but 236 of the 530 are 1-5 files;
# lines, not this ceiling, bound most P- batches.
SPLIT_CEILING = 40

# --redo takes a LIST: after a packet-shape change every pending batch is regenerated in one call, and a
# batch that has already been read is regenerated with the ids it was emitted with, so a decision file
# written against it still covers it exactly.
# The argument guard (2026-09-22). This script WRITES batches/ and state.json, and it used to treat anything it
# did not recognise — `--help`, a typo, no argument at all — as "emit the next tier-A batch": a `--help` probe
# emitted the real batch A-035 and moved cursor A 3971 -> 4011. So: every flag must be known, a tier emission
# must name its tier, `--help` only prints, and `--dry-run` on ANY mode writes into a throwaway directory.
KNOWN = {"--tier", "--lines", "--redo", "--revisit", "--revisit-file", "--note", "--items", "--books", "--dry-run",
         "--s2-file", "--shelves", "--out", "--splits", "--only"}
if any(a in ("-h", "--help", "/?") for a in sys.argv[1:]):
    print(__doc__)
    raise SystemExit(0)
unknown = [a for a in sys.argv[1:] if a.startswith("-") and a not in KNOWN]
if unknown:
    raise SystemExit(f"unknown option(s) {unknown} — nothing emitted.\n{__doc__}")
MODES = ("--tier", "--redo", "--revisit", "--revisit-file", "--items", "--s2-file", "--splits")
if not any(m in sys.argv[1:] for m in MODES):
    raise SystemExit(f"name a mode ({' / '.join(MODES)}) — a bare run emits nothing.\n{__doc__}")

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
tier = (opt.get("tier") or "").upper()
target = int(opt.get("lines") or TARGET_LINES)
ITEMS = "--items" in sys.argv
DRY = "--dry-run" in sys.argv
OUTDIR = opt.get("out")
if DRY and not OUTDIR and not ITEMS:
    # the item pass's --dry-run only prints; every other mode renders for real, so a dry run renders into a
    # directory nobody reads
    import tempfile
    OUTDIR = tempfile.mkdtemp(prefix="next_batch-dry-")
if OUTDIR:
    if os.path.abspath(OUTDIR) == os.path.abspath(idbase.BATCHES):
        raise SystemExit("--out must not be batches/ — that is what a real emission writes")
    os.makedirs(OUTDIR, exist_ok=True)

os.makedirs(idbase.BATCHES, exist_ok=True)
st = idbase.load_state()


def save_state(state):
    """state.json is the emission ledger; a run writing into --out emitted nothing and records nothing."""
    if OUTDIR:
        print(f"   (--out {OUTDIR}: state.json not written)")
        return
    idbase.save_state(state)

st.setdefault("cursors", {"A": 0, "B": 0, "C": 0, "D": 0})
st["cursors"].setdefault("X", 0)
st.setdefault("emitted", [])
st.setdefault("items", [])

ev = Evidence()
ctx = identity_packet.Ctx(ev)


def render(sid):
    return identity_packet.packet(sid, ev, ctx)


_tagger, _entries = [], []


def conventions(shelves, kind):
    """The batch's slice of the ledger (TOOLS_TODO 20) — built once per run, prepended to the packets."""
    if not _tagger:
        _tagger.append(ledger.ShelfTagger(ev))
        _entries.extend(ledger.load())
    return ledger.block_for(ev, shelves, kind, tagger=_tagger[0], entries=_entries)


def write_batch(name, blocks, ids, shelves=None, kind=None):
    """`lines` returned is the PACKET line count (what the sizing rule is about); the conventions block rides
    on top of it and is reported separately."""
    head = conventions(shelves if shelves is not None else ids, kind)
    lines = []
    for b in blocks:
        lines += b + [""]
    where = OUTDIR or idbase.BATCHES
    with open(os.path.join(where, name + ".txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(head + lines))
    with open(os.path.join(where, name + ".ids"), "w", encoding="utf-8") as f:
        f.write("\n".join(str(s) for s in ids) + "\n")
    print(f"   {head[0]}")
    return len(lines)


if ITEMS:
    # ── the ITEM pass (TOOLS_TODO 16) ────────────────────────────────────────────────────────────
    # The shelves are decided; these are the BOOKS on them that are not. Two populations, defined once
    # in idbase so the emitter and `identity_coverage.py` cannot drift: a collected edition with no `I`
    # line of its own, and a book on a collected LINE whose judged range names no run. They overlap
    # heavily (a trade-line book is usually both), so the batch is their UNION, grouped by shelf —
    # shelf context is the whole reason an item-level answer is cheap.
    decides, winner, _sup, _dup = idbase.scan_decisions()
    pop = idbase.item_population(ev, decides, winner)
    want = {i: pop["shelf_of"][i] for i in pop["no_i"]}
    want.update({i: pop["shelf_of"][i] for i in pop["no_c"]})
    done = {i for b in st["items"] for i in b["ids"]}
    todo = [i for i in want if i not in done]

    folders = {}
    for sid, path in ev.con.execute("""SELECT i.SeriesId, i.Path FROM Item i
                                       WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                                         AND i.SeriesId IS NOT NULL"""):
        if sid in ev.shelf_set:
            folders.setdefault(sid, Counter())[idbase.short_path(os.path.dirname(path or ""))] += 1
    dom = {sid: c.most_common(1)[0][0] for sid, c in folders.items()}
    by_shelf = {}
    for iid in todo:
        by_shelf.setdefault(want[iid], []).append(iid)
    order = sorted(by_shelf, key=lambda s: (dom.get(s, ""), s))

    def decision_of(sid):
        """The winning S line for a shelf, and the notes beside it — restated, never re-decided."""
        w = winner.get(sid)
        rec = decides[w]
        line = ""
        for raw in open(w, encoding="utf-8"):
            t = raw.strip()
            if t.startswith(("S ", "S" + str(sid))) and t.split()[1].lstrip("S").isdigit() \
                    and int(t.split()[1].lstrip("S")) == sid:
                line = t.split("|", 1)[0] + "| " + (t.split("|", 1)[1].strip()[:150] if "|" in t else "")
                break
        notes = []
        for raw in open(w, encoding="utf-8"):
            t = raw.strip()
            if t.startswith("N ") and t.split()[1].lstrip("S").isdigit() \
                    and int(t.split()[1].lstrip("S")) == sid:
                notes.append(t[2:].strip())
        return {"line": line or f"S {sid} (see {os.path.basename(w)})", "cv": rec["cv"].get(sid),
                "gcd": rec["gcd"].get(sid), "conf": rec["confs"].get(sid),
                "batch": os.path.splitext(os.path.basename(w))[0], "notes": notes}

    cap = int(opt.get("books") or BOOK_CEILING)
    take_ids, blocks, n_books, take_shelves = [], [], 0, []
    for sid in order:
        its = sorted(by_shelf[sid])
        if take_ids and n_books + len(its) > cap:
            break
        blocks.append(identity_packet.item_packet(sid, ev, ctx, its, decision_of(sid)))
        take_shelves.append(sid)
        take_ids += its
        n_books += len(its)
        if n_books >= cap:
            break

    name = f"X-{1 + len(st['items']):03d}"
    counts = {"books without an I line": len(pop["no_i"]),
              "line-shelf books with a judged range and no run ref": len(pop["no_c"]),
              "line-shelf books with NO judged range at all (not in this population)": len(pop["no_span"]),
              "union still to hand out": len(todo), "accepted shelves": len(pop["accepted"]),
              "collected-line shelves": len(pop["line_shelves"])}
    if DRY:
        print({"batch": name, "books": n_books, "shelves": len(blocks), "DRY RUN": True})
        for k, v in counts.items():
            print(f"   {v:>8,}  {k}")
        print("\n" + "\n".join(blocks[0] if blocks else ["(nothing to emit)"]))
        raise SystemExit(0)
    if not take_ids:
        print({"batch": None, "books": 0, "remaining": 0})
        raise SystemExit(0)
    written = write_batch(name, blocks, take_ids, shelves=take_shelves, kind="X")
    st["items"].append({"batch": name, "kind": "X", "ids": take_ids, "shelves": len(blocks),
                        "lines": written, "at": time.strftime("%Y-%m-%d %H:%M:%S")})
    st["cursors"]["X"] = st["cursors"].get("X", 0) + len(take_ids)
    save_state(st)
    print({"batch": name, "books": len(take_ids), "shelves": len(blocks), "lines": written,
           "remaining": len(todo) - len(take_ids)})
    for k, v in counts.items():
        print(f"   {v:>8,}  {k}")
    raise SystemExit(0)


def dominant_folders():
    """The shelf's dominant folder, one sweep over the items — the ordering key of every shelf batch."""
    folders = {}
    for sid, path in ev.con.execute("""SELECT i.SeriesId, i.Path FROM Item i
                                       WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL"""):
        if sid in ev.shelf_set:
            folders.setdefault(sid, Counter())[idbase.short_path(os.path.dirname(path or ""))] += 1
    return {sid: c.most_common(1)[0][0] for sid, c in folders.items()}


def pack(todo, cap, renderer=None):
    """Fill one batch from `todo` in order: ~TARGET_LINES of packets, at most `cap` shelves, and a shelf whose
    own packet exceeds SOLO_LINES gets a batch to itself (PLAN §7-S)."""
    take, blocks, total = [], [], 0
    for sid in todo[:cap + 8]:
        block = (renderer or render)(sid)
        if len(block) + 1 > SOLO_LINES:
            if take:
                break
            return [sid], [block]
        if take and (total + len(block) + 1 > target or len(take) >= cap):
            break
        take.append(sid)
        blocks.append(block)
        total += len(block) + 1
    return take, blocks


if opt.get("s2-file"):
    # ── S.2 (PLAN §12 as re-ordered 2026-09-22, TOOLS_TODO 23) ──────────────────────────────────
    # The 0.9s are not re-read blind: triage_09.py lists the ones carrying a signal, and only those come here.
    # Emitted as REVISITS (see the docstring for why the name is `R-`), resumably: `st["s2"]` remembers every
    # shelf handed out, so re-running after a crash or a landing continues where it stopped.
    rf = opt["s2-file"]
    path = rf if os.path.isfile(rf) else os.path.join(idbase.ROOT, rf)
    if not os.path.isfile(path):
        raise SystemExit(f"no such triage sheet: {rf}")
    sids = []
    for raw in open(path, encoding="utf-8"):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        head = line.split()[0].lstrip("S")
        if head.isdigit():
            sids.append(int(head))
    st.setdefault("s2", [])
    st.setdefault("revisits", [])
    done = {s for b in st["s2"] for s in b["ids"]}
    dom = dominant_folders()
    todo = sorted({s for s in sids if s in ev.shelf_set and s not in done}, key=lambda s: (dom.get(s, ""), s))
    gone = len({s for s in sids if s not in ev.shelf_set})
    if not todo:
        print({"batch": None, "shelves": 0, "remaining": 0, "gone since triage": gone})
        raise SystemExit(0)
    take, blocks = pack(todo, int(opt.get("shelves") or S2_CEILING))
    name = f"R-{1 + len(st['revisits']):03d}"
    n = write_batch(name, blocks, take, kind="S2")
    note = opt.get("note") or f"S.2: 0.9 shelves carrying a triage signal (source: {os.path.basename(rf)})"
    st["revisits"].append({"batch": name, "ids": take, "lines": n, "note": note, "source": os.path.basename(rf),
                           "s2": True, "at": time.strftime("%Y-%m-%d %H:%M:%S")})
    st["s2"].append({"batch": name, "ids": take, "at": time.strftime("%Y-%m-%d %H:%M:%S")})
    save_state(st)
    print({"batch": name, "shelves": len(take), "lines": n, "remaining": len(todo) - len(take),
           "gone since triage": gone})
    raise SystemExit(0)

if "--splits" in sys.argv:
    # ── the split lane (TOOLS_TODO 27) ─────────────────────────────────────────────────────────────
    import splitbase
    pop = splitbase.population(ev)
    st.setdefault("splits", [])
    done = {s for b in st["splits"] for s in b["ids"]}
    dom = dominant_folders()
    if opt.get("only"):
        want = [int(x.strip().lstrip("S")) for x in opt["only"].split(",") if x.strip()]
        bad = [s for s in want if s not in pop]
        if bad:
            raise SystemExit(f"--only: not in the split population (no winning F split-needed on a live shelf): {bad}")
        todo = [s for s in want if s not in done]
    else:
        todo = sorted((s for s in pop if s not in done), key=lambda s: (dom.get(s, ""), s))
    if not todo:
        print({"batch": None, "shelves": 0, "remaining": 0, "population": len(pop)})
        raise SystemExit(0)
    renderer = lambda sid: splitbase.packet(sid, ev, pop[sid])
    if opt.get("only"):
        take, blocks = todo, [renderer(s) for s in todo]
    else:
        take, blocks = pack(todo, int(opt.get("shelves") or SPLIT_CEILING), renderer)
    name = f"P-{1 + len(st['splits']):03d}"
    n = write_batch(name, blocks, take, kind="P")
    st["splits"].append({"batch": name, "ids": take, "lines": n, "at": time.strftime("%Y-%m-%d %H:%M:%S")})
    save_state(st)
    print({"batch": name, "shelves": len(take), "lines": n, "remaining": len(todo) - len(take),
           "population": len(pop)})
    raise SystemExit(0)

if revisit or opt.get("revisit-file"):
    # A revisit re-decides shelves that were already read, because the RULING changed — not because the
    # packets were wrong. So it emits fresh packets for exactly the named shelves and touches neither the
    # tier cursor nor the emitted-ids set: those shelves are still spoken for by their original batch, and
    # the original decision file stays on disk as the audit trail (the R- file supersedes it at apply time).
    sids, source = [], "--revisit"
    split_notes = {}
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
                # TOOLS_TODO 28e: a `check_splits.py --landed` sheet row (S<sid> TAB key TAB run=… TAB P-file
                # [TAB new|kept]) puts the split's own answer in the packet, so the reader seeds the S line from
                # the P- file's `run` ids without opening it, and knows a kept half for what it is
                col = raw.rstrip("\r\n").split("\t")
                if len(col) >= 4 and col[2].startswith("run="):
                    batch = os.path.splitext(col[3].strip())[0]
                    if (col[4].strip() if len(col) > 4 else "new") == "kept":
                        note = f"   split: the KEPT half of the {batch} split — {col[1].strip()}; re-identify what stayed"
                    else:
                        note = (f"   split run: \"{col[1].strip()}\" {col[2].strip()} (new shelf from {batch}) — "
                                f"seed the S line from these ids, then verify them")
                    split_notes.setdefault(int(head), []).append(note)
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
    def render_noted(s):
        b = render(s)
        return b[:1] + split_notes.get(s, []) + b[1:]

    n = write_batch(name, [render_noted(s) for s in ids], ids, kind="R")
    st["revisits"].append({"batch": name, "ids": ids, "lines": n, "note": note, "source": source,
                           "at": time.strftime("%Y-%m-%d %H:%M:%S")})
    save_state(st)
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
        n = write_batch(name, [render(s) for s in ids], ids, kind=rec["tier"])
        rec["lines"] = n
        print({"tier": rec["tier"], "batch": name, "shelves": len(ids), "lines": n, "was": was,
               "gone since emission": len(gone)})
    save_state(st)
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
written = write_batch(name, blocks, take, kind=tier)
st["emitted"].append({"batch": name, "tier": tier, "ids": take, "lines": written,
                      "at": time.strftime("%Y-%m-%d %H:%M:%S")})
st["cursors"][tier] = st["cursors"].get(tier, 0) + len(take)
save_state(st)
print({"tier": tier, "batch": name, "shelves": len(take), "lines": written,
       "remaining": len(todo) - len(take)})
