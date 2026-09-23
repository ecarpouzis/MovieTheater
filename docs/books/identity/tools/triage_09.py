"""S.2 triage of the 0.9 shelves (TOOLS_TODO 23): which of them carry a SIGNAL, one row per (shelf, signal, detail).

`python triage_09.py [--cursor 0] [--limit 1000] [--all] [--out triage.tsv] [--show 5]`

Why. After wave 12 every shelf has a reading, and 6,327 of them won at 0.9 ("one leg plus our own naming /
years / count agree, or two legs with one unexplained residue"). PLAN's S.2 once said: re-read every 0.9 blind.
Eric deferred that — a second blind reading of a clean one-leg shelf buys nothing but tokens. What is worth a
second reader is a 0.9 whose own data now argues with it. So this tool READS (it decides nothing, writes nothing
but its sheet) and lists the 0.9 shelves carrying one of six signals:

  a  claimed      the S's CV volume / GCD series is also the id of another live shelf's winning S line, or of
                  an `I` / `C` line on a book of another shelf (reported as a-S / a-C / a-I: a C naming this run
                  from a trade elsewhere is often exactly right, and the lead should see that sub-count apart)
  b  cv-perfile   a MAJORITY of the shelf's per-file CV links name a volume other than its S volume
  c  range        a judged range, a `C` line on the S's own run, or GCD's own "Collects #a-b" note, that does
                  not fit the S run's count or cached numbering (gcdnotes.py's contradiction check)
  d  flag         an open ContainmentFlag
  e  size         21+ files — a big shelf is where a one-leg 0.9 hides a second run
  f  touched      a landed merge folded another shelf INTO it after its decision landed, or its file count
                  has moved since the packet it was decided on (a split took files away)

Signal-free 0.9 shelves stay at 0.9 and are DONE. The sheet is the feed for `next_batch.py --s2-file` (first
column `S<sid>`), which emits them as `R-` revisit batches in folder order, 60 shelves a batch.

Chunked by shelf id with a cursor (global rule: nothing iterates the whole library in one call): each chunk
prints `{processed, remaining, nextCursor, counts}`; `--all` drives the chunks to the end and totals them; a
re-run with `--cursor <nextCursor>` continues, appending to `--out`. The evidence is loaded once per run (the
Evidence sweeps are seconds); the chunk bounds the per-shelf work and makes progress visible.
"""
import os
import re
import sys
from collections import Counter, defaultdict

import gcdnotes
import idbase

SIZE_SIGNAL = 21
RX_HEAD = re.compile(r"^== S(\d+) .*?(\d+) files / (\d+) collections")


def lines_of(path):
    for raw in open(path, encoding="utf-8"):
        yield raw.strip()


class Triage:
    def __init__(self):
        self.ev = ev = idbase.Evidence()
        self.gcd = idbase.open_gcd_dump()
        self.cvref = idbase.open_cv_ref()
        self.notes = gcdnotes.Notes(self.gcd, ev.con)
        self.judged, self.runs = gcdnotes.judged_ranges(ev.con)
        self.decides, self.winner, _s, _d = idbase.scan_decisions()
        pop = idbase.item_population(ev, self.decides, self.winner)
        self.line_shelves = pop["line_shelves"]

        # the population: live shelves whose WINNING line is an S at 0.9
        self.s_cv, self.s_gcd = {}, {}
        self.pop = []
        by_cv, by_gcd = defaultdict(set), defaultdict(set)
        for sid, w in self.winner.items():
            rec = self.decides[w]
            if rec["kinds"].get(sid) != "S" or sid not in ev.shelf_set:
                continue
            cv, g = rec["cv"].get(sid), rec["gcd"].get(sid)
            if cv:
                by_cv[cv].add(sid)
            if g:
                by_gcd[g].add(sid)
            if rec["confs"].get(sid) == "0.9":
                self.pop.append(sid)
                self.s_cv[sid], self.s_gcd[sid] = cv, g
        self.pop.sort()
        self.by_cv, self.by_gcd = by_cv, by_gcd
        # Declared merges are not a claim: an Epic Collection singleton that shares its GCD line with three
        # siblings and says `merge-with` about them has already answered the question signal (a) asks.
        self.partners = defaultdict(set)
        for rec in self.decides.values():
            for sid, toks in rec.get("flags_full", {}).items():
                for tok in toks:
                    if tok.startswith("merge-with="):
                        other = tok.split("=", 1)[1].lstrip("S")
                        if other.isdigit():
                            self.partners[sid].add(int(other))
                            self.partners[int(other)].add(sid)
        self.skipped_declared = Counter()

        # item -> shelf, for "a line on a book of ANOTHER shelf"
        self.shelf_of = {r[0]: r[1] for r in ev.con.execute(
            "SELECT Id, SeriesId FROM Item WHERE Kind = 0 AND coalesce(IsExcluded,0) = 0 AND SeriesId IS NOT NULL")}

        # every I and C line's ids, resolved to the RUN they name (issue -> volume / series)
        self.i_cv_iss, self.i_gcd_iss, self.i_gcd_ser = {}, {}, {}
        self.c_lines = defaultdict(list)                 # itemId -> [({leg: key}, a, b)]
        for p, rec in self.decides.items():
            for iid, cl in rec["collects"].items():
                for ids, a, b, _conf in cl:
                    self.c_lines[iid].append((ids, a, b))
            for line in lines_of(p):
                if not line.startswith("I "):
                    continue
                head = line.split("|", 1)[0].split()
                if len(head) < 3 or not head[1].isdigit():
                    continue
                iid = int(head[1])
                kv = dict((t.split("=", 1) + [""])[:2] for t in head[2:] if "=" in t)
                if kv.get("cv", "").isdigit():
                    self.i_cv_iss[iid] = int(kv["cv"])
                g = kv.get("gcd", "")
                if g.isdigit():
                    self.i_gcd_iss[iid] = int(g)
                elif g.startswith("s") and g[1:].isdigit():
                    self.i_gcd_ser[iid] = int(g[1:])
        self.iss_vol = self._resolve(self.cvref, "SELECT issueId, volId FROM cv_iss WHERE issueId IN ({})",
                                     set(self.i_cv_iss.values()))
        self.iss_ser = self._resolve(self.gcd, "SELECT id, series_id FROM gcd_issue WHERE id IN ({})",
                                     set(self.i_gcd_iss.values()))
        claims_cv, claims_gcd = defaultdict(list), defaultdict(list)
        for iid, iss in self.i_cv_iss.items():
            v = self.iss_vol.get(iss)
            if v:
                claims_cv[v].append(("I", iid))
        for iid, iss in self.i_gcd_iss.items():
            s = self.iss_ser.get(iss)
            if s:
                claims_gcd[s].append(("I", iid))
        for iid, s in self.i_gcd_ser.items():
            claims_gcd[s].append(("I", iid))
        for iid, cl in self.c_lines.items():
            for ids, _a, _b in cl:
                if str(ids.get("cv", "")).isdigit():
                    claims_cv[int(ids["cv"])].append(("C", iid))
                if str(ids.get("gcd", "")).lstrip("s").isdigit():
                    claims_gcd[int(str(ids["gcd"]).lstrip("s"))].append(("C", iid))
        self.claims_cv, self.claims_gcd = claims_cv, claims_gcd

        # (f) when did each batch land: the earliest undo journal named for it
        self.landed_at = {}
        if os.path.isdir(idbase.UNDO):
            for f in os.listdir(idbase.UNDO):
                m = re.match(r"^([A-Z]+-\d+)-(\d{8})-(\d{6})\.jsonl$", f)
                if m:
                    t = f"{m.group(2)[:4]}-{m.group(2)[4:6]}-{m.group(2)[6:]} " \
                        f"{m.group(3)[:2]}:{m.group(3)[2:4]}:{m.group(3)[4:]}"
                    self.landed_at[m.group(1)] = min(self.landed_at.get(m.group(1), t), t)
        self.stamps = sorted(set(self.landed_at.values()))
        self.merged_into = defaultdict(list)
        for old, new, at in ev.con.execute("SELECT OldSeriesId, NewSeriesId, MergedAt FROM SeriesMerge "
                                           "WHERE NewSeriesId IS NOT NULL"):
            self.merged_into[new].append((old, (at or "")[:19]))
        self._packet_sizes = {}

        self.cv_count = lambda v: (ev.cv_volume.get(v) or {}).get("countOfIssues")
        self._gcd_count = {}

    @staticmethod
    def _resolve(con, sql, ids):
        out, ids = {}, sorted(ids)
        if con is None:
            return out
        for k in range(0, len(ids), 900):
            part = ids[k:k + 900]
            for a, b in con.execute(sql.format(",".join("?" * len(part))), part):
                out[a] = b
        return out

    def gcd_count(self, g):
        if g not in self._gcd_count:
            s = self.ev.gcd_series.get(g)
            n = s["issueCount"] if s else None
            if n is None and self.gcd is not None:
                r = self.gcd.execute("SELECT issue_count FROM gcd_series WHERE id=?", (g,)).fetchone()
                n = r[0] if r else None
            self._gcd_count[g] = n
        return self._gcd_count[g]

    def packet_size(self, batch, sid):
        """The shelf's file count as its deciding packet printed it (the batch file's `== S<sid>` header)."""
        if batch not in self._packet_sizes:
            sizes = {}
            p = os.path.join(idbase.BATCHES, batch + ".txt")
            if os.path.isfile(p):
                for line in lines_of(p):
                    m = RX_HEAD.match(line)
                    if m:
                        sizes[int(m.group(1))] = int(m.group(2))
            self._packet_sizes[batch] = sizes
        return self._packet_sizes[batch].get(sid)

    def fits_run(self, a, b, cv, g):
        """Does #a-b fit the S run: inside the cached CV numbering when there is one, else within the larger
        of the two legs' counts. Unknown counts fit (no evidence is not a signal)."""
        nums = self.ev.cv_issue_nums.get(cv) if cv else None
        if nums:
            lo, hi = min(nums), max(nums)
            if lo - 0.5 <= a and b <= hi + 0.5:
                return True
        counts = [n for n in (self.cv_count(cv) if cv else None, self.gcd_count(g) if g else None) if n]
        if not counts:
            return not nums
        return b <= max(counts) + 0.5 and a >= 0

    def signals(self, sid):
        ev, out = self.ev, []
        cv, g = self.s_cv.get(sid), self.s_gcd.get(sid)
        # (a) claimed — by a shelf that has NOT declared itself the same comic (merge-with, either direction)
        mine = self.partners.get(sid, set()) | {sid}
        s_partners = set()
        for leg, key, by in (("cv", cv, self.by_cv), ("gcd", g, self.by_gcd)):
            for other in sorted((by.get(key, set()) if key else set()) - {sid}):
                if other in mine:
                    self.skipped_declared["a-S"] += 1
                    continue
                s_partners.add(other)
                out.append(("a-S", f"{leg} {key} is also the S of S{other}"))
        for leg, key, claims in (("cv", cv, self.claims_cv), ("gcd", g, self.claims_gcd)):
            if not key:
                continue
            elsewhere = Counter()
            for kind, iid in claims.get(key, ()):
                sh = self.shelf_of.get(iid)
                if sh is None or sh in mine:
                    continue
                if sh in s_partners:
                    # the partner's S already carries this id — the a-S row said it; the books under that S
                    # repeat it by construction
                    continue
                elsewhere[(kind, sh)] += 1
            for (kind, sh), n in sorted(elsewhere.items()):
                out.append((f"a-{kind}", f"{leg} {key} named by {n} {kind} line(s) on books of S{sh}"))
        # (b) the per-file CV majority names another volume
        pf = ev.cv_files.get(sid, Counter())
        if cv and pf:
            total, agree = sum(pf.values()), pf.get(cv, 0)
            if total - agree > total / 2:
                top = ", ".join(f"{v}x{n}" for v, n in pf.most_common(3) if v != cv)
                out.append(("b", f"{total - agree}/{total} per-file CV links name another volume ({top})"))
        # (c) ranges that do not fit the S run
        gmap = self.notes.item_gcd_issues()
        for iid in self.by_shelf.get(sid, ()):
            jr = self.judged.get(iid)
            runs_here = self.runs.get(iid, ())
            if jr and sid not in self.line_shelves and not runs_here and (cv or g):
                a, b = float(jr[0]), float(jr[1])
                if not self.fits_run(a, b, cv, g):
                    out.append(("c", f"item {iid} judged #{idbase.fmt_num(a)}-{idbase.fmt_num(b)} does not fit the "
                                     f"S run (cv {cv or '-'} count {self.cv_count(cv) if cv else '-'}, gcd {g or '-'} "
                                     f"count {self.gcd_count(g) if g else '-'})"))
            for ids, a, b in self.c_lines.get(iid, ()):
                own = (cv and str(ids.get("cv")) == str(cv)) or (g and str(ids.get("gcd", "")).lstrip("s") == str(g))
                if own and not self.fits_run(a, b, cv, g):
                    out.append(("c", f"item {iid} C #{idbase.fmt_num(a)}-{idbase.fmt_num(b)} on the S's own run "
                                     f"does not fit its count"))
            if jr:
                row = self.notes.issue(gmap[iid]) if iid in gmap else None
                c = gcdnotes.contradiction(jr, gcdnotes.parse_notes(row["notes"]) if row else [], runs_here)
                if c:
                    out.append(("c", f"item {iid} {c[1]}"))
        # (d) an open flag
        for f in ev.open_flags(sid):
            out.append(("d", f"{f['flag']}{' item ' + str(f['itemId']) if f['itemId'] else ''}"))
        # (e) size
        if ev.size.get(sid, 0) >= SIZE_SIGNAL:
            out.append(("e", f"{ev.size[sid]} files"))
        # (f) touched since decided
        w = self.winner.get(sid)
        batch = os.path.splitext(os.path.basename(w))[0] if w else None
        at = self.landed_at.get(batch)
        merges = self.merged_into.get(sid, ())
        if at:
            # The resolve that follows a landing merges what that landing linked — the decision's OWN effect.
            # A merge counts as "since" only once a LATER wave has landed after it.
            nxt = next((s for s in self.stamps if s > at and s[:13] != at[:13]), None)
            later = [(o, t) for o, t in merges if nxt and t > nxt and o not in self.partners.get(sid, ())]
            if later:
                out.append(("f", f"{len(later)} shelf/shelves merged in after the wave that landed {batch} "
                                 f"({at[:10]}): " + ", ".join(f"S{o}" for o, _ in later[:5])))
        was = self.packet_size(batch, sid) if batch else None
        now = ev.size.get(sid, 0)
        if was is not None and (now < was or (now > was and not merges)):
            out.append(("f", f"{was} files when {batch} was read, {now} now"
                             + (" (files left the shelf — a split?)" if now < was else " (no merge explains it)")))
        return out

    def prepare(self):
        self.by_shelf = defaultdict(list)
        for iid, s in self.shelf_of.items():
            self.by_shelf[s].append(iid)


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = sys.argv[1:]

    def opt(name, default=None):
        return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else default

    cursor, limit, show = int(opt("--cursor", 0)), int(opt("--limit", 1000)), int(opt("--show", 5))
    out = opt("--out")
    t = Triage()
    t.prepare()
    print(f"population: {len(t.pop):,} live shelves whose winning line is S at 0.9")
    sink = open(out, "a" if cursor else "w", encoding="utf-8") if out else None
    if sink and not cursor:
        sink.write("# shelf\tsignal\tdetail   (triage_09.py; feed: next_batch.py --s2-file)\n")
    total, flagged, per_signal_shelves, examples = Counter(), set(), defaultdict(set), defaultdict(list)
    sizes = Counter()
    while True:
        chunk = [s for s in t.pop if s > cursor][:limit]
        counts = Counter()
        for sid in chunk:
            sig = t.signals(sid)
            kinds = {k for k, _ in sig}
            for k, detail in sig:
                counts[k] += 1
                per_signal_shelves[k[0]].add(sid)
                if len(examples[k]) < show:
                    examples[k].append((sid, detail))
                if sink:
                    sink.write(f"S{sid}\t{k}\t{detail}\n")
            if kinds:
                flagged.add(sid)
                n = t.ev.size.get(sid, 0)
                sizes["1 file" if n == 1 else "2-5" if n <= 5 else "6-20" if n <= 20 else "21+"] += 1
        total.update(counts)
        nxt = chunk[-1] if chunk else None
        remaining = sum(1 for s in t.pop if s > (nxt if nxt is not None else cursor))
        print({"processed": len(chunk), "remaining": remaining, "nextCursor": nxt,
               "counts": dict(sorted(counts.items()))})
        if sink:
            sink.flush()
        if "--all" not in a or not chunk or remaining == 0:
            break
        cursor = nxt
    if sink:
        sink.close()
    print(f"\nrows per signal: {dict(sorted(total.items()))}")
    print("shelves per signal: " + ", ".join(f"{k} {len(v):,}" for k, v in sorted(per_signal_shelves.items())))
    print(f"de-duplicated shelves with any signal (this run): {len(flagged):,}  — by size {dict(sizes)}")
    print(f"not counted as claims (the partner declared merge-with): {dict(t.skipped_declared)}")
    for k in sorted(examples):
        for sid, d in examples[k]:
            print(f"   {k:<4} S{sid:<7} {t.ev.series[sid]['name'][:40]:<40} {d[:150]}")


if __name__ == "__main__":
    main()
