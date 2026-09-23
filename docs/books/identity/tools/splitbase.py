"""The split lane's shared layer (TOOLS_TODO 27): which shelves are waiting on a split, what their packet shows,
and where a new ParsedSeriesKey would LAND at the next resolve. Read-only, always.

`python splitbase.py --census [--cursor 0] [--limit 100] [--all] [--show 10]`   the population, chunked

Why a module of its own. 508 shelves carry a winning `F split-needed` and are refused until split, and a blind
re-read cannot fix them: the reader who wrote the flag already said the shelf is several runs. What they need
is a SPLIT decision — which items move to which new key — and that decision is consumed by three tools that
must agree about it: `next_batch.py --splits` hands the shelves out, `check_splits.py` refuses a bad split, and
the census below sizes the lane. Three copies of "which shelves are flagged" or "where does this key land"
would drift, and a drifted landing rule is a split that silently merges a run into an unrelated shelf. So both
live here once, for the same reason the tier rule lives in idbase.

Where a key LANDS is the question that matters, and it is answered by reading the C# rather than guessing:
`books-series-split` only rewrites `ComicDetail.ParsedSeriesKey` and creates a `Series` row for a key whose
canonical form (`parsed:` + `SeriesResolver.NormalizeKey`) is not already taken; `books-resolve --series` then
maps every parsed key to a survivor by (1) an exact `Series.ParsedKey` / alias, (2) the `SeriesKeyLink` row
keyed by that exact spelling (-> `cv:<id>` / `ext:<id>`), (3) the normalized spelling. `landing()` replays those
three lookups against the live tables. A key that reaches an existing shelf is a JOIN — right when the reader
means it (S6791's 1963 run gathering S66349's issues), a silent merge when not.

The census is chunked by shelf id with a cursor (global rule: nothing iterates a whole population in one
call): each chunk prints `{processed, remaining, nextCursor, counts}`; `--all` drives the chunks and totals.
"""
import os
import re
import sys
import unicodedata
from collections import Counter, defaultdict

import idbase
import identity_packet
from idbase import num, short_path

SOLO_LINES = 300          # the same rule as every other lane: a packet this long is a batch of its own
RX_SREF = re.compile(r"\bS(\d{1,6})\b")
RX_EXT = re.compile(r"\.(cbz|cbr|cb7|cbt|pdf|epub)$", re.I)
# where the title stops and the numbering starts — propose_split.py's rule, so the packet's groups are the
# groups that script would have proposed (plus one thing it lacks: a leading chronology prefix is stripped)
RX_STOP = re.compile(
    r"\s+(?:"
    r"v\d{1,3}\b"
    r"|vol(?:ume)?\.?\s*\d{1,3}\b"
    r"|book\s*\d{1,3}\b"
    r"|#\s*\d+"
    r"|\d{1,4}(?:\.\d+)?\s*(?:-|\(|$)"
    r")", re.I)
RX_LEAD_NUM = re.compile(r"^\s*\d{1,4}[\s._-]+")


def normalize_key(parsed_key):
    """`SeriesResolver.NormalizeKey`, ported character for character: trim, lower, drop ONE leading "the ",
    every non-letter/digit -> space, collapse. C#'s `char.IsLetterOrDigit` is letters (L*) plus DECIMAL digits
    (Nd) — Python's `isalnum` also admits `½` and `²`, which C# does not, so the category is tested directly."""
    s = (parsed_key or "").strip().lower()
    if s.startswith("the "):
        s = s[4:]
    s = "".join(c if (c.isalpha() or unicodedata.category(c) == "Nd") else " " for c in s)
    return " ".join(s.split())


def title_of(fn):
    """The title a filename carries, up to its numbering (propose_split.py's `title_of`)."""
    stem = RX_EXT.sub("", fn or "")
    stem = RX_LEAD_NUM.sub("", stem)          # a chronology prefix ("0325 Flashback …") is an order, not a title
    m = RX_STOP.search(stem)
    t = (stem[:m.start()] if m else stem).strip(" -_,")
    return re.sub(r"\s*\((?:19|20)\d{2}\)\s*$", "", t).strip()


# ── the population ────────────────────────────────────────────────────────────────────────────────
def decision_lines(path, sid, items=()):
    """The winning file's lines about one shelf: its S/R, every F, and the N lines on the shelf or its books."""
    items = set(items)
    out = {"head": None, "f": [], "split": [], "n": []}
    for raw in open(path, encoding="utf-8"):
        t = raw.strip()
        if not t or t.startswith("#"):
            continue
        tok = t.split(None, 2)
        if len(tok) < 2:
            continue
        ident = tok[1].lstrip("S")
        if not ident.isdigit():
            continue
        n = int(ident)
        if tok[0] in ("S", "R") and n == sid:
            out["head"] = t
        elif tok[0] == "F" and n == sid:
            out["f"].append(t)
            if len(tok) > 2 and tok[2].split()[0].split("=")[0] == "split-needed":
                out["split"].append(t.split("|", 1)[1].strip() if "|" in t else "")
        elif tok[0] == "N" and (n == sid or n in items):
            out["n"].append(t)
    return out


def population(ev, decides=None, winner=None):
    """{sid: {"file", "kind"}} — live shelves whose WINNING decision carries `F split-needed`.

    The winner, not any file: a revisit that re-decided a shelf as one run (S at 0.9 with the residue named)
    retires an older file's split flag, and a split handed out on a retired flag is a split nobody asked for."""
    if decides is None or winner is None:
        decides, winner, _s, _d = idbase.scan_decisions()
    out = {}
    for sid, w in winner.items():
        if sid not in ev.shelf_set:
            continue
        rec = decides[w]
        if "split-needed" in rec["flags"].get(sid, ()):
            out[sid] = {"file": w, "kind": rec["kinds"].get(sid)}
    return out


# ── where a key lands ─────────────────────────────────────────────────────────────────────────────
class Landing:
    """The resolver's three lookups, replayed read-only (see the module docstring)."""

    def __init__(self, con, shelf_set):
        self.shelf_set = shelf_set
        self.by_exact = {}
        self.by_norm = defaultdict(set)
        self.by_canon = defaultdict(set)
        self.name = {}
        self.dead = set()
        rows = con.execute("""SELECT Id, coalesce(ParsedKey,''), coalesce(CanonicalKey,''),
                                     coalesce(DisplayNameOverride, Name, '')
                              FROM Series WHERE CanonicalKey NOT LIKE 'book:%'""").fetchall()
        for sid, pk, ck, nm in rows:
            self.name[sid] = nm
            if pk:
                self.by_exact.setdefault(pk, sid)
                self.by_norm[normalize_key(pk)].add(sid)
            if ck:
                self.by_canon[ck].add(sid)
        for pk, sid in con.execute("SELECT ParsedKey, SeriesId FROM SeriesAlias WHERE ParsedKey IS NOT NULL"):
            self.by_exact[pk] = sid                 # the alias is the resolver's own last answer: it wins
            self.by_norm[normalize_key(pk)].add(sid)
        self.link = {}
        for pk, prov, key in con.execute("""SELECT ParsedKey, Provider, ProviderKey FROM SeriesKeyLink
                                             WHERE ProviderKey IS NOT NULL AND Provider IN (0, 1)
                                             ORDER BY rowid"""):
            self.link.setdefault(pk, f"{'cv' if prov == 0 else 'ext'}:{key}")

    def land(self, key):
        """-> (how, {sid}) — the Series rows `key` would join at the next resolve; empty = a NEW shelf.
        `how` is 'exact' / 'link' / 'norm' / 'new'."""
        if key in self.by_exact:
            return "exact", {self.by_exact[key]}
        if key in self.link:
            hit = self.by_canon.get(self.link[key], set())
            if hit:
                return "link", set(hit)
        n = normalize_key(key)
        hit = set(self.by_norm.get(n, set())) | set(self.by_canon.get("parsed:" + n, set()))
        if hit:
            return "norm", hit
        return "new", set()

    def live(self, sids):
        return {s for s in sids if s in self.shelf_set}


# ── the packet ────────────────────────────────────────────────────────────────────────────────────
def shelf_items(con, sid):
    return con.execute("""
        SELECT i.Id, i.FileName, i.Path, i.PageCount, coalesce(cd.IsCollection,0), cd.IssueNo,
               coalesce(cd.ParsedSeriesKey,'')
        FROM Item i LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
        ORDER BY i.Path, i.FileName""", (sid,)).fetchall()


def group_items(rows):
    """[((title, folder), [row, ...]), ...] in the packet's G1..Gn order. ONE copy: the packet prints these
    numbers and `check_splits` expands a `{"group": "G3"}` line through this same function (TOOLS_TODO 28c),
    so the group a reader names is the group the verb receives."""
    groups = defaultdict(list)
    for r in rows:
        groups[(title_of(r[1]), short_path(os.path.dirname(r[2] or "")))].append(r)
    return [(g, groups[g]) for g in sorted(groups, key=lambda g: (g[1], g[0]))]


# ── nearby shelves (TOOLS_TODO 28a) ───────────────────────────────────────────────────────────────
RX_LEG_ID = re.compile(r"\b(cv|gcd)\s*[=:#]?\s*(\d{2,7})\b", re.I)
RX_VOLTOK = re.compile(r"^(?:v\d{1,3}|vol|volume|\d+)$")


def title_words(s):
    """A title's words for the nearby match: the resolver's normal form minus years, `vN` and bare numbers —
    'X-Men: Red (2022)' and 'X-Men - Red v1' are both {x, men, red}."""
    return frozenset(w for w in normalize_key(s).split() if not RX_VOLTOK.match(w))


def leg_ids(text):
    """cv / gcd ids a decision line names in prose ('CV 3092 / GCD 2605', 'cv=27202') -> {(leg, id)}."""
    return {(m.group(1).lower(), int(m.group(2))) for m in RX_LEG_ID.finditer(text or "")}


class NearIndex:
    """Every live shelf by its S-line ids (the WINNING decision's `S … cv= gcd=`), by its stored
    Series.CvVolumeId, and by its title words — built once per run from Evidence (in memory; no per-shelf
    query). A join the F line did not name (Elric S6101 -> S6105) is found here or nowhere."""

    def __init__(self, ev, decides=None, winner=None):
        if decides is None or winner is None:
            decides, winner, _s, _d = idbase.scan_decisions()
        self.ev = ev
        self.s_ids = defaultdict(set)         # (leg, id) -> {sid}  from winning S lines
        self.s_of = {}                        # sid -> (file base, conf)
        for sid, w in winner.items():
            rec = decides[w]
            if sid not in ev.shelf_set or rec["kinds"].get(sid) != "S":
                continue
            self.s_of[sid] = (os.path.splitext(os.path.basename(w))[0], rec["confs"].get(sid))
            if rec["cv"].get(sid):
                self.s_ids[("cv", rec["cv"][sid])].add(sid)
            if rec["gcd"].get(sid):
                self.s_ids[("gcd", rec["gcd"][sid])].add(sid)
        # the resolver's own record of merges, so a shelf the R clause names by an id a landed wave has since
        # merged away (Cosplayers S4251 -> S34939 -> S102444) is followed to the shelf that holds it today
        self.merged = dict(ev.con.execute("SELECT OldSeriesId, NewSeriesId FROM SeriesMerge "
                                          "WHERE NewSeriesId IS NOT NULL ORDER BY MergedAt"))
        self.stored_cv = defaultdict(set)
        self.exact, self.plus1 = defaultdict(set), defaultdict(set)
        for sid, s in ev.series.items():
            if s.get("cvVolumeId"):
                self.stored_cv[s["cvVolumeId"]].add(sid)
            for t in {s["name"] or ""} | set(ev.keys.get(sid, ())):
                w = title_words(t)
                if not w:
                    continue
                self.exact[w].add(sid)
                if len(w) > 1:
                    for x in w:                     # w minus one word: the shelf is the title plus ONE word
                        self.plus1[w - {x}].add(sid)

    def s_line(self, sid):
        """'S cv=… gcd=… (C-054 0.9)' for a shelf with a winning S line, else ''."""
        if sid not in self.s_of:
            return ""
        ids = sorted(f"{leg}={v}" for (leg, v), ss in self.s_ids.items() if sid in ss)
        b, conf = self.s_of[sid]
        return f"S {' '.join(ids)} ({b} {conf or '?'})"

    def live_of(self, sid):
        """`sid`, or the shelf a chain of landed merges carried it into; None when that is not a live shelf."""
        seen = set()
        while sid not in self.ev.series and sid in self.merged and sid not in seen:
            seen.add(sid)
            sid = self.merged[sid]
        return sid if sid in self.ev.series else None

    def nearby(self, sid, want_ids, titles, skip=(), cap=12, refs=(), sources=None):
        """-> [token, ...]: live shelves (not `sid`, not `skip`) whose S line or stored cv holds one of
        `want_ids`, or that the decision's own prose names as `S<id>` (`refs`, followed through merges), then
        shelves whose title words equal a run title's (or add one word to it). Id hits first, all of them;
        title hits capped at `cap`, largest first, with the overflow counted. `sources` {(leg, id): label}
        says where an id came from ("R clause", "N line", "collected by item 117741") — TOOLS_TODO 29."""
        ev, skip = self.ev, set(skip) | {sid}
        sources = sources or {}
        why = defaultdict(list)

        def src(key):
            return f" ({sources[key]})" if key in sources else ""

        for leg, v in sorted(want_ids):
            for o in self.s_ids.get((leg, v), ()):
                why[o].append(f"shares {leg}={v}{src((leg, v))}")
            if leg == "cv":
                for o in self.stored_cv.get(v, ()):
                    if not any(t.startswith(f"shares cv={v}") for t in why[o]):
                        why[o].append(f"shares stored cv={v}{src((leg, v))}")
        for r in refs:
            o = self.live_of(r)
            if o is not None and o not in skip:
                why[o].append(f"named S{r} in the decision's R/N lines" + (f" (now S{o})" if o != r else ""))
        by_id = [o for o in why if o not in skip and o in ev.series]
        tw = {title_words(t) for t in titles} - {frozenset()}
        title_hit = defaultdict(str)
        for w in tw:
            for o in self.exact.get(w, ()):
                title_hit[o] = "title"
            for o in self.plus1.get(w, ()):
                title_hit.setdefault(o, "title+1")
        by_title = [o for o in title_hit if o not in skip and o not in why and o in ev.series]
        by_title.sort(key=lambda o: (title_hit[o] != "title", -ev.size.get(o, 0), o))
        out = []
        for o in sorted(by_id, key=lambda o: (-ev.size.get(o, 0), o)):
            out.append(f"S{o} \"{ev.series[o]['name']}\" {ev.size.get(o, 0)}f [{', '.join(why[o])}"
                       f"{' — ' + self.s_line(o) if o in self.s_of else ''}]")
        for o in by_title[:cap]:
            out.append(f"S{o} \"{ev.series[o]['name']}\" {ev.size.get(o, 0)}f [{title_hit[o]}"
                       f"{' — ' + self.s_line(o) if o in self.s_of else ''}]")
        if len(by_title) > cap:
            out.append(f"(+{len(by_title) - cap} more by title)")
        return out


_NEAR = {}
_NOTES = {}


def near_index(ev):
    """One NearIndex per Evidence (the census renders 500 packets; the index is built once)."""
    if id(ev) not in _NEAR:
        _NEAR.clear()
        _NEAR[id(ev)] = NearIndex(ev)
    return _NEAR[id(ev)]


def gcd_notes(ev):
    """One gcdnotes.Notes per Evidence — the GCD dump (read-only) and every item's GCD ISSUE id, loaded once."""
    if id(ev) not in _NOTES:
        import gcdnotes
        _NOTES.clear()
        _NOTES[id(ev)] = gcdnotes.Notes(idbase.open_gcd_dump(), ev.con)
    return _NOTES[id(ev)]


def collected_runs(ev, iid, cap=4):
    """TOOLS_TODO 29: what a trade COLLECTS, as run ids — the item's own GCD issue record (the reader's `I` line,
    else v1's link; gcdnotes.Notes.item_gcd_issues) rolled up by ORIGIN series through `gcd_reprint`, plus any
    series the "Collects …" notes link by id. -> (own, [(gcdSeriesId, "name (year)", ranges)], stamped) or None.

    P-002 missed S9439 (Infinity) because the 1046pp HC's own record is CV 70940 / GCD s177302 — the HC — and
    the RUN it collects, GCD 75977 (S9439's S identity), was printed nowhere. `own` = (gcdIssueId, seriesId,
    series name). `stamped` = the stored row is another book (TOOLS_TODO 25): the roll-up is printed with a
    warning and its ids are NOT probed, because they describe some other book's contents."""
    notes = gcd_notes(ev)
    gid = notes.item_gcd_issues().get(iid)
    if gid is None:
        return None
    row = notes.issue(gid)
    if not row:
        return None
    import gcdnotes
    runs = [(sid, nm, rg) for sid, nm, rg, _n in notes.reprints(gid)]
    have = {r[0] for r in runs}
    for e in gcdnotes.parse_notes(row["notes"]):
        if e["sid"] and e["sid"] not in have and e["sid"] != row["seriesId"]:
            runs.append((e["sid"], e["name"] or "?", e["ranges"]))
            have.add(e["sid"])
    runs = [r for r in runs if r[0] != row["seriesId"]][:cap]
    if not runs:
        return None
    return (gid, row["seriesId"], row["series"]), runs, bool(notes.stamped(iid, row))


def packet(sid, ev, rec, landing=None):
    """One shelf's split packet: the winning decision (R/S + the split-needed F + every other F + the N lines on
    the shelf or its books), the shelves the F line names, the `nearby:` shelves it did not name (TOOLS_TODO 28a;
    probing the R/S clause and N lines too, and each trade's collected runs, printed under it — TOOLS_TODO 29),
    the keys the files carry, and every item grouped by title x folder with its numbers — the same grouping
    `propose_split.py` prints.

    Every item id is printed, because the split's answer is per item and an id a packet hides is an item nobody
    can move. Filenames are NOT all printed: inside a group, 4+ numbered issue files whose names carry the same
    words print as `id #num year pp` tokens under one exemplar filename (S6791's 972 files are ~290 lines this
    way, not ~1,000). A file whose name says anything else prints whole."""
    con, L = ev.con, []
    s = ev.series.get(sid)
    if s is None:
        return [f"== S{sid}  (no longer a file-holding comic shelf)"]
    rows = shelf_items(con, sid)
    dl = decision_lines(rec["file"], sid, [r[0] for r in rows])
    keys = Counter(r[6] for r in rows)
    batch = os.path.splitext(os.path.basename(rec["file"]))[0]
    L.append(f"== S{sid} {s['name']}  [split]  {len(rows)} files / {sum(1 for r in rows if r[4])} collections  "
             f"decided in {batch} ({rec['kind'] or '?'})")
    L.append("   decided: " + (dl["head"] or "(no S/R line)"))
    for f in dl["f"]:
        L.append("   " + f)
    for n in dl["n"]:
        L.append("   " + n)
    named = []
    for text in dl["split"]:
        named += [int(x) for x in RX_SREF.findall(text) if int(x) != sid]
    for other in dict.fromkeys(named):
        o = ev.series.get(other)
        if o is None:
            L.append(f"   named: S{other} (not a file-holding shelf today)")
            continue
        okeys = sorted(ev.keys.get(other, ()))
        L.append(f"   named: S{other} {o['name']}  {ev.size.get(other, 0)} files  keys: {' | '.join(okeys) or '(none)'}"
                 f"  <- a key of this shelf may be used to JOIN it")

    # per-file provider links, per item — v1's evidence, printed per group so a group's run is visible
    cv, gcd = {}, {}
    for iid, prov, sec in con.execute("""
            SELECT l.ItemId, l.Provider, l.SecondaryKey FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
            WHERE i.SeriesId = ? AND l.Status IN (1, 5) AND l.Provider IN (0, 3)""", (sid,)):
        if sec and str(sec).strip().isdigit():
            (cv if prov == idbase.P_CV else gcd)[iid] = int(sec)

    order = group_items(rows)
    # nearby (TOOLS_TODO 28a): live shelves the F line did not name but a run of this shelf may belong to —
    # their S line / stored cv holds an id the F lines or the per-file links name, or their title is a run's
    want = set()
    for f in dl["f"]:
        want |= leg_ids(f)
    want |= {("cv", v) for v in cv.values()} | {("gcd", v) for v in gcd.values()}
    # TOOLS_TODO 29: the decided R/S clause and the N lines are probed too — P-002 missed S102444 (Cosplayers,
    # "CV 72946" in the R clause) and S9439 (Infinity) because only the F line was read — and so are the runs a
    # trade COLLECTS (gcd_reprint / the notes), which is where the Infinity HC's run id lives. `sources` labels
    # each id the F lines and per-file links did not already supply, so the reader sees why a shelf is nearby.
    sources = {}
    for label, texts in (("R clause", [dl["head"] or ""]), ("N line", dl["n"])):
        for t in texts:
            for k in leg_ids(t) - want:
                sources.setdefault(k, label)
    collected = {}
    for r in rows:
        if r[4]:
            got = collected_runs(ev, r[0])
            if got:
                collected[r[0]] = got
                if not got[2]:
                    for g_sid, _nm, _rg in got[1]:
                        sources.setdefault(("gcd", g_sid), f"collected by item {r[0]}")
    want |= set(sources)
    refs = []
    for t in [dl["head"] or ""] + dl["n"]:
        refs += [int(x) for x in RX_SREF.findall(t) if int(x) != sid and int(x) not in named]
    near = near_index(ev).nearby(sid, want, [g[0] for g, _rs in order] + [s["name"] or ""], skip=named,
                                 refs=list(dict.fromkeys(refs)), sources=sources)
    if near:
        L.extend(_wrap(near, "   nearby: ", width=220))
    L.append("   keys now: " + " · ".join(f'"{k or "(none)"}" x{n}' for k, n in keys.most_common()))

    L.append(f"   groups (title x folder): {len(order)}")
    modal = keys.most_common(1)[0][0] if keys else ""
    for k, (g, rs) in enumerate(order, 1):
        nums = [num(r[5]) for r in rs if not r[4] and num(r[5]) is not None]
        pp = [r[3] for r in rs if r[3]]
        cvs = Counter(cv[r[0]] for r in rs if r[0] in cv)
        gcds = Counter(gcd[r[0]] for r in rs if r[0] in gcd)
        legs = []
        if cvs:
            legs.append("cv per-file " + ", ".join(f"{v}x{n}" for v, n in cvs.most_common(3)))
        if gcds:
            legs.append("gcd per-file " + ", ".join(f"{v}x{n}" for v, n in gcds.most_common(3)))
        gkeys = Counter(r[6] for r in rs)
        L.append(f"   G{k} \"{g[0]}\" in {g[1] or '(root)'} — {len(rs)} file(s)"
                 f"{', ' + str(sum(1 for r in rs if r[4])) + ' COL' if any(r[4] for r in rs) else ''}"
                 f"{', numbers ' + identity_packet.ladder(nums) if nums else ''}"
                 f"{', pp ' + str(min(pp)) + '-' + str(max(pp)) if pp else ''}"
                 f"{'; ' + '; '.join(legs) if legs else ''}"
                 f"; key {' | '.join(repr(x or '(none)') for x in gkeys) if len(gkeys) > 1 else repr(next(iter(gkeys)) or '(none)')}")
        # Inside a group the title and folder are already fixed, so what varies is the number, the year and the
        # ripper tag. Items whose filename says nothing beyond that (same `core` as the group's modal one, a
        # numeric issue, not a collection) print as compact `id #num year pp` tokens under ONE exemplar
        # filename; every other item — a collection, an unnumbered file, a name carrying different words —
        # prints its whole filename. Every id is printed either way.
        cores = Counter(core_of(r[1]) for r in rs)
        modal_core = cores.most_common(1)[0][0] if cores else ""
        compact = [r for r in rs if not r[4] and num(r[5]) is not None and core_of(r[1]) == modal_core]
        if len(compact) < 4:
            compact = []
        cset = {r[0] for r in compact}
        if compact:
            L.append(f"      e.g. {compact[0][1]}")
            toks = [f"{r[0]} #{r[5]}{(' ' + year_of(r[1])) if year_of(r[1]) else ''} {r[3] or '?'}pp"
                    + (f' key="{r[6]}"' if len(gkeys) > 1 and r[6] != modal else "") for r in compact]
            L.extend(_wrap(toks, "        "))
        for r in rs:
            if r[0] in cset:
                continue
            other = f'  key="{r[6]}"' if len(gkeys) > 1 and r[6] != modal else ""
            L.append(f"      [{r[0]}]{' COL' if r[4] else ''} {r[1]} ({r[3] or '?'}pp){other}")
            if r[0] in collected:
                (gid, own_sid, own_name), runs, stamped = collected[r[0]]
                own = f"cv {cv[r[0]]} / " if r[0] in cv else ""
                L.append(f"         own record {own}gcd s{own_sid} \"{own_name}\" (issue {gid}) COLLECTS: "
                         + " · ".join(f"gcd={g} {nm} {_ranges(rg)}" for g, nm, rg in runs)
                         + ("  ⚠ the stored GCD row looks like another book — these are ITS contents, not probed"
                            if stamped else ""))
    return L


def _ranges(rg):
    return ", ".join(f"#{idbase.fmt_num(a)}" if a == b else f"#{idbase.fmt_num(a)}-{idbase.fmt_num(b)}"
                     for a, b in rg) if rg else "(unnumbered)"


RX_BRACKETS = re.compile(r"\([^)]*\)|\[[^\]]*\]")
RX_YEAR = re.compile(r"\((?:[^)]*?\b)?((?:19|20)\d{2})\b[^)]*\)")


def core_of(fn):
    """A filename minus extension, bracketed tags (year, ripper, `(digital)`) and digits — what is left is the
    words, and two files with the same words in one group differ only in number / year / ripper."""
    s = RX_BRACKETS.sub(" ", RX_EXT.sub("", fn or ""))
    return " ".join(re.sub(r"\d+", " ", s).split()).lower()


def year_of(fn):
    m = RX_YEAR.search(fn or "")
    return m.group(1) if m else ""


def _wrap(toks, lead, width=200):
    out, line = [], lead
    for t in toks:
        if len(line) + len(t) + 3 > width and line.strip():
            out.append(line.rstrip(" ·"))
            line = lead
        line += t + " · "
    out.append(line.rstrip(" ·"))
    return out


# ── the census ────────────────────────────────────────────────────────────────────────────────────
def census_chunk(ev, pop, cursor, limit):
    """One bounded chunk: the flagged shelves with id > cursor, `limit` of them, each rendered once."""
    ids = sorted(s for s in pop if s > cursor)[:limit]
    rows, counts = [], Counter()
    for sid in ids:
        n = len(packet(sid, ev, pop[sid]))
        files = ev.size.get(sid, 0)
        rows.append((sid, files, n))
        counts["winning R"] += pop[sid]["kind"] == "R"
        counts["winning S"] += pop[sid]["kind"] == "S"
        counts[f"packet > {SOLO_LINES} lines (solo batch)"] += n + 1 > SOLO_LINES
    remaining = sum(1 for s in pop if s > (ids[-1] if ids else cursor))
    return rows, counts, (ids[-1] if ids else None), remaining


def estimate_batches(rows, target=1400, cap=40):
    """How many P- batches the census implies, packed the way next_batch.py --splits packs them (solo over
    SOLO_LINES, else ~target lines and at most `cap` shelves). Folder order is ignored — an estimate."""
    n, total, k = 0, 0, 0
    for _sid, _files, lines in rows:
        if lines + 1 > SOLO_LINES:
            n += 1
            continue
        if k and (total + lines + 1 > target or k >= cap):
            n += 1
            total, k = 0, 0
        total += lines + 1
        k += 1
    return n + (1 if k else 0)


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = sys.argv[1:]
    if "--census" not in a:
        raise SystemExit(__doc__)

    def opt(name, default):
        return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else default

    ev = idbase.Evidence()
    pop = population(ev)
    cursor, limit, show = int(opt("--cursor", 0)), int(opt("--limit", 100)), int(opt("--show", 10))
    allrows, total = [], Counter()
    while True:
        rows, counts, nxt, remaining = census_chunk(ev, pop, cursor, limit)
        allrows += rows
        total.update(counts)
        print({"processed": len(rows), "remaining": remaining, "nextCursor": nxt, "counts": dict(counts)})
        if "--all" not in a or nxt is None or remaining == 0:
            break
        cursor = nxt
    if not allrows:
        return
    files = sorted(r[1] for r in allrows)
    buckets = Counter()
    for f in files:
        buckets["1-5" if f <= 5 else "6-10" if f <= 10 else "11-20" if f <= 20 else "21-50" if f <= 50
                else "51-100" if f <= 100 else "101-300" if f <= 300 else "301+"] += 1
    print(f"\nshelves: {len(allrows)} (of {len(pop)} flagged)  totals: {dict(total)}")
    print("files per shelf: " + " · ".join(f"{b}: {buckets[b]}" for b in
                                           ("1-5", "6-10", "11-20", "21-50", "51-100", "101-300", "301+")))
    print(f"files: total {sum(files):,}, median {files[len(files) // 2]}, max {files[-1]}")
    lines = sorted(r[2] for r in allrows)
    print(f"packet lines: total {sum(lines):,}, median {lines[len(lines) // 2]}, max {lines[-1]}")
    print(f"P- batches (estimate, 1,400 lines / 40 shelves, solo > {SOLO_LINES}): {estimate_batches(allrows)}")
    for sid, f, n in sorted(allrows, key=lambda r: -r[2])[:show]:
        print(f"   S{sid:<7} {f:>5} files  {n:>5} packet lines  {ev.series[sid]['name'][:60]}")


if __name__ == "__main__":
    main()
