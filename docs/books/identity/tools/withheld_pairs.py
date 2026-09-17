"""The ids readers got RIGHT and did not store, because storing them would have merged the wrong shelves.

`python withheld_pairs.py [--out withheld.txt]`

A reader who finds the right ComicVine volume for a shelf, and then sees that the same id is the STORED
link of a neighbouring shelf that is plainly a different comic, is in a trap: writing the id merges the two
(PLAN §6.5), and writing `-` throws away a correct answer. Twenty-six readers wrote the answer into an `N`
line instead — "withheld cv=36705: it is the wrong stored link of S2179 … the lead should re-link both
sides" — which is the right instinct and invisible to every tool.

The pair is the unit, not the shelf. Neither side can be decided alone: the withheld shelf only gets its id
once the partner is re-linked or refused, and the partner's wrongness is only demonstrable next to the shelf
that the volume actually describes. So this emits BOTH sides, in `--revisit-file` shape, so one revisit
batch puts them in front of one reader together.

Second source, same shape of problem: an `F <partner> wrong-cv-link` written from OUTSIDE the partner's own
batch. That reader was looking at a different shelf, saw the neighbour's stored link was wrong, and said so
where nobody owns the follow-up — the partner's own batch never re-reads it.

Writes `docs/books/identity/withheld.txt`. Reads decision files and the live DB, read-only.
"""
import os
import re
import sys
HERE = os.path.dirname(os.path.abspath(__file__))

import idbase
from idbase import Evidence

OUT = "withheld.txt"
for k, a in enumerate(sys.argv):
    if a == "--out" and k + 1 < len(sys.argv):
        OUT = sys.argv[k + 1]
if not os.path.isabs(OUT):
    OUT = os.path.join(idbase.ROOT, OUT)

RX_WITHHELD = re.compile(r"withheld\s+cv=(\d+)", re.I)
RX_PARTNER = re.compile(r"\bS(\d+)\b")
RX_N = re.compile(r"^N\s+S?(\d+)\s+(.*)$")
RX_F = re.compile(r"^F\s+S?(\d+)\s+(\S+)\s*\|?\s*(.*)$")

ev = Evidence()
decides, winner, superseded, dupes = idbase.scan_decisions()

# what each batch was supposed to be about, so "written from outside its own batch" is answerable
own = {}
for path in decides:
    base = os.path.splitext(os.path.basename(path))[0]
    ids = os.path.join(idbase.BATCHES, base + ".ids")
    own[path] = ({int(x) for x in open(ids, encoding="utf-8").read().split()}
                 if os.path.isfile(ids) else set())

rows = {}          # sid -> (batch, why); one line per shelf, both sides of every pair present


def add(sid, batch, why):
    if sid in rows:
        if why not in rows[sid][1]:
            rows[sid] = (rows[sid][0], rows[sid][1] + "; " + why)
        return
    rows[sid] = (batch, why)


pairs = []
for path, rec in decides.items():
    base = os.path.splitext(os.path.basename(path))[0]
    for raw in open(path, encoding="utf-8"):
        line = raw.strip()

        m = RX_N.match(line)
        if m:
            sid, text = int(m.group(1)), m.group(2)
            w = RX_WITHHELD.search(text)
            if not w:
                continue
            vid = int(w.group(1))
            # the partner is the shelf the note names; the first S<number> after the withheld id is the
            # reader's own pointer at it, and there is no other source for it
            partners = [int(x) for x in RX_PARTNER.findall(text)]
            partners = [p for p in partners if p != sid and p in ev.shelf_set]
            pairs.append((sid, vid, partners[0] if partners else None, base, text))
            continue

        m = RX_F.match(line)
        if m:
            sid, flag = int(m.group(1)), m.group(2).split("=")[0]
            if flag != "wrong-cv-link":
                continue
            if sid in own.get(path, set()):
                continue                       # its own batch owns the follow-up
            pairs.append((None, None, sid, base, m.group(3)))

for sid, vid, partner, base, text in pairs:
    if sid is not None:
        stored = ev.series.get(partner, {}).get("cvVolumeId") if partner else None
        add(sid, base, f"withheld cv={vid} — the id is right for this shelf but is the stored link of "
                       f"S{partner if partner else '?'}; store it once the partner is re-linked")
        if partner:
            add(partner, base, f"partner of the withheld cv={vid} on S{sid}"
                               + (f"; its stored link is {stored}" if stored else "")
                               + " — re-link it to its own volume or refuse it, then S%d can take cv=%d"
                               % (sid, vid))
    elif partner is not None:
        stored = ev.series.get(partner, {}).get("cvVolumeId")
        add(partner, base, f"another batch flagged wrong-cv-link on this shelf from outside its own batch"
                           + (f" (stored link {stored})" if stored else "")
                           + f": {text[:140]}")

live = {sid: v for sid, v in rows.items() if sid in ev.shelf_set}
dropped = sorted(set(rows) - set(live))

# Stale pairs (added 2026-09-11): a pair a LATER revisit batch already re-read, or whose withheld id is now the
# shelf's stored link (the wave landed it), is finished — listing it again sent 44 of 46 shelves back to a reader.
import json as _json, re as _re
_st = _json.load(open(os.path.join(HERE, os.pardir, "state.json"), encoding="utf-8")) if os.path.exists(
    os.path.join(HERE, os.pardir, "state.json")) else {}
_revisited = {}
for _r in _st.get("revisits", []):
    for _sid in _r["ids"]:
        _revisited.setdefault(_sid, set()).add(_r["batch"])
stale = []
for sid, (batch, why) in list(live.items()):
    # only the WITHHELD side is "landed" when its stored link equals the id; a partner row also names the id
    # ("partner of the withheld cv=N") and its stored link IS that id by definition — it was being dropped
    m = _re.match(r"withheld cv=(\d+)", why)
    stored = ev.series.get(sid, {}).get("cvVolumeId")
    if m and stored == int(m.group(1)):
        stale.append((sid, "landed")); del live[sid]
    elif sid in _revisited and batch not in _revisited[sid]:
        stale.append((sid, "re-read in " + ",".join(sorted(_revisited[sid])))); del live[sid]

lines = [f"# Withheld ComicVine ids and their partners — {len(live)} shelf/shelves, "
         f"{len([p for p in pairs if p[0] is not None])} withheld id(s)",
         "# Both sides of every pair are listed: neither can be decided without the other.",
         "# <sid> <batch> <why>   — feed to: next_batch.py --revisit-file withheld.txt"]
for sid in sorted(live):
    batch, why = live[sid]
    nm = ev.series[sid]["name"][:44]
    lines.append(f"{sid:<7} {batch:<8} {nm}: {why}")
with open(OUT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")

n_with = len([p for p in pairs if p[0] is not None])
n_flag = len([p for p in pairs if p[0] is None])
print(f"{len(live)} shelf/shelves -> {OUT}")
print(f"   {n_with} withheld-cv note(s), {n_flag} out-of-batch wrong-cv-link flag(s)")
print(f"   {len([p for p in pairs if p[0] is not None and p[2] is None])} withheld note(s) named no live partner")
if stale:
    print(f"   {len(stale)} stale pair(s) skipped (already landed or re-read): {stale[:6]}")
if dropped:
    print(f"   {len(dropped)} shelf/shelves dropped — no longer file-holding (a landed wave moved them): {dropped[:8]}")
