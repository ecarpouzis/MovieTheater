"""TOOLS_TODO 32: re-probe every winning `cv=-` / `F provider-missing` shelf with the FIXED ComicVine name index.

`python reprobe_cvmissing.py [--cursor 0] [--limit 500] [--all] [--out reprobe.tsv] [--show 8] [--near 1]`

Why. Until TODO 30 the CV name index was keyed by cvref's stored `normName`, which drops "of" / "the" / "and" /
"a" ANYWHERE in a name, while every probe is `norm_name` (which keeps them) — so ~34k of 154k volumes were
invisible to an exact probe, and a reader who saw "0 CV hits" may have written `cv=-` or `F provider-missing`
about a run that exists. This tool asks exactly that question, and only that: for each such shelf, run the
packet's OWN probe set (`identity_packet._probe_set` — the shelf name, its parsed keys, the suffix-stripped
variants, the v1 candidates' names, the per-file GCD names) against both keyings, and report the volumes the
fixed index finds that the old one could not. A hit the old index could ALSO see was already on the packet the
reader decided on, so it is not news and is not reported; neither is a hit on the CV the shelf already stores (its
winning S line or its CvVolumeId) — there the `provider-missing` was about the GCD leg (R-061: 45 of 48 hits).

A hit is `near` when its start year is within `--near` (default 1) of the shelf's first year, else `far` — far
hits print (a relaunch or a reprint line can start years later) but a lead should read `near` first.

It READS (decides nothing, writes nothing but its sheet). The sheet's first column is `S<sid>`, signal `g`, so it
feeds `next_batch.py --s2-file` directly (and can be concatenated onto triage_09's sheet).

Chunked by shelf id with a cursor (global rule: nothing iterates the whole population in one call): each chunk
prints `{processed, remaining, nextCursor, counts}`; `--all` drives the chunks to the end; a re-run with
`--cursor <nextCursor>` continues, appending to `--out`. The indexes load once per run.
"""
import sys
from collections import Counter, defaultdict

import idbase
from idbase import norm_name
import identity_packet
from identity_packet import Ctx


class Reprobe:
    def __init__(self, near):
        self.near = near
        self.ev = ev = idbase.Evidence()
        self.ctx = Ctx(ev)
        self.decides, self.winner, _s, _d = idbase.scan_decisions()

        # the population: live shelves whose WINNING line is an S with no cv, or carries F provider-missing
        self.pop, self.why, self.have = [], {}, {}
        for sid, w in self.winner.items():
            if sid not in ev.shelf_set:
                continue
            rec = self.decides[w]
            kind = rec["kinds"].get(sid)
            toks = rec.get("flags_full", {}).get(sid, ())
            pm = any(t.split("=", 1)[0] == "provider-missing" for t in toks)
            cvless = kind == "S" and not str(rec["cv"].get(sid) or "").isdigit()
            if pm or cvless:
                self.pop.append(sid)
                self.why[sid] = "+".join(x for x, on in (("S cv=-", cvless), ("provider-missing", pm)) if on)
                # the CV this shelf already HAS (its winning S line, its stored CvVolumeId): a hit on it is not
                # news — R-061 found 45 of 48 "confirmed" hits were exactly this, the provider-missing being
                # about the GCD leg
                have = {int(v) for v in (rec["cv"].get(sid), ev.series.get(sid, {}).get("cvVolumeId"))
                        if str(v or "").isdigit()}
                self.have[sid] = have
        self.pop.sort()

        # the OLD keying (cvref normName) beside the fixed one (norm_name(name)) — identity_packet.Ctx.cv_index
        self.new_idx = self.ctx.cv_index()
        self.old_idx = defaultdict(set)
        if self.ctx.cvref is not None:
            for vid, norm in self.ctx.cvref.execute("SELECT volId, normName FROM cv_vol"):
                if norm:
                    self.old_idx[norm].add(vid)

    def hits(self, sid):
        ev = self.ev
        s = ev.series.get(sid)
        if not s:
            return []
        keys = sorted(ev.keys.get(sid, ()))
        probes = identity_packet._probe_set(ev, self.ctx, sid, s, keys, ev.candidates(sid), want_gcd_names=True)
        y0 = s.get("yearStart")
        out, seen = [], set()
        for probe, label in probes:
            k = norm_name(probe)
            if not k:
                continue
            old = self.old_idx.get(k, ())
            for r in self.new_idx.get(k, ()):
                vid = r[0]
                if vid in seen or vid in old or vid in self.have.get(sid, ()):
                    continue
                seen.add(vid)
                y = r[3]
                gap = abs(int(y) - int(y0)) if (y and y0) else None
                out.append({"vid": vid, "name": r[1], "year": y, "count": r[4], "pub": r[5],
                            "probe": probe, "label": label or "own spelling", "gap": gap,
                            "near": gap is not None and gap <= self.near})
        out.sort(key=lambda h: (not h["near"], h["gap"] if h["gap"] is not None else 9999, h["vid"]))
        return out


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = sys.argv[1:]

    def opt(name, default=None):
        return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else default

    cursor, limit, show = int(opt("--cursor", 0)), int(opt("--limit", 500)), int(opt("--show", 8))
    out = opt("--out")
    r = Reprobe(int(opt("--near", 1)))
    print(f"population: {len(r.pop):,} live shelves whose winning line is S cv=- or carries F provider-missing "
          f"({dict(Counter(r.why.values()))})")
    sink = open(out, "a" if cursor else "w", encoding="utf-8") if out else None
    if sink and not cursor:
        sink.write("# shelf\tsignal\tdetail   (reprobe_cvmissing.py, TOOLS_TODO 32; feed: next_batch.py --s2-file)\n")
    total, shelves_near, shelves_any, examples = Counter(), set(), set(), []
    while True:
        chunk = [s for s in r.pop if s > cursor][:limit]
        counts = Counter()
        for sid in chunk:
            hs = r.hits(sid)
            if not hs:
                continue
            shelves_any.add(sid)
            if any(h["near"] for h in hs):
                shelves_near.add(sid)
            for h in hs[:6]:
                tag = "near" if h["near"] else "far"
                counts[tag] += 1
                detail = (f"{r.why[sid]}: CV {h['vid']} \"{h['name']}\" {h['year'] or '?'} {h['pub'] or '?'} "
                          f"{h['count'] or '?'} issues — {tag}"
                          + (f" (gap {h['gap']})" if h["gap"] is not None else "")
                          + f", found by '{h['probe']}' ({h['label']}); invisible before TODO 30")
                if sink:
                    sink.write(f"S{sid}\tg\t{detail}\n")
                if len(examples) < show and h["near"]:
                    examples.append((sid, detail))
        total.update(counts)
        nxt = chunk[-1] if chunk else None
        remaining = sum(1 for s in r.pop if s > (nxt if nxt is not None else cursor))
        print({"processed": len(chunk), "remaining": remaining, "nextCursor": nxt,
               "counts": dict(sorted(counts.items()))})
        if sink:
            sink.flush()
        if "--all" not in a or not chunk or remaining == 0:
            break
        cursor = nxt
    if sink:
        sink.close()
    print(f"\nhit rows: {dict(sorted(total.items()))}; shelves with a new hit (this run): {len(shelves_any):,}, "
          f"of them with a NEAR hit: {len(shelves_near):,}")
    for sid, d in examples:
        print(f"   S{sid:<7} {r.ev.series[sid]['name'][:40]:<40} {d[:170]}")


if __name__ == "__main__":
    main()
