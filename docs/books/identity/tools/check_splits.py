"""The contract for a split decision (TOOLS_TODO 27): `decisions/P-NNN.jsonl` checked before `books-series-split`.

`python check_splits.py <P-001> [more...] [--all]`       (with no names: every decisions/P-*.jsonl, with --all implied)
`python check_splits.py --project <P-001> --out <file>`  the verb's input: the item lines as {itemId, key} only
`python check_splits.py --landed <P-001> [...] [--out sheet.tsv]`   after landing: which shelf each key became
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
One `shelf` line per shelf in the batch's `.ids` — the coverage contract, as `S`/`R` is for a tier batch;
`"split": false` says the shelf should NOT be split (the flag was wrong, or a dominant run with residue) and
then no item of it may appear. An item line is written only for an item that MOVES; items that stay are
omitted. `run` is optional and names the new run's ids where known (legs as on a `C` line: cv, gcd, mu, barney,
inducks, marvel; a value may be a list when a leg splits the run, as GCD does Storm Front Vol. 2 by publisher)
— it seeds the new shelf's `S` line in its identity batch, and the verb never reads it.

`books-series-split` reads only `itemId` and `key` and would count a `shelf` line as a bad line, so the landing
feeds it `--project`'s output, never the P- file itself.

Rules, each a failure with its own message:
  • the line is a JSON object with only the fields above; `itemId` an int; `key` a trimmed, non-empty string
    (<= 200 chars, no control characters) that differs from the item's CURRENT ParsedSeriesKey
  • every item exists (a live comic file with a ComicDetail row) and sits on a shelf of this batch whose shelf
    line says `split: true`, and whose WINNING decision carries `F split-needed`
  • no item twice (in the file; with --all, across every P- file)
  • every key is NEW, or lands on a live shelf the source shelf's winning `F split-needed` line NAMES (`S<id>`)
    — a join the reader asked for; landing back on the source shelf, or on any other live shelf, is refused
  • two different keys that normalize to one canonical key are refused (the verb would make them one row)
  • every shelf keeps at least one item; a `split: true` shelf moves at least one
  • a `run` names only known legs with real ids, and every line of one key states the same `run`
"""
import json
import os
import re
import sys
from collections import Counter, defaultdict

import idbase
import splitbase

FIELDS_ITEM = {"itemId", "key", "run"}
FIELDS_SHELF = {"shelf", "split", "why"}
MIN_WHY = 40
MAX_KEY = 200
RX_CTRL = re.compile(r"[\x00-\x1f\x7f]")
RX_P = re.compile(r"^P-(\d+)$")


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


class Ctx:
    """Everything the rules read, loaded once per run."""

    def __init__(self):
        self.ev = ev = idbase.Evidence()
        self.decides, self.winner, _s, _d = idbase.scan_decisions()
        self.pop = splitbase.population(ev, self.decides, self.winner)
        self.landing = splitbase.Landing(ev.con, ev.shelf_set)
        self.item = {}
        for iid, sid, key in ev.con.execute("""
                SELECT i.Id, i.SeriesId, cd.ParsedSeriesKey FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0"""):
            self.item[iid] = (sid, key or "")
        self._ck = None
        self._named = {}

    def checker(self):
        if self._ck is None:
            import check_identity
            self._ck = check_identity.Checker()
        return self._ck

    def named(self, sid):
        """The shelves the winning `F split-needed` line of `sid` names — the only shelves a key may JOIN."""
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


def check_file(path, cx, errors, seen_items=None, seen_norm=None):
    """Every rule on one file. -> summary dict. `seen_items` / `seen_norm` carry state across files (--all)."""
    base = os.path.basename(path)
    ev = cx.ev
    idp = ids_path(path)
    if not os.path.exists(idp):
        errors.append(f"no .ids for {base} (looked beside it and in batches/) — nothing to be complete against")
        batch = set()
    else:
        batch = {int(x.strip().lstrip("S")) for x in open(idp, encoding="utf-8") if x.strip().lstrip("S").isdigit()}
    lines = read_lines(path, errors)
    shelf_line, moves = {}, []
    for k, o in lines:
        if "shelf" in o:
            extra = set(o) - FIELDS_SHELF
            if extra:
                errors.append(f"line {k}: unknown field(s) {sorted(extra)} on a shelf line")
            sid = o.get("shelf")
            if not isinstance(sid, int):
                errors.append(f"line {k}: `shelf` must be an integer shelf id")
                continue
            if sid in shelf_line:
                errors.append(f"line {k}: S{sid} has two shelf lines (line {shelf_line[sid][0]} too)")
                continue
            if sid not in batch:
                errors.append(f"line {k}: S{sid} is not in {os.path.basename(idp)}")
            if not isinstance(o.get("split"), bool):
                errors.append(f"line {k}: S{sid} `split` must be true or false")
            why = o.get("why")
            if not isinstance(why, str) or len(why.strip()) < MIN_WHY:
                errors.append(f"line {k}: S{sid} `why` must say which runs and which records (>= {MIN_WHY} chars)")
            shelf_line[sid] = (k, o)
        elif "itemId" in o:
            extra = set(o) - FIELDS_ITEM
            if extra:
                errors.append(f"line {k}: unknown field(s) {sorted(extra)} on an item line (fields: itemId, key, run)")
            moves.append((k, o))
        else:
            errors.append(f"line {k}: neither a shelf line nor an item line")

    for sid in sorted(batch - set(shelf_line)):
        errors.append(f"S{sid} is in the batch and has no shelf line — every shelf is answered, split or not")

    moved_from = Counter()
    key_run, key_src, in_file = {}, defaultdict(set), set()
    for k, o in moves:
        iid, key = o.get("itemId"), o.get("key")
        loc = f"line {k}: item {iid}"
        if not isinstance(iid, int) or isinstance(iid, bool):
            errors.append(f"line {k}: `itemId` must be an integer")
            continue
        if not isinstance(key, str) or not key.strip():
            errors.append(f"{loc}: `key` must be a non-empty string")
            continue
        if key != key.strip() or RX_CTRL.search(key) or len(key) > MAX_KEY:
            errors.append(f"{loc}: key {key!r} must be trimmed, <= {MAX_KEY} chars, with no control characters "
                          f"(the verb trims it, so the key checked here would not be the key written)")
            continue
        if iid in in_file:
            errors.append(f"{loc}: moved twice in this file")
            continue
        in_file.add(iid)
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
        how, hit = cx.landing.land(key)
        live = cx.landing.live(hit)
        if src in live:
            errors.append(f"{loc}: key {key!r} resolves ({how}) back to S{src} itself — the item would not move")
        else:
            stray = live - cx.named(src)
            if stray:
                s0 = sorted(stray)[0]
                errors.append(f"{loc}: key {key!r} lands ({how}) on live shelf S{s0} "
                              f"'{cx.landing.name.get(s0, '?')}', which S{src}'s F split-needed line does not name "
                              f"— the resolve would MERGE the item into it; pick a key of its own")
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
                        if leg in ("cv", "gcd", "mu") and (not isinstance(x, int) or isinstance(x, bool)):
                            errors.append(f"{loc}: run {leg}={x!r} must be an integer id")
                        elif leg in ("cv", "gcd", "mu") and not cx.run_ok(leg, x):
                            errors.append(f"{loc}: run {leg}={x} is not in our tables or the local catalogues")
                canon = json.dumps(run, sort_keys=True)
                if key in key_run and key_run[key][0] != canon:
                    errors.append(f"{loc}: key {key!r} names run {canon} here and {key_run[key][0]} on line "
                                  f"{key_run[key][1]} — one key is one run")
                key_run.setdefault(key, (canon, k))

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
            "items": len(in_file), "keys": len(key_src)}


def project(path, out):
    """The verb's input: `{itemId, key}` per item line, in file order. The P- file is a superset the verb cannot
    read whole (a shelf line has no itemId and would be counted as a bad line)."""
    n = 0
    with open(out, "w", encoding="utf-8") as f:
        for raw in open(path, encoding="utf-8"):
            t = raw.strip()
            if not t:
                continue
            o = json.loads(t)
            if "itemId" in o:
                f.write(json.dumps({"itemId": o["itemId"], "key": o["key"]}, ensure_ascii=False) + "\n")
                n += 1
    print(f"{n} item line(s) -> {out}")


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


def landed(paths, out=None):
    """After the landing: where each key now lives. Read-only. The sheet's first column is `S<sid>`, which is
    what `next_batch.py --revisit-file` reads, so the new shelves go straight into an identity batch with the
    reader's `run` ids beside them to seed the `S` line."""
    con = idbase.open_hot()
    alias = dict(con.execute("SELECT ParsedKey, SeriesId FROM SeriesAlias"))
    rows = []
    for p in paths:
        seen = {}
        for raw in open(p, encoding="utf-8"):
            t = raw.strip()
            if not t:
                continue
            o = json.loads(t)
            if "itemId" not in o or o["key"] in seen:
                continue
            seen[o["key"]] = o.get("run")
        for key, run in seen.items():
            sid = alias.get(key)
            if sid is None:
                sid = con.execute("SELECT Id FROM Series WHERE ParsedKey = ?", (key,)).fetchone()
                sid = sid[0] if sid else None
            n = con.execute("SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id "
                            "WHERE cd.ParsedSeriesKey = ? AND i.SeriesId = ?", (key, sid)).fetchone()[0] if sid else 0
            rows.append((sid, key, run, n, os.path.basename(p)))
    for sid, key, run, n, b in rows:
        print(f"  {('S' + str(sid)) if sid else '(no shelf yet)':<9} {n:>4} item(s)  {key}  run={json.dumps(run) if run else '-'}  [{b}]")
    missing = [r for r in rows if not r[0] or not r[3]]
    print(f"{len(rows)} key(s); {len(missing)} not yet on a shelf of their own (run books-resolve --series)")
    if out:
        with open(out, "w", encoding="utf-8") as f:
            f.write("# new shelves from the split lane — feed to next_batch.py --revisit-file\n")
            for sid, key, run, n, b in rows:
                if sid:
                    f.write(f"S{sid}\t{key}\trun={json.dumps(run) if run else '-'}\t{b}\n")
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
        project(resolve_split_file(opt("--project")), out)
        return 0
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
    total = 0
    for p in files:
        errors = []
        s = check_file(p, cx, errors, seen_items, seen_norm)
        total += len(errors)
        print(f"  {'FAIL' if errors else ' OK '} {os.path.basename(p):<18} {s['shelves']:>3} shelves "
              f"({s['split']} split, {s['kept']} kept)  {s['items']:>5} item(s) moved to {s['keys']} key(s)")
        for e in errors:
            print(f"        {e}")
    if every and seen_norm:
        # The same key in two files is a GATHER (S6791's 1963 run collecting S66349's issues) and is fine; two
        # DIFFERENT spellings of one canonical key in two files are one Series row nobody meant.
        for n, pairs in sorted(seen_norm.items()):
            keys = {k for k, _b in pairs}
            if len(keys) > 1 and len({b for _k, b in pairs}) > 1:
                total += 1
                print(f"        FAIL across files: keys {sorted(keys)} ({sorted({b for _k, b in pairs})}) normalize "
                      f"to one canonical key 'parsed:{n}'")
    print(f"{len(files)} file(s), {total} failure(s)")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
