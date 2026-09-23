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


def packet(sid, ev, rec, landing=None):
    """One shelf's split packet: the winning decision (R/S + the split-needed F + every other F + the N lines on
    the shelf or its books), the shelves the F line names, the keys the files carry, and every item grouped by
    title x folder with its numbers — the same grouping `propose_split.py` prints.

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
    L.append("   keys now: " + " · ".join(f'"{k or "(none)"}" x{n}' for k, n in keys.most_common()))

    # per-file provider links, per item — v1's evidence, printed per group so a group's run is visible
    cv, gcd = {}, {}
    for iid, prov, sec in con.execute("""
            SELECT l.ItemId, l.Provider, l.SecondaryKey FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
            WHERE i.SeriesId = ? AND l.Status IN (1, 5) AND l.Provider IN (0, 3)""", (sid,)):
        if sec and str(sec).strip().isdigit():
            (cv if prov == idbase.P_CV else gcd)[iid] = int(sec)

    groups = defaultdict(list)
    for r in rows:
        groups[(title_of(r[1]), short_path(os.path.dirname(r[2] or "")))].append(r)
    order = sorted(groups, key=lambda g: (g[1], g[0]))
    L.append(f"   groups (title x folder): {len(order)}")
    modal = keys.most_common(1)[0][0] if keys else ""
    for k, g in enumerate(order, 1):
        rs = groups[g]
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
    return L


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
