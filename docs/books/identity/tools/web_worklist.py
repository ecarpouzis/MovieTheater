"""One sheet of everything that needs the web, so the ≤15-fetches-per-batch budget is spent on purpose.

`python web_worklist.py [--out web-worklist.md] [--compare <file appended by compare_decisions --worklist>]`

Three streams of "we could not settle this locally" exist and none of them was ever in one place: an
`F <sid> needs-web` flag raised mid-batch, a shelf a model-comparison run disagreed about
(`compare_decisions.py --worklist`), and every shelf a reader parked at 0.7. Left separate they get worked
twice or not at all, because each looks small on its own.

So this merges them into one entry per shelf, and every entry carries the same three things, in the order
a person needs them: the candidate ids that are actually in play, the FACT that would settle it (not "check
this" — the specific number or year to look for), and the page URLs to look at. PLAN §7-S caps web use at
15 fetches per batch and requires each one to be cited in the evidence clause, which only works if the
question was written down before the browser opened.

Writes `docs/books/identity/web-worklist.md`. Reads only decision files and the live DB, read-only.
"""
import os
import re
import sys
from collections import defaultdict

import idbase
from idbase import Evidence

OUT = "web-worklist.md"
COMPARE = None
for k, a in enumerate(sys.argv):
    if a == "--out" and k + 1 < len(sys.argv):
        OUT = sys.argv[k + 1]
    if a == "--compare" and k + 1 < len(sys.argv):
        COMPARE = sys.argv[k + 1]
if not os.path.isabs(OUT):
    OUT = os.path.join(idbase.ROOT, OUT)

RX_S = re.compile(r"^S\s+(\d+)\s+cv=(\S+)\s+gcd=(\S+)\s+([\d.]+)\s*\|\s*(.*)$")
RX_R = re.compile(r"^R\s+(\d+)\s*\|\s*(.*)$")
RX_F = re.compile(r"^F\s+(\d+)\s+(\S+)\s*\|?\s*(.*)$")


def cv_url(v):
    return f"https://comicvine.gamespot.com/volume/4050-{v}/" if v and str(v).isdigit() else None


def gcd_url(g):
    return f"https://www.comics.org/series/{g}/" if g and str(g).isdigit() else None


ev = Evidence()
decides, winner, superseded, dupes = idbase.scan_decisions()

# ── stream 1 and 3: the flags and the 0.7 shelves, read from the file that is IN FORCE ────────────
entries = {}


def entry(sid, base):
    """Created ONLY when there is a question. An earlier draft used a defaultdict and touched it on every
    line it inspected, so all 3,942 decided shelves appeared on the worklist — a list of everything is a
    list of nothing, which is the failure this tool exists to avoid."""
    e = entries.setdefault(sid, {"why": [], "cv": set(), "gcd": set(), "note": [], "batch": base})
    e["batch"] = e["batch"] or base
    return e


for path, rec in decides.items():
    base = os.path.splitext(os.path.basename(path))[0]
    for raw in open(path, encoding="utf-8"):
        line = raw.strip()
        m = RX_S.match(line) or RX_R.match(line) or RX_F.match(line)
        if not m:
            continue
        sid = int(m.group(1))
        if winner.get(sid) != path:          # a superseded line is not a question any more
            continue
        if line.startswith("S "):
            _, cv, gcd, conf, why = m.groups()
            if conf != "0.7":
                continue
            e = entry(sid, base)
            e["why"].append("parked at 0.7 — one leg, something unexplained")
            e["note"].append(why)
            if cv != "-":
                e["cv"].add(cv)
            if gcd != "-":
                e["gcd"].add(gcd.lstrip("s"))
        elif line.startswith("F "):
            _, flag, detail = m.groups()
            if flag.split("=")[0] not in ("needs-web", "provider-missing", "needs-fetch"):
                continue
            e = entry(sid, base)
            e["why"].append(f"F {flag}")
            e["note"].append(detail)          # verbatim: the reader's own question, not a paraphrase
            for v in re.findall(r"cv[=: ](\d+)", detail):
                e["cv"].add(v)
            for g in re.findall(r"gcd[=: ]s?(\d+)", detail):
                e["gcd"].add(g)

# ── stream 2: the model-comparison disagreements, appended by compare_decisions.py --worklist ─────
compare_blocks = []
if COMPARE and os.path.isfile(COMPARE):
    block, sid = [], None
    for raw in open(COMPARE, encoding="utf-8"):
        if raw.startswith("## S"):
            if sid:
                compare_blocks.append((sid, "".join(block)))
            sid = int(raw[4:].strip())
            block = []
        elif sid:
            block.append(raw)
    if sid:
        compare_blocks.append((sid, "".join(block)))
    for sid, _b in compare_blocks:
        entry(sid, "compare")["why"].append("model comparison disagreed on the verdict or the ids")
compare_text = dict(compare_blocks)

# ── the settling fact: what to LOOK for, computed per shelf from what we already hold ─────────────
def settling_fact(sid):
    s = ev.series.get(sid)
    if not s:
        return "the shelf no longer exists — drop this entry"
    n = ev.numbered.get(sid, 0)
    size = ev.size.get(sid, 0)
    cols = ev.collections.get(sid, 0)
    bits = []
    if n:
        bits.append(f"we hold {n} numbered issue file(s)")
    if cols:
        bits.append(f"{cols} collected edition(s)")
    if s["yearStart"]:
        bits.append(f"our years {s['yearStart']}-{s['yearEnd'] or '?'}")
    holding = "; ".join(bits) or f"{size} file(s)"
    if n:
        return (f"does the candidate's issue list REACH the numbers we hold, and does its start year match? "
                f"({holding}) — a record with fewer issues than we own is not this run")
    return (f"is there a NUMBERED run behind these books at all, and if so what are its issue count and "
            f"start year? ({holding}) — the run wins over a collected-edition record when one exists")


rows = sorted(entries.items())
lines = [f"# Web worklist — {len(rows)} shelf/shelves",
         "",
         "Each entry: the candidates in play, the fact that would settle it, the pages to open. PLAN §7-S:",
         "at most 15 fetches per batch, and every one cited in the evidence clause as",
         "`web: comics.org/series/<id> lists #1-45 (1998-2002)`.", ""]
for sid, e in rows:
    s = ev.series.get(sid)
    name = s["name"] if s else "(gone)"
    tier = ev.tier(sid)[0] if s else "?"
    lines.append(f"## S{sid} {name}  [tier {tier}]  {e['batch'] or ''}")
    lines.append(f"- why: {'; '.join(dict.fromkeys(e['why']))}")
    # candidates already on the page: the stored link, then whatever the decision named
    cv = set(e["cv"])
    if s and s["cvVolumeId"]:
        cv.add(str(s["cvVolumeId"]))
    gcd = set(e["gcd"]) | {str(g) for g in ev.gcd_ids(sid)}
    lines.append(f"- candidates: cv {sorted(cv) or '—'} · gcd {sorted(gcd) or '—'}")
    lines.append(f"- settle: {settling_fact(sid)}")
    urls = [u for u in [cv_url(v) for v in sorted(cv)] + [gcd_url(g) for g in sorted(gcd)] if u]
    lines.append(f"- pages: {' | '.join(urls[:6]) or 'search comics.org and comicvine by the shelf name'}")
    for nte in dict.fromkeys(e["note"]):
        lines.append(f"- said: {nte[:300]}")
    if sid in compare_text:
        lines.append("- comparison:")
        lines += ["  " + l.rstrip() for l in compare_text[sid].strip().splitlines()]
    lines.append("")

with open(OUT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")
print(f"{len(rows)} shelf/shelves -> {OUT}")
by = defaultdict(int)
for _sid, e in rows:
    for w in dict.fromkeys(e["why"]):
        by[w.split(" —")[0]] += 1
for k, v in sorted(by.items(), key=lambda x: -x[1]):
    print(f"   {v:>4}  {k}")
