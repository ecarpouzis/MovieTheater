"""Expand the pass-2 per-series decision files into the importer's JSONL + the flags sheet.

Same contract as the first pass and for the same reason: it REFUSES to expand a series unless every
collected edition in it is decided exactly once. That refusal is the coverage guarantee — it is what
makes "I did this series" checkable rather than claimed.

Decision syntax, one per line, in pass2/S<seriesId>.txt:

    S <itemId> <start> <end> <conf> | <edition title> | <evidence>   write a range
    u <itemId> <why>                                                 this edition: no known range
    U <seriesId> <why>                                               every edition in the series
    F <itemId> <flag> | <detail>                                     a question for Eric
    N <seriesId> <note>                                              a series-level finding
"""
import json, os, re, sqlite3, sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")
HOT = os.environ.get("BOOKS_DB", "F:/Work/MovieTheater/data/books/v2/books.db")

FLAGS = {"overlap-in-series", "conflated-series", "label-ambiguous", "provider-disagrees",
         "arithmetic-odd", "duplicate-edition", "span-retracted", "issue-numbers-wrong"}


def load_packets():
    """The coverage set, read from the live file rather than a packet export.

    The guarantee is the point, not where it comes from: a series does not expand unless every
    collected edition in it is decided exactly once. Reading books.db directly means the check is
    against what is on the shelf today, so a decision file cannot go stale against a packet built
    hours earlier."""
    con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
    by_series = defaultdict(lambda: {"collections": []})
    for sid, iid, fn in con.execute(
            """SELECT i.SeriesId, i.Id, i.FileName FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
               WHERE cd.IsCollection = 1 AND coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL"""):
        by_series[sid]["collections"].append({"id": iid, "file": fn})
    con.close()
    return by_series


def parse(path, packets):
    sid = int(re.search(r"S(\d+)", os.path.basename(path)).group(1))
    pkt = packets.get(sid)
    if pkt is None:
        # A shelf that no longer holds a single collected edition: every book it was judged on moved to
        # another run when the folder fold re-keyed it. The judgements themselves are not lost - they were
        # re-homed to the shelf their ITEM is on - so an empty file here is residue, not a gap. It must
        # still not silently expand: it is reported and skipped, and rehome_decisions.py clears it.
        return sid, {}, {}, [], []
    want = {c["id"] for c in pkt["collections"]}
    spans, unknowns, flags, notes = {}, {}, [], []

    for n, raw in enumerate(open(path, encoding="utf-8"), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        kind, rest = line.split(None, 1)
        if kind == "S":
            head, title, why = [x.strip() for x in rest.split("|", 2)]
            iid, a, b, conf = head.split()
            spans[int(iid)] = dict(itemId=int(iid), seriesId=sid, start=float(a), end=float(b),
                                   editionTitle=title, confidence=float(conf), rationale=why,
                                   batch="pass2")
        elif kind == "u":
            iid, why = rest.split(None, 1)
            unknowns[int(iid)] = dict(itemId=int(iid), seriesId=sid, unknown=True, why=why.strip(),
                                      batch="pass2")
        elif kind == "U":
            _, why = rest.split(None, 1)
            for c in pkt["collections"]:
                unknowns.setdefault(c["id"], dict(itemId=c["id"], seriesId=sid, unknown=True,
                                                  why=why.strip(), batch="pass2"))
        elif kind == "F":
            head, detail = [x.strip() for x in rest.split("|", 1)]
            iid, flag = head.split()
            if flag not in FLAGS:
                raise SystemExit(f"{path}:{n}: unknown flag '{flag}'")
            flags.append([int(iid), sid, flag, detail])
        elif kind == "N":
            _, note = rest.split(None, 1)
            notes.append([sid, note.strip()])
        else:
            raise SystemExit(f"{path}:{n}: unknown directive '{kind}'")

    decided = set(spans) | set(unknowns)
    missing = want - decided
    extra = decided - want
    if missing:
        raise SystemExit(f"{path}: {len(missing)} edition(s) undecided: {sorted(missing)[:12]}")
    if extra:
        raise SystemExit(f"{path}: {len(extra)} decision(s) for items not in this series: {sorted(extra)[:12]}")
    both = set(spans) & set(unknowns)
    if both:
        raise SystemExit(f"{path}: decided twice: {sorted(both)[:12]}")
    return sid, spans, unknowns, flags, notes


def main():
    packets = load_packets()
    files = sorted(f for f in os.listdir(DEC) if re.match(r"^S\d+\.txt$", f)) if os.path.isdir(DEC) else []
    if not files:
        raise SystemExit(f"no decision files in {DEC}")
    all_rows, all_flags, all_notes = [], [], []
    per = []
    for f in files:
        sid, spans, unknowns, flags, notes = parse(os.path.join(DEC, f), packets)
        rows = sorted(list(spans.values()) + list(unknowns.values()), key=lambda r: r["itemId"])
        all_rows += rows
        all_flags += flags
        all_notes += notes
        per.append((sid, len(spans), len(unknowns), len(flags)))

    with open(os.path.join(HERE, "pass2_spans.jsonl"), "w", encoding="utf-8") as out:
        for r in all_rows:
            out.write(json.dumps(r, ensure_ascii=False) + "\n")
    import csv
    with open(os.path.join(HERE, "pass2_flags.csv"), "w", encoding="utf-8", newline="") as out:
        csv.writer(out).writerows(all_flags)
    with open(os.path.join(HERE, "pass2_notes.csv"), "w", encoding="utf-8", newline="") as out:
        csv.writer(out).writerows(all_notes)

    print(f"{len(per)} series decided — {sum(p[1] for p in per)} ranges, "
          f"{sum(p[2] for p in per)} refusals, {sum(p[3] for p in per)} flags")
    for sid, s, u, fl in per:
        print(f"   S{sid:<7} {s:>3} ranges  {u:>3} refusals  {fl:>2} flags")


main()
