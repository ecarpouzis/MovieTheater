"""The contract for a split decision (TOOLS_TODO 27 + 28): `decisions/P-NNN.jsonl` checked before `books-series-split`.

`python check_splits.py <P-001> [more...] [--all] [--unlanded]`   (no names: every decisions/P-*.jsonl, --all implied)
`python check_splits.py --project <P-001> --out <file>`  the verb's input: the item lines as {itemId, key} only,
                                                         group / range lines EXPANDED into item lines
`python check_splits.py --landed <P-001> [...] [--out sheet.tsv]`   after landing: which shelf each key became,
                                                         and the kept half each split left behind
`python check_splits.py --walkback <undo.csv> --out <file>`         the verb's undo CSV as its input, reversed

Why this exists. `books-series-split` is supervised by design — it rewrites `ComicDetail.ParsedSeriesKey` for
exactly the lines it is given and proposes nothing — so the only thing between a reader's typo and a merged
shelf is this file. A split line is small and its failure is large: a key that normalizes onto an unrelated
shelf's key MERGES the moved items into that shelf at the next resolve (the verb reuses an existing canonical
key instead of creating a row; `SeriesResolver` then maps the key there by its normalized spelling), and a key
spelled like the item's own current key moves nothing while reporting success. `splitbase.Landing` replays the
resolver's three lookups so those cases are caught here, not discovered in the wave's audits.

The file (one JSON object per line):
    {"shelf": 9845, "split": true, "why": "<≥ 40 chars: which runs, which records, which items stay>"}
    {"itemId": 107776, "key": "Jim Butcher's The Dresden Files - Storm Front v2 (2009)", "run": {"cv": 27202, "gcd": [55645, 52657]}}
    {"group": "G6", "files": 66, "key": "X-Men (1963)", "run": {"cv": 2133, "gcd": 1576}}
    {"range": "#1-141", "match": "Uncanny X-Men", "folder": "Complete Chronology", "except": [117639], "key": …}
One `shelf` line per shelf in the batch's `.ids` — the coverage contract, as `S`/`R` is for a tier batch;
`"split": false` says the shelf should NOT be split (the flag was wrong, or a dominant run with residue) and
then no item of it may appear. An item line is written only for an item that MOVES; items that stay are
omitted. `run` is optional and names the new run's ids where known (legs as on a `C` line: cv, gcd, mu, barney,
inducks, marvel; a value may be a list when a leg splits the run, as GCD does Storm Front Vol. 2 by publisher)
— it seeds the new shelf's `S` line in its identity batch, and the verb never reads it.

A shelf line may carry `"join": [sid, …]` (TOOLS_TODO 28b): a LEAD-APPROVED join the F line does not name —
a key may then land on those shelves too, and the missed-join WARN is silenced for them.

A shelf line may carry `"pending_join": [sid, …]` (TOOLS_TODO 29): what STAYS on this shelf stays only because
it cannot move without a join the lead has not approved (P-002's Cosplayers: Perfect Collection stays on S34939
while its run's shelf is S102444). It moves nothing and approves nothing; each sid must be a live comic shelf
other than this one. `--landed` carries it onto the kept half's sheet row (`pending_join=<sid>,…`), and
`next_batch.py --revisit-file` prints it in that shelf's R packet as `merge-with candidate: S<sid>`.

Group and range lines (TOOLS_TODO 28c) move many items in one line, so a 900-file shelf is not hand-typed:
  • `group`: `G<n>` exactly as the packet numbers it (splitbase.group_items — the packet's own function).
    `files` (optional, recommended) must equal the group's size, so a group that drifted since the packet refuses.
  • `range`: `#a-b` or `#a` over the shelf's NUMBERED issue files (collections never match a range); `match`
    (required) = words every filename must carry; `folder` (optional) = words the folder must carry; `except`
    (optional) = item ids to leave out, each of which must be matched. Two matched files with one number is
    AMBIGUOUS and refused (two runs' #5) unless `"dupes": true` says they are rips of one issue.
  • `from`: the source shelf; required when the batch holds more than one shelf (a bare `G3` is ambiguous).
A line that matches nothing, or an item named by two lines, is refused. `--project` writes the expansion.

`books-series-split` reads only `itemId` and `key` and would count a `shelf` line as a bad line, so the landing
feeds it `--project`'s output, never the P- file itself.

Rules, each a failure with its own message:
  • the line is a JSON object with only the fields above; `itemId` an int; `key` a trimmed, non-empty string
    (<= 200 chars, no control characters) that differs from the item's CURRENT ParsedSeriesKey
  • every item exists (a live comic file with a ComicDetail row) and sits on a shelf of this batch whose shelf
    line says `split: true`, and whose WINNING decision carries `F split-needed`
  • no item twice (in the file; with --all, across every P- file)
  • every key is NEW, or lands on a live shelf the source shelf's winning `F split-needed` line NAMES (`S<id>`)
    or its shelf line `join`s — landing back on the source shelf, or on any other live shelf, is refused
  • two different keys that normalize to one canonical key are refused (the verb would make them one row)
  • every shelf keeps at least one item; a `split: true` shelf moves at least one
  • a `run` names only known legs with real ids, and every line of one key states the same `run`
A file whose every move already carries its key is LANDED and is checked as landed instead (each item left the
shelf it was split from — the undo CSV says which); half-landed is a failure; `--unlanded` (split_land.ps1)
refuses a landed file outright.
And a WARN (not a failure): a `run` cv/gcd id that is already the winning `S` identity of another live shelf
the F line does not name and no `join` approves — a probable missed join (Elric S6101 -> S6105).
"""
import json
import os
import re
import sys
from collections import Counter, defaultdict

import idbase
import splitbase

FIELDS_ITEM = {"itemId", "key", "run"}
FIELDS_SHELF = {"shelf", "split", "why", "join", "pending_join"}
FIELDS_GROUP = {"group", "from", "files", "key", "run"}
FIELDS_RANGE = {"range", "match", "folder", "except", "dupes", "from", "key", "run"}
MIN_WHY = 40
MAX_KEY = 200
RX_CTRL = re.compile(r"[\x00-\x1f\x7f]")
RX_P = re.compile(r"^P-(\d+)$")
RX_GROUP = re.compile(r"^G(\d{1,4})$")
RX_RANGE = re.compile(r"^#(-?\d{1,5}(?:\.\d+)?)(?:-(\d{1,5}(?:\.\d+)?))?$")


def resolve_split_file(arg):
    """A bare `P-001`, a name with `.jsonl`, or any path — the same search idbase does for `.txt` decisions.
    batches/ is not searched: `P-001.txt` there is the PACKET."""
    cands = [arg, arg + ".jsonl",
             os.path.join(idbase.DECISIONS, arg), os.path.join(idbase.DECISIONS, arg + ".jsonl"),
             os.path.join(idbase.ROOT, arg), os.path.join(idbase.ROOT, arg + ".jsonl")]
    for c in cands:
        if os.path.isfile(c) and c.endswith(".jsonl"):
            return os.path.abspath(c)
    raise SystemExit(f"no such split file: {arg!r}\n  tried:\n    " + "\n    ".join(cands))


def ids_path(path):
    base = os.path.splitext(os.path.basename(path))[0]
    here = os.path.join(os.path.dirname(os.path.abspath(path)), base + ".ids")
    return here if os.path.exists(here) else os.path.join(idbase.BATCHES, base + ".ids")


def read_ids(path, errors):
    idp = ids_path(path)
    if not os.path.exists(idp):
        errors.append(f"no .ids for {os.path.basename(path)} (looked beside it and in batches/) — nothing to be "
                      f"complete against")
        return set()
    return {int(x.strip().lstrip("S")) for x in open(idp, encoding="utf-8") if x.strip().lstrip("S").isdigit()}


def read_lines(path, errors):
    """-> [(lineNo, obj)] of the parseable lines; a line that is not a JSON object is a failure."""
    out = []
    for k, raw in enumerate(open(path, encoding="utf-8"), 1):
        t = raw.strip()
        if not t:
            continue
        try:
            o = json.loads(t)
        except ValueError as e:
            errors.append(f"line {k}: not JSON ({e})")
            continue
        if not isinstance(o, dict):
            errors.append(f"line {k}: not a JSON object")
            continue
        out.append((k, o))
    return out


def _words(s):
    return splitbase.normalize_key(s).split()


def _int(x):
    return isinstance(x, int) and not isinstance(x, bool)


def landed_rows(path):
    """{(source shelf, new key): [itemId, ...]} from the verb's newest undo CSV for this P- file, or None when the
    file never went through `books-series-split`. A LANDED file's group / range lines cannot be re-expanded against
    the live shelf — the items they named have left it and its groups renumbered (P-003's `G2` of S726, P-004's 249
    failures after wave 20) — so they are expanded from the landing's own record instead."""
    base = os.path.splitext(os.path.basename(path))[0]
    csvs = sorted(f for f in os.listdir(idbase.UNDO)
                  if f.startswith(f"split-{base}-") and f.endswith(".csv")) if os.path.isdir(idbase.UNDO) else []
    if not csvs:
        return None
    import csv
    out = defaultdict(list)
    with open(os.path.join(idbase.UNDO, csvs[-1]), encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            if row.get("SeriesIdAtSplit", "").isdigit() and row.get("ItemId", "").isdigit():
                out[(int(row["SeriesIdAtSplit"]), row["NewParsedSeriesKey"])].append(int(row["ItemId"]))
    return out


def expand_moves(con, lines, batch, errors, rows_cache=None, landed=None):
    """Every MOVE the file states, in file order: item lines as written, group / range lines expanded into item
    lines (TOOLS_TODO 28c). -> [(loc, {"itemId", "key", "run"?})]. Deterministic: the rows are
    `splitbase.shelf_items` order and the groups `splitbase.group_items` — the packet's own functions.
    A spec that is ambiguous or matches nothing is an error and expands to nothing.
    `landed` (landed_rows): the file has been through the verb, so a group / range line takes the items its
    (source, key) moved per the undo CSV, less the ones item lines state — the first such line claims them all."""
    rows_cache = {} if rows_cache is None else rows_cache
    explicit = {o["itemId"] for _k, o in lines if "itemId" in o and _int(o.get("itemId"))}
    claimed = set()

    def rows_of(sid):
        if sid not in rows_cache:
            rows_cache[sid] = splitbase.shelf_items(con, sid)
        return rows_cache[sid]

    out = []
    for k, o in lines:
        if "shelf" in o:
            continue
        if "itemId" in o:
            extra = set(o) - FIELDS_ITEM
            if extra:
                errors.append(f"line {k}: unknown field(s) {sorted(extra)} on an item line (fields: itemId, key, run)")
            out.append((f"line {k}", o))
            continue
        kind = "group" if "group" in o else "range" if "range" in o else None
        if kind is None:
            errors.append(f"line {k}: neither a shelf line, an item line, a group line nor a range line")
            continue
        spec = o[kind]
        loc = f"line {k} ({kind} {spec!r})"
        extra = set(o) - (FIELDS_GROUP if kind == "group" else FIELDS_RANGE)
        if extra:
            errors.append(f"{loc}: unknown field(s) {sorted(extra)} on a {kind} line "
                          f"(fields: {', '.join(sorted(FIELDS_GROUP if kind == 'group' else FIELDS_RANGE))})")
            continue
        if "key" not in o:
            errors.append(f"{loc}: a {kind} line needs a `key`")
            continue
        src = o.get("from")
        if src is None:
            if len(batch) != 1:
                errors.append(f"{loc}: names no source shelf and the batch holds {len(batch)} shelves — "
                              f"ambiguous; add \"from\": <shelf id>")
                continue
            src = next(iter(batch))
        elif not _int(src) or src not in batch:
            errors.append(f"{loc}: `from` {src!r} is not a shelf of this batch")
            continue
        if landed is not None and (src, o["key"]) in landed:
            got = [i for i in landed[(src, o["key"])] if i not in explicit and i not in claimed]
            claimed.update(got)
            for iid in got:
                item = {"itemId": iid, "key": o["key"]}
                if "run" in o:
                    item["run"] = o["run"]
                out.append((loc, item))
            continue
        rows = rows_of(src)
        if kind == "group":
            m = RX_GROUP.match(spec) if isinstance(spec, str) else None
            order = splitbase.group_items(rows)
            if not m or not 1 <= int(m.group(1)) <= len(order):
                errors.append(f"{loc}: S{src}'s packet has groups G1-G{len(order)}; {spec!r} is none of them")
                continue
            (title, folder), rs = order[int(m.group(1)) - 1]
            if "files" in o and o["files"] != len(rs):
                errors.append(f"{loc}: says files={o['files']!r} but G{m.group(1)} of S{src} (\"{title}\" in "
                              f"{folder or '(root)'}) holds {len(rs)} today — the shelf changed since the packet")
                continue
            got = [r[0] for r in rs]
        else:
            m = RX_RANGE.match(spec) if isinstance(spec, str) else None
            match = o.get("match")
            if not m:
                errors.append(f"{loc}: a range is `#a-b` or `#a`")
                continue
            a = float(m.group(1))
            b = float(m.group(2)) if m.group(2) else a
            if b < a:
                errors.append(f"{loc}: the range runs backwards")
                continue
            if not isinstance(match, str) or not _words(match):
                errors.append(f"{loc}: a range needs `match` — the filename words every moved file carries")
                continue
            if "folder" in o and (not isinstance(o["folder"], str) or not _words(o["folder"])):
                errors.append(f"{loc}: `folder` must be words the folder path carries")
                continue
            exc = o.get("except", [])
            if not isinstance(exc, list) or not all(_int(x) for x in exc):
                errors.append(f"{loc}: `except` must be a list of item ids")
                continue
            if "dupes" in o and not isinstance(o["dupes"], bool):
                errors.append(f"{loc}: `dupes` must be true or false")
                continue
            mw, fw = set(_words(match)), set(_words(o.get("folder") or ""))
            cand = []
            for r in rows:
                x = idbase.num(r[5])
                if r[4] or x is None or not a <= x <= b:
                    continue
                if not mw <= set(_words(splitbase.RX_EXT.sub("", r[1] or ""))):
                    continue
                if fw and not fw <= set(_words(os.path.dirname(r[2] or ""))):
                    continue
                cand.append((r[0], x))
            ids = {i for i, _x in cand}
            stray = [x for x in exc if x not in ids]
            if stray:
                errors.append(f"{loc}: `except` names {stray}, which the range does not match — a typo "
                              f"would otherwise go unnoticed")
                continue
            cand = [(i, x) for i, x in cand if i not in set(exc)]
            if not cand:
                errors.append(f"{loc}: matches no numbered issue file on S{src}")
                continue
            per = defaultdict(list)
            for i, x in cand:
                per[x].append(i)
            twice = {x: v for x, v in per.items() if len(v) > 1}
            if twice and o.get("dupes") is not True:
                x0 = sorted(twice)[0]
                errors.append(f"{loc}: AMBIGUOUS — {len(twice)} number(s) match two or more files "
                              f"(#{idbase.fmt_num(x0)}: items {twice[x0]}); narrow it with `folder` / `except`, "
                              f"or say \"dupes\": true if they are rips of one issue")
                continue
            got = [i for i, _x in cand]
        for iid in got:
            item = {"itemId": iid, "key": o["key"]}
            if "run" in o:
                item["run"] = o["run"]
            out.append((loc, item))
    return out


class Ctx:
    """Everything the rules read, loaded once per run."""

    def __init__(self):
        self.ev = ev = idbase.Evidence()
        self.decides, self.winner, _s, _d = idbase.scan_decisions()
        self.pop = splitbase.population(ev, self.decides, self.winner)
        self.landing = splitbase.Landing(ev.con, ev.shelf_set)
        self.near = splitbase.NearIndex(ev, self.decides, self.winner)
        self.item = {}
        for iid, sid, key in ev.con.execute("""
                SELECT i.Id, i.SeriesId, cd.ParsedSeriesKey FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0"""):
            self.item[iid] = (sid, key or "")
        self.rows_cache = {}
        self._ck = None
        self._named = {}
        self._split_new = None

    def rejoined_via(self, key, now):
        """The new shelf a landed split made for `key` (split_land.ps1's `undo/split-wave*-newshelves.tsv`), when
        landed merges carry it into `now` — else None."""
        if self._split_new is None:
            self._split_new = {}
            if os.path.isdir(idbase.UNDO):
                for f in sorted(os.listdir(idbase.UNDO)):
                    if f.startswith("split-wave") and f.endswith("-newshelves.tsv"):
                        for raw in open(os.path.join(idbase.UNDO, f), encoding="utf-8"):
                            col = raw.rstrip("\r\n").split("\t")
                            if len(col) > 4 and col[4] == "new" and col[0].lstrip("S").isdigit():
                                self._split_new[col[1]] = int(col[0].lstrip("S"))
        s = self._split_new.get(key)
        first, seen = s, set()
        while s is not None and s != now and s in self.near.merged and s not in seen:
            seen.add(s)
            s = self.near.merged[s]
        return first if (s == now and first != now) else None

    def checker(self):
        if self._ck is None:
            import check_identity
            self._ck = check_identity.Checker()
        return self._ck

    def named(self, sid):
        """The shelves the winning `F split-needed` line of `sid` names — the shelves a key may JOIN unasked."""
        if sid not in self._named:
            got = set()
            rec = self.pop.get(sid)
            if rec:
                for text in splitbase.decision_lines(rec["file"], sid)["split"]:
                    got |= {int(x) for x in splitbase.RX_SREF.findall(text)}
            got.discard(sid)
            self._named[sid] = got
        return self._named[sid]

    def run_ok(self, leg, v):
        ck = self.checker()
        if leg == "cv":
            return ck.cv_volume_exists(v)
        if leg == "gcd":
            return ck.gcd_series_exists(v)
        if leg == "mu":
            return ck.mu_series_exists(v)
        return True        # barney / inducks / marvel keys are checked at C-line landing, not here


RX_ITEM_REF = re.compile(r"item \d+")


def _collapse(msgs, start):
    """A group line of 400 items that breaks one rule is ONE finding: messages of one line that differ only in
    the item id are folded into the first, with the count."""
    seen, out = {}, []
    for m in msgs[start:]:
        k = RX_ITEM_REF.sub("item …", m, count=1)
        if k in seen:
            seen[k][1] += 1
            continue
        seen[k] = [len(out), 0]
        out.append(m)
    for i, n in seen.values():
        if n:
            out[i] += f"  (+{n} more item(s) of this line, same finding)"
    msgs[start:] = out


def check_file(path, cx, errors, seen_items=None, seen_norm=None, warnings=None):
    """Every rule on one file. -> summary dict. `seen_items` / `seen_norm` carry state across files (--all)."""
    start = len(errors)
    try:
        return _check_file(path, cx, errors, seen_items, seen_norm, warnings)
    finally:
        _collapse(errors, start)


def _check_file(path, cx, errors, seen_items=None, seen_norm=None, warnings=None):
    base = os.path.basename(path)
    ev = cx.ev
    warnings = [] if warnings is None else warnings
    batch = read_ids(path, errors)
    idname = os.path.basename(ids_path(path))
    lines = read_lines(path, errors)
    shelf_line, joins, pj_errors = {}, {}, []
    for k, o in lines:
        if "shelf" not in o:
            continue
        extra = set(o) - FIELDS_SHELF
        if extra:
            errors.append(f"line {k}: unknown field(s) {sorted(extra)} on a shelf line")
        sid = o.get("shelf")
        if not _int(sid):
            errors.append(f"line {k}: `shelf` must be an integer shelf id")
            continue
        if sid in shelf_line:
            errors.append(f"line {k}: S{sid} has two shelf lines (line {shelf_line[sid][0]} too)")
            continue
        if sid not in batch:
            errors.append(f"line {k}: S{sid} is not in {idname}")
        if not isinstance(o.get("split"), bool):
            errors.append(f"line {k}: S{sid} `split` must be true or false")
        why = o.get("why")
        if not isinstance(why, str) or len(why.strip()) < MIN_WHY:
            errors.append(f"line {k}: S{sid} `why` must say which runs and which records (>= {MIN_WHY} chars)")
        if "join" in o:
            j = o["join"]
            if not isinstance(j, list) or not j or not all(_int(x) for x in j):
                errors.append(f"line {k}: S{sid} `join` must be a non-empty list of shelf ids")
            else:
                bad = [x for x in j if x == sid or x not in ev.series]
                if bad:
                    errors.append(f"line {k}: S{sid} `join` names {bad} — not a live comic shelf other than itself")
                joins[sid] = {x for x in j if x != sid}
        if "pending_join" in o:
            # TOOLS_TODO 29: a note for the NEXT identity batch, not a move — so it is checked for being a real,
            # live, other shelf and nothing else (a stale id would print a merge-with prompt at a dead shelf)
            pj = o["pending_join"]
            if not isinstance(pj, list) or not pj or not all(_int(x) for x in pj):
                errors.append(f"line {k}: S{sid} `pending_join` must be a non-empty list of shelf ids")
            else:
                bad = [x for x in pj if x == sid or x not in ev.series]
                if bad:
                    # held back until the file is known not to be LANDED: after the landing the kept half or
                    # the target may have merged, and a landed file's note is history, not a defect
                    pj_errors.append(f"line {k}: S{sid} `pending_join` names {bad} — not a live comic shelf "
                                     f"other than itself")
        shelf_line[sid] = (k, o)
    moves = expand_moves(cx.ev.con, lines, batch, errors, cx.rows_cache, landed_rows(path))

    for sid in sorted(batch - set(shelf_line)):
        errors.append(f"S{sid} is in the batch and has no shelf line — every shelf is answered, split or not")

    # A LANDED file (every move's item already carries its key) is checked as landed: the pre-landing rules
    # (items on the batch's shelves, a key that differs from the current one) are false of it by construction.
    # What must hold instead: each item left the shelf it was split from. Half-landed is its own failure.
    done = [(lk, o) for lk, o in moves if _int(o.get("itemId")) and o["itemId"] in cx.item
            and cx.item[o["itemId"]][1] == o.get("key")]
    if moves and len(done) == len(moves):
        src_of = key_sources(path)
        rejoined = set()
        for lk, o in moves:
            now = cx.item[o["itemId"]][0]
            if now in src_of.get(o["key"], ()):
                # ...unless the NEW shelf the landing made for this key was merged back into its source by a
                # later identity decision (wave 17: R-031's `F 102501 merge-with=3171` returned P-002's
                # "Brilliant (2022)" to S3171 — a reprint of that run after all). The split's own sheet names the
                # new shelf, and SeriesMerge records where it went; that is a decision taking effect, not a
                # split that never resolved.
                back = cx.rejoined_via(o["key"], now)
                if back:
                    if (o["key"], now) not in rejoined:
                        rejoined.add((o["key"], now))
                        warnings.append(f"{lk}: item {o['itemId']} is back on S{now} — its split shelf S{back} was "
                                        f"merged into S{now} by a later decision (history, not a failure)")
                    continue
                errors.append(f"{lk}: item {o['itemId']} carries its new key but still sits on S{now}, the shelf "
                              f"it was split from — run books-resolve --series")
        return {"shelves": len(shelf_line), "split": sum(1 for _k, o in shelf_line.values() if o.get("split") is True),
                "kept": sum(1 for _k, o in shelf_line.values() if o.get("split") is False),
                "items": len(moves), "keys": len({o["key"] for _lk, o in moves}), "joins": 0, "landed": True}
    errors.extend(pj_errors)
    if done:
        errors.append(f"PARTIALLY LANDED: {len(done)} of {len(moves)} moves already carry their new key "
                      f"(first: {done[0][0]}, item {done[0][1]['itemId']}) — finish the landing or walk it back")

    moved_from = Counter()
    key_run, key_src, in_file = {}, defaultdict(set), {}
    landed_on, warned = defaultdict(set), set()
    for lk, o in moves:
        iid, key = o.get("itemId"), o.get("key")
        loc = f"{lk}: item {iid}"
        if not _int(iid):
            errors.append(f"{lk}: `itemId` must be an integer")
            continue
        if not isinstance(key, str) or not key.strip():
            errors.append(f"{loc}: `key` must be a non-empty string")
            continue
        if key != key.strip() or RX_CTRL.search(key) or len(key) > MAX_KEY:
            errors.append(f"{loc}: key {key!r} must be trimmed, <= {MAX_KEY} chars, with no control characters "
                          f"(the verb trims it, so the key checked here would not be the key written)")
            continue
        if iid in in_file:
            errors.append(f"{loc}: moved twice in this file (also by {in_file[iid]})")
            continue
        in_file[iid] = lk
        if seen_items is not None:
            if iid in seen_items and seen_items[iid] != base:
                errors.append(f"{loc}: also moved in {seen_items[iid]} — one item, one split decision")
            seen_items.setdefault(iid, base)
        got = cx.item.get(iid)
        if got is None:
            errors.append(f"{loc}: no such live comic file with a ComicDetail row")
            continue
        src, now = got
        if src not in batch:
            errors.append(f"{loc}: sits on S{src}, which is not a shelf of this batch")
            continue
        sl = shelf_line.get(src)
        if sl and sl[1].get("split") is False:
            errors.append(f"{loc}: S{src}'s shelf line says split=false, so none of its items may move")
        if src not in cx.pop:
            errors.append(f"{loc}: S{src}'s winning decision no longer carries F split-needed")
        if key == now:
            errors.append(f"{loc}: key {key!r} is the item's CURRENT key — it would not move (omit items that stay)")
            continue
        allowed = cx.named(src) | joins.get(src, set())
        how, hit = cx.landing.land(key)
        live = cx.landing.live(hit)
        if src in live:
            errors.append(f"{loc}: key {key!r} resolves ({how}) back to S{src} itself — the item would not move")
        else:
            stray = live - allowed
            if stray:
                s0 = sorted(stray)[0]
                errors.append(f"{loc}: key {key!r} lands ({how}) on live shelf S{s0} "
                              f"'{cx.landing.name.get(s0, '?')}', which S{src}'s F split-needed line does not name "
                              f"and no `join` approves — the resolve would MERGE the item into it; pick a key of "
                              f"its own")
            landed_on[src] |= live
        moved_from[src] += 1
        key_src[key].add(src)
        run = o.get("run")
        if run is not None:
            if not isinstance(run, dict) or not run:
                errors.append(f"{loc}: `run` must be an object like {{\"cv\": 27202, \"gcd\": 35902}}")
            else:
                for leg, v in run.items():
                    if leg not in idbase.RUN_LEGS:
                        errors.append(f"{loc}: unknown leg '{leg}' in run (expected {', '.join(sorted(idbase.RUN_LEGS))})")
                        continue
                    for x in (v if isinstance(v, list) else [v]):
                        if leg in ("cv", "gcd", "mu") and not _int(x):
                            errors.append(f"{loc}: run {leg}={x!r} must be an integer id")
                        elif leg in ("cv", "gcd", "mu") and not cx.run_ok(leg, x):
                            errors.append(f"{loc}: run {leg}={x} is not in our tables or the local catalogues")
                        elif leg in ("cv", "gcd"):
                            # TOOLS_TODO 28b: the run's id is another live shelf's S identity -> a missed join?
                            for owner in sorted(cx.near.s_ids.get((leg, x), set()) - {src} - allowed - live):
                                if (src, key, owner) in warned:
                                    continue
                                warned.add((src, key, owner))
                                okeys = sorted(ev.keys.get(owner, ()))
                                warnings.append(
                                    f"{loc}: run {leg}={x} is already the S identity of live shelf S{owner} "
                                    f"'{ev.series.get(owner, {}).get('name', '?')}' ({cx.near.s_line(owner)}), which "
                                    f"S{src}'s F line does not name — a probable MISSED JOIN. Join it with its key "
                                    f"({' | '.join(repr(x2) for x2 in okeys[:3]) or 'none'}) and, lead-approved, "
                                    f"\"join\": [{owner}] on the shelf line; or say in `why` why it is another run")
                canon = json.dumps(run, sort_keys=True)
                if key in key_run and key_run[key][0] != canon:
                    errors.append(f"{loc}: key {key!r} names run {canon} here and {key_run[key][0]} at "
                                  f"{key_run[key][1]} — one key is one run")
                key_run.setdefault(key, (canon, lk))

    for sid, js in joins.items():
        idle = sorted(js - landed_on.get(sid, set()))
        if idle:
            warnings.append(f"S{sid}: `join` names {idle} but no moved item's key lands there — use the joined "
                            f"shelf's key, or drop the join")

    norm = defaultdict(set)
    for key in key_src:
        norm[splitbase.normalize_key(key)].add(key)
        if seen_norm is not None:
            seen_norm[splitbase.normalize_key(key)].add((key, base))
    for n, ks in norm.items():
        if len(ks) > 1:
            errors.append(f"keys {sorted(ks)} normalize to one canonical key 'parsed:{n}' — the verb makes them ONE "
                          f"Series row; spell them identically or tell them apart")

    for sid, (k, o) in shelf_line.items():
        if o.get("split") is True and not moved_from.get(sid):
            errors.append(f"line {k}: S{sid} says split=true and moves no item")
        if moved_from.get(sid) and moved_from[sid] >= ev.size.get(sid, 0):
            errors.append(f"S{sid}: all {ev.size.get(sid, 0)} items move — a split keeps the run that stays on "
                          f"the shelf (leave the largest run's items out of the file)")
    return {"shelves": len(shelf_line), "split": sum(1 for _k, o in shelf_line.values() if o.get("split") is True),
            "kept": sum(1 for _k, o in shelf_line.values() if o.get("split") is False),
            "items": len(in_file), "keys": len(key_src), "joins": sum(len(v) for v in joins.values())}


def project(path, out):
    """The verb's input: `{itemId, key}` per MOVE, in file order, group / range lines expanded (TOOLS_TODO 28c).
    The P- file is a superset the verb cannot read whole (a shelf line has no itemId and would be counted as a
    bad line). An expansion error writes NOTHING and exits 1 — a half-written verb input is a half split."""
    errors = []
    batch = read_ids(path, errors)
    moves = expand_moves(idbase.open_hot(), read_lines(path, errors), batch, errors)
    if errors:
        for e in errors:
            print(f"   {e}")
        print(f"{len(errors)} error(s) — nothing written to {out}")
        return 1
    with open(out, "w", encoding="utf-8") as f:
        for _loc, o in moves:
            f.write(json.dumps({"itemId": o["itemId"], "key": o["key"]}, ensure_ascii=False) + "\n")
    print(f"{len(moves)} item line(s) -> {out}")
    return 0


def walkback(csv_path, out):
    """The verb's own undo CSV turned back into its input: `{itemId, key: PreviousParsedSeriesKey}` per row, LAST
    row per item winning the other way round (the earliest previous key is the original). A row whose previous
    key was empty cannot be restored by the verb (it refuses an empty key) and is counted, not written."""
    import csv
    first = {}
    with open(csv_path, encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            iid = int(row["ItemId"])
            first.setdefault(iid, row["PreviousParsedSeriesKey"])
    n, empty = 0, 0
    with open(out, "w", encoding="utf-8") as f:
        for iid, key in first.items():
            if not key:
                empty += 1
                continue
            f.write(json.dumps({"itemId": iid, "key": key}, ensure_ascii=False) + "\n")
            n += 1
    print(f"{n} item(s) -> {out}" + (f"; {empty} had no previous key and need a hand fix" if empty else ""))
    return 1 if empty else 0


def key_sources(path):
    """{key: {source shelf}} for one P- file — which shelf each key's items LEFT. The verb's undo CSV
    (`undo/split-<P-NNN>-<stamp>.csv`, the newest) records `SeriesIdAtSplit` per item, which answers it for item,
    group and range lines alike; with no CSV, the file order stands in (a move belongs to the shelf line above it,
    a group / range line to its `from`)."""
    base = os.path.splitext(os.path.basename(path))[0]
    csvs = sorted(f for f in os.listdir(idbase.UNDO)
                  if f.startswith(f"split-{base}-") and f.endswith(".csv")) if os.path.isdir(idbase.UNDO) else []
    out = defaultdict(set)
    if csvs:
        import csv
        with open(os.path.join(idbase.UNDO, csvs[-1]), encoding="utf-8", newline="") as f:
            for row in csv.DictReader(f):
                if row.get("SeriesIdAtSplit", "").isdigit():
                    out[row["NewParsedSeriesKey"]].add(int(row["SeriesIdAtSplit"]))
        return out
    cur = None
    for raw in open(path, encoding="utf-8"):
        t = raw.strip()
        if not t:
            continue
        o = json.loads(t)
        if "shelf" in o:
            cur = o["shelf"]
        elif "key" in o:
            src = o.get("from", cur)
            if src is not None:
                out[o["key"]].add(src)
    return out


def landed(paths, out=None):
    """After the landing: where each key now lives, AND the kept half each split left behind (TOOLS_TODO 28e —
    both halves need re-identifying). Read-only. The sheet's first column is `S<sid>`, which is what
    `next_batch.py --revisit-file` reads; its other columns (key, run=, batch, new|kept) become the packet's
    `split run:` / `split:` line, so the reader seeds the `S` line from the P- file without opening it.

    A key is read off every line that carries one (item, group and range lines alike). The kept half is the
    source shelf of each `split: true` line, still holding files; a source shelf holding none is reported."""
    con = idbase.open_hot()
    alias = dict(con.execute("SELECT ParsedKey, SeriesId FROM SeriesAlias"))
    live = {r[0] for r in con.execute(idbase.SHELF_SQL)}
    rows, kept = [], []
    merged = dict(con.execute("SELECT OldSeriesId, NewSeriesId FROM SeriesMerge WHERE NewSeriesId IS NOT NULL "
                              "ORDER BY MergedAt"))

    def now_of(s):
        """a shelf id followed through landed merges to the shelf that holds it today"""
        seen_ = set()
        while s not in live and s in merged and s not in seen_:
            seen_.add(s)
            s = merged[s]
        return s

    for p in paths:
        seen, split_src, pending = {}, [], {}
        b = os.path.basename(p)
        for raw in open(p, encoding="utf-8"):
            t = raw.strip()
            if not t:
                continue
            o = json.loads(t)
            if "shelf" in o:
                if o.get("split") is True:
                    split_src.append(o["shelf"])
                if isinstance(o.get("pending_join"), list) and o["pending_join"]:
                    # TOOLS_TODO 29: the kept half's merge-with prompt; a split:false shelf with one is listed
                    # too (as `kept`, nothing moved) — it is the whole shelf that waits on the join
                    pending[o["shelf"]] = [x for x in o["pending_join"] if isinstance(x, int)]
                    if o.get("split") is not True:
                        split_src.append(o["shelf"])
                continue
            if "key" not in o or o["key"] in seen:
                continue
            seen[o["key"]] = o.get("run")
        new_of = []
        for key, run in seen.items():
            sid = alias.get(key)
            if sid is None:
                sid = con.execute("SELECT Id FROM Series WHERE ParsedKey = ?", (key,)).fetchone()
                sid = sid[0] if sid else None
            n = con.execute("SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id "
                            "WHERE cd.ParsedSeriesKey = ? AND i.SeriesId = ?", (key, sid)).fetchone()[0] if sid else 0
            rows.append((sid, key, run, n, b))
            new_of.append((sid, key))
        src_of = key_sources(p)
        for src in split_src:
            n = con.execute("SELECT count(*) FROM Item i WHERE i.SeriesId = ? AND i.Kind = 0 "
                            "AND coalesce(i.IsExcluded,0) = 0", (src,)).fetchone()[0]
            gone = [f"S{s} \"{k}\"" if s else f"(no shelf) \"{k}\"" for s, k in new_of if src in src_of.get(k, ())]
            if not gone and src in pending:
                gone = ["nothing (split: false)"]
            pj = [now_of(x) for x in pending.get(src, [])]
            # TOOLS_TODO 33b: the CV ids of the runs that LEFT this shelf — a kept half still storing one of them
            # is the collision R-032 had to withhold 10 ids over
            mcv = [int(seen[k]["cv"]) for _s, k in new_of
                   if src in src_of.get(k, ()) and isinstance(seen[k], dict) and str(seen[k].get("cv", "")).isdigit()]
            kept.append((src if src in live and n else None, src, n, b, gone, list(dict.fromkeys(pj)),
                         list(dict.fromkeys(mcv))))
        # TOOLS_TODO 33a: each new key's PAIR — the shelf its items left (followed through merges), so
        # next_batch --revisit-file never hands one half of a split to one reader and the other half to another
        for i in range(len(rows) - len(new_of), len(rows)):
            sid, key, run, n, bb = rows[i]
            srcs = sorted(src_of.get(key, ()))
            rows[i] = (sid, key, run, n, bb, now_of(srcs[0]) if srcs else None)
    for sid, key, run, n, b, pair in rows:
        print(f"  {('S' + str(sid)) if sid else '(no shelf yet)':<9} {n:>4} item(s)  {key}  run={json.dumps(run) if run else '-'}  [{b}]"
              + (f"  pair S{pair}" if pair else ""))
    stored = {}
    if kept:
        ks = sorted({k[0] for k in kept if k[0]})
        if ks:
            stored = dict(con.execute(f"SELECT Id, CvVolumeId FROM Series WHERE Id IN ({','.join('?' * len(ks))})", ks))
    for sid, src, n, b, _g, pj, mcv in kept:
        clash = stored.get(sid) if sid and stored.get(sid) in mcv else None
        print(f"  {('S' + str(sid)) if sid else '(gone)':<9} {n:>4} item(s)  (kept half of S{src})  [{b}]"
              + (f"  pending join -> {', '.join('S' + str(x) for x in pj)}" if pj else "")
              + (f"  ⚠ stored cv {clash} = the moved run's" if clash else ""))
    missing = [r for r in rows if not r[0] or not r[3]]
    print(f"{len(rows)} key(s); {len(missing)} not yet on a shelf of their own (run books-resolve --series); "
          f"{len(kept)} kept half/halves, {sum(1 for k in kept if not k[0])} holding no files")
    if out:
        with open(out, "w", encoding="utf-8") as f:
            f.write("# shelves from the split lane (new + kept halves) — feed to next_batch.py --revisit-file\n")
            f.write("# S<sid>\t<key>\trun=<json|->\t<P- file>\tnew|kept[\tpair=<origin sid>][\tmoved_cv=<id>,…]"
                    "[\tpending_join=<sid>,…]\n")
            f.write("# pair = the shelf the split took this row's items from (a kept row is its own pair): "
                    "--revisit-file never cuts a batch inside a pair group\n")
            for sid, key, run, n, b, pair in rows:
                if sid:
                    f.write(f"S{sid}\t{key}\trun={json.dumps(run) if run else '-'}\t{b}\tnew"
                            + (f"\tpair={pair}" if pair else "") + "\n")
            for sid, src, n, b, gone, pj, mcv in kept:
                if sid:
                    f.write(f"S{sid}\tmoved out -> {'; '.join(gone) or '(unknown: no undo CSV)'}\trun=-\t{b}\tkept"
                            f"\tpair={src}"
                            + (f"\tmoved_cv={','.join(str(x) for x in mcv)}" if mcv else "")
                            + (f"\tpending_join={','.join(str(x) for x in pj)}" if pj else "") + "\n")
        print(f"sheet -> {out}")
    return 1 if missing else 0


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = sys.argv[1:]

    def opt(name):
        return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else None

    if "--walkback" in a:
        if not opt("--out"):
            raise SystemExit("--walkback needs --out <file>")
        return walkback(opt("--walkback"), opt("--out"))
    if "--project" in a:
        out = opt("--out")
        if not out:
            raise SystemExit("--project needs --out <file>")
        return project(resolve_split_file(opt("--project")), out)
    skip = {opt("--out")} if opt("--out") else set()
    names = [x for x in a if not x.startswith("--") and x not in skip]
    if "--landed" in a:
        return landed([resolve_split_file(x) for x in names], opt("--out"))
    if names:
        files = [resolve_split_file(x) for x in names]
        every = "--all" in a
    else:
        files = sorted(os.path.join(idbase.DECISIONS, f) for f in os.listdir(idbase.DECISIONS)
                       if f.endswith(".jsonl") and RX_P.match(os.path.splitext(f)[0])) \
            if os.path.isdir(idbase.DECISIONS) else []
        every = True
    if not files:
        print("no split files")
        return 0
    cx = Ctx()
    seen_items = {} if every else None
    seen_norm = defaultdict(set) if every else None
    total, nwarn = 0, 0
    for p in files:
        errors, warnings = [], []
        s = check_file(p, cx, errors, seen_items, seen_norm, warnings)
        if s.get("landed") and "--unlanded" in a:
            errors.append("already LANDED — split_land.ps1 must not land a file twice")
        total += len(errors)
        nwarn += len(warnings)
        print(f"  {'FAIL' if errors else ' OK '} {os.path.basename(p):<18} {s['shelves']:>3} shelves "
              f"({s['split']} split, {s['kept']} kept)  {s['items']:>5} item(s) moved to {s['keys']} key(s)"
              f"{'  ' + str(s['joins']) + ' approved join(s)' if s['joins'] else ''}"
              f"{'  LANDED (checked as landed: every item carries its key and left its shelf)' if s.get('landed') else ''}")
        for e in errors:
            print(f"        {e}")
        for w in warnings:
            print(f"        WARN {w}")
    if every and seen_norm:
        # The same key in two files is a GATHER (S6791's 1963 run collecting S66349's issues) and is fine; two
        # DIFFERENT spellings of one canonical key in two files are one Series row nobody meant.
        for n, pairs in sorted(seen_norm.items()):
            keys = {k for k, _b in pairs}
            if len(keys) > 1 and len({b for _k, b in pairs}) > 1:
                total += 1
                print(f"        FAIL across files: keys {sorted(keys)} ({sorted({b for _k, b in pairs})}) normalize "
                      f"to one canonical key 'parsed:{n}'")
    print(f"{len(files)} file(s), {total} failure(s), {nwarn} warning(s)")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
