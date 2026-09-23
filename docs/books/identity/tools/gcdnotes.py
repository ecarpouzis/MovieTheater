"""What a trade COLLECTS, in GCD's own words: `gcd_issue.notes` ("Collects X #a-b") and `gcd_reprint` (TOOLS_TODO 18 + 21).

`python gcdnotes.py --issue <gcdIssueId>`                       the notes clause + the reprint roll-up of one issue
`python gcdnotes.py --contradictions [--cursor N] [--limit 2000] [--all] [--show 5]`
                                                               every judged range GCD's notes contradict, chunked

Why. R-028 and X-062 settled most `C` ranges from one sentence on the GCD issue row — "Collects Bloodshot (Valiant,
2019 series) #1-6" — and that sentence overturned four judged ranges (Bloodshot 2019 Books 1-4, Life Is Strange:
Coming Home, American Vampire Book One, the X-Men chronology). Readers were deriving ranges from page arithmetic
while the answer sat in a column the packet never printed. So the packet prints it now (identity_packet.py), and
this module is the one parser both the packet and the population check use — a range the packet shows and a
range the check counts must be read by the same code, or the two disagree about what GCD said.

`gcd_reprint` is the structured twin of the prose: one row per reprinted STORY, target issue = the trade, origin
issue = the floppy. Rolled up by origin series it gives the trade's contents without parsing anything; it is
sparser than the notes (indexers fill notes first) and prints beside them, never instead.

The contradiction check is a lie detector, not a ruling (PLAN §0): it FLAGS a judged range the notes disagree
with — the reader decides. Three shapes, counted apart because they mean different things:
  count   one series named, and the notes' issue COUNT differs from the judged range's — the strong one
  offset  one series, same count, different numbers — often a run numbered two ways (Return of the Master is
          CV #1-5 and GCD #103-107); a `C` in that run's numbering answers it
  multi   several series named (an omnibus, a crossover) and the judged range equals none of them
The population check reads only (read-only connections) and is chunked by item id with a cursor, per the global
rule for anything iterating the whole library; `--all` drives the chunks and totals them.
"""
import os
import re
import sys
from collections import Counter, defaultdict

import idbase

RX_CLAUSE = re.compile(r"(?:Collect(?:s|ing|ed)?|Reprints?|Contient)\b[\]\s:]*(.*?)(?:\n\s*\n|$)", re.S | re.I)
_N = r"\d{1,5}(?:\.\d+)?"
RX_NUMS = re.compile(rf"#\s*({_N}(?:\s*[-–]\s*#?{_N})?(?:\s*(?:,|&|\band\b)\s*#?{_N}(?:\s*[-–]\s*#?{_N})?(?![\d]|\s*series))*)")
RX_ONE = re.compile(rf"({_N})(?:\s*[-–]\s*#?({_N}))?")
RX_LINK = re.compile(r"\[gcd_link_series\]\((\d+)\)")
RX_LEAD = re.compile(r"^(?:[\s,;:.\-]|and\b|plus\b|stories from\b|material from\b|issues?\b|\[collects?\])+", re.I)


def parse_notes(notes):
    """-> [{"name": str, "sid": int|None, "ranges": [(a, b), ...]}] — one entry per series the clause names.

    Only the COLLECT clause is read (the first "Collects / Reprints / Contient [Collects]" paragraph): printing
    notes and on-sale dates in the same field carry numbers too, and "#2 printing" is not a range."""
    if not notes:
        return []
    m = RX_CLAUSE.search(notes)
    if not m:
        return []
    clause = m.group(1)
    out, prev = [], 0
    for mm in RX_NUMS.finditer(clause):
        name = clause[prev:mm.start()]
        prev = mm.end()
        link = RX_LINK.search(name)
        name = RX_LINK.sub("", name)
        name = re.sub(r"\s+issues?$", "", RX_LEAD.sub("", name.strip()).strip(" ,;:"))
        ranges = []
        for part in re.split(r"\s*(?:,|&|\band\b)\s*", mm.group(1)):
            one = RX_ONE.search(part)
            if one:
                a = float(one.group(1))
                b = float(one.group(2)) if one.group(2) else a
                if b >= a and b - a < 2000:
                    ranges.append((a, b))
        if ranges:
            out.append({"name": name[-90:], "sid": int(link.group(1)) if link else None, "ranges": ranges})
    return out


def span(ranges):
    return (min(a for a, _ in ranges), max(b for _, b in ranges), sum(int(b - a) + 1 for a, b in ranges))


def fmt_ranges(ranges):
    f = idbase.fmt_num
    return ", ".join(f"#{f(a)}" if a == b else f"#{f(a)}-{f(b)}" for a, b in ranges)


def contradiction(judged, parsed, runs=()):
    """judged = (a, b) on the book's Curated span; parsed = parse_notes(...); runs = [(a, b)] the book's run
    rows (a `C` in another run's numbering). -> None when the notes fit, else (shape, text)."""
    if not parsed or judged is None or judged[0] is None:
        return None
    A, B = float(judged[0]), float(judged[1])
    fits = {(A, B)} | {(float(a), float(b)) for a, b in runs if a is not None}
    for e in parsed:
        lo, hi, _n = span(e["ranges"])
        if (lo, hi) in fits or any(r in fits for r in e["ranges"]):
            return None
    f = idbase.fmt_num
    says = "; ".join(f"{e['name'] or '?'} {fmt_ranges(e['ranges'])}" for e in parsed)[:220]
    if len(parsed) == 1:
        _lo, _hi, n = span(parsed[0]["ranges"])
        shape = "count" if n != int(B - A) + 1 else "offset"
    else:
        shape = "multi"
    return shape, f"RANGE CONTRADICTED by GCD notes ({shape}): judged #{f(A)}-{f(B)}, GCD says {says}"


class Notes:
    """The dump lookups, cached: an issue's notes + number + series, its reprint roll-up, and every item's GCD
    ISSUE id (the reader's `I` line first — it is the decision — then v1's per-file link)."""

    def __init__(self, gcd, con=None):
        self.gcd = gcd
        self.con = con
        self._item_gcd = None

    def issue(self, gid):
        if self.gcd is None:
            return None
        r = self.gcd.execute("""SELECT i.id, i.number, i.series_id, s.name, s.year_began, i.notes, i.page_count
                                FROM gcd_issue i JOIN gcd_series s ON s.id = i.series_id WHERE i.id = ?""",
                             (gid,)).fetchone()
        return dict(zip(("id", "number", "seriesId", "series", "year", "notes", "pages"), r)) if r else None

    def reprints(self, gid):
        """gcd_reprint rolled up by ORIGIN series: [(seriesId, "name (year)", [(a, b)...], stories)]."""
        if self.gcd is None:
            return []
        by = defaultdict(set)
        names, stories = {}, Counter()
        for sid, sname, year, number in self.gcd.execute(
                """SELECT o.series_id, s.name, s.year_began, o.number
                   FROM gcd_reprint r JOIN gcd_issue o ON o.id = r.origin_issue_id
                   JOIN gcd_series s ON s.id = o.series_id
                   WHERE r.target_issue_id = ?""", (gid,)):
            names[sid] = f"{sname} ({year or '?'})"
            stories[sid] += 1
            x = idbase.num(number)
            if x is not None:
                by[sid].add(x)
        out = []
        for sid in sorted(names, key=lambda s: -stories[s]):
            xs = sorted(by.get(sid, ()))
            ranges, s0 = [], None
            for k, x in enumerate(xs):
                if s0 is None:
                    s0 = x
                if k + 1 == len(xs) or xs[k + 1] != x + 1:
                    ranges.append((s0, x))
                    s0 = None
            out.append((sid, names[sid], ranges, stories[sid]))
        return out

    def item_gcd_issues(self):
        """{itemId: gcd ISSUE id}. An `I` line's `gcd=<digits>` wins (the `s<series>` form names no issue);
        a later file (a revisit, by precedence) overrides an earlier one. Then v1's link: Status 5
        (identity-read) over 1 (matched)."""
        if self._item_gcd is not None:
            return self._item_gcd
        out = {}
        if self.con is not None:
            for iid, key in self.con.execute("""SELECT ItemId, ProviderKey FROM ItemProviderLink
                                                WHERE Provider = 3 AND Status = 1 AND ProviderKey GLOB '[0-9]*'"""):
                out[iid] = int(key)
            for iid, key in self.con.execute("""SELECT ItemId, ProviderKey FROM ItemProviderLink
                                                WHERE Provider = 3 AND Status = 5 AND ProviderKey GLOB '[0-9]*'"""):
                out[iid] = int(key)
        paths = sorted((os.path.join(idbase.DECISIONS, f) for f in os.listdir(idbase.DECISIONS) if f.endswith(".txt")),
                       key=lambda p: (idbase.revisit_rank(p) or 0, os.path.basename(p))) \
            if os.path.isdir(idbase.DECISIONS) else []
        for p in paths:
            for raw in open(p, encoding="utf-8"):
                if not raw.startswith("I "):
                    continue
                head = raw.split("|", 1)[0].split()
                if len(head) < 3 or not head[1].isdigit():
                    continue
                g = next((t[4:] for t in head[2:] if t.startswith("gcd=")), "")
                if g.isdigit():
                    out[int(head[1])] = int(g)
        self._item_gcd = out
        return out

    def book_lines(self, iid, judged=None, runs=(), indent="     "):
        """The packet lines for one book: GCD's notes clause, the reprint roll-up, and the contradiction flag.
        Empty when GCD says nothing about what the book collects — silence costs no packet line."""
        gid = self.item_gcd_issues().get(iid)
        if gid is None:
            return []
        row = self.issue(gid)
        if not row:
            return []
        L = []
        parsed = parse_notes(row["notes"])
        if parsed:
            L.append(f"{indent}gcd notes {gid} (#{row['number']} of {row['series']}): "
                     + "; ".join(f"{e['name'] or '?'}{' [s' + str(e['sid']) + ']' if e['sid'] else ''} "
                                 f"{fmt_ranges(e['ranges'])}" for e in parsed)[:240])
        rep = self.reprints(gid)
        if rep:
            L.append(f"{indent}gcd_reprint {gid}: " + " · ".join(
                f"s{sid} {nm} {fmt_ranges(rg) if rg else '(unnumbered)'} [{n} stor{'y' if n == 1 else 'ies'}]"
                for sid, nm, rg, n in rep[:4]))
        c = contradiction(judged, parsed, runs)
        if c:
            L.append(f"{indent}⚠ {c[1]}")
        return L


def judged_ranges(con):
    """{itemId: (a, b)} Curated (Source 3) spans, and {itemId: [(a, b)]} their run rows' own ranges."""
    judged = {r[0]: (r[1], r[2]) for r in con.execute(
        "SELECT ItemId, IssueStart, IssueEnd FROM CollectedEditionSpan WHERE Source = 3 AND IssueStart IS NOT NULL")}
    runs = defaultdict(list)
    if con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='CollectedEditionSpanRun'").fetchone():
        for iid, a, b in con.execute("""SELECT ItemId, IssueStart, IssueEnd FROM CollectedEditionSpanRun
                                        WHERE Source = 3 AND IssueStart IS NOT NULL"""):
            runs[iid].append((a, b))
    return judged, runs


def contradictions_chunk(notes, judged, runs, cursor, limit):
    """One bounded chunk of the population check: judged items with id > cursor, `limit` of them."""
    ids = sorted(i for i in judged if i > cursor)[:limit]
    found, counts = [], Counter()
    gmap = notes.item_gcd_issues()
    for iid in ids:
        gid = gmap.get(iid)
        if gid is None:
            counts["no GCD issue id"] += 1
            continue
        row = notes.issue(gid)
        parsed = parse_notes(row["notes"]) if row else []
        if not parsed:
            counts["GCD issue, notes name no range"] += 1
            continue
        counts["notes name a range"] += 1
        c = contradiction(judged[iid], parsed, runs.get(iid, ()))
        if c:
            counts[f"contradicted: {c[0]}"] += 1
            found.append((iid, gid, c[0], c[1]))
        else:
            counts["notes fit the judged range"] += 1
    remaining = sum(1 for i in judged if i > (ids[-1] if ids else cursor))
    return found, counts, (ids[-1] if ids else None), remaining


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = sys.argv[1:]

    def opt(name, default=None):
        return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else default

    con = idbase.open_hot()
    notes = Notes(idbase.open_gcd_dump(), con)
    if "--issue" in a:
        gid = int(opt("--issue"))
        row = notes.issue(gid)
        if not row:
            raise SystemExit(f"no GCD issue {gid} in the dump")
        print(f"GCD issue {gid}: #{row['number']} of {row['series']} ({row['year']}) s{row['seriesId']}, "
              f"{row['pages'] or '?'}pp")
        for e in parse_notes(row["notes"]):
            print(f"   notes:  {e['name'] or '?'}{' [s' + str(e['sid']) + ']' if e['sid'] else ''} {fmt_ranges(e['ranges'])}")
        for sid, nm, rg, n in notes.reprints(gid):
            print(f"   reprint: s{sid} {nm} {fmt_ranges(rg) if rg else '(unnumbered)'}  [{n} stories]")
        if not parse_notes(row["notes"]) and row["notes"]:
            print(f"   (notes, no collect clause): {row['notes'][:200]!r}")
        return
    if "--contradictions" in a:
        judged, runs = judged_ranges(con)
        cursor, limit, show = int(opt("--cursor", 0)), int(opt("--limit", 2000)), int(opt("--show", 5))
        total, allfound, chunks = Counter(), [], 0
        while True:
            found, counts, nxt, remaining = contradictions_chunk(notes, judged, runs, cursor, limit)
            chunks += 1
            total.update(counts)
            allfound += found
            print({"processed": sum(counts.values()), "remaining": remaining, "nextCursor": nxt,
                   "counts": dict(counts)})
            if "--all" not in a or nxt is None or remaining == 0 or nxt == cursor:
                break
            cursor = nxt
        print(f"\n{chunks} chunk(s); totals: {dict(total)}")
        by = Counter(f[2] for f in allfound)
        print(f"contradicted: {len(allfound)} ({dict(by)})")
        for iid, gid, shape, text in [f for f in allfound if f[2] == "count"][:show] + \
                                      [f for f in allfound if f[2] != "count"][:max(0, show - by['count'])]:
            fn = con.execute("SELECT FileName FROM Item WHERE Id=?", (iid,)).fetchone()
            print(f"   item {iid} gcd {gid}  {(fn[0] if fn else '?')[:70]}\n      {text}")
        return
    raise SystemExit(__doc__)


if __name__ == "__main__":
    main()
