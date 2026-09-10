"""The model pass, run per BOOK — the tail the author pass cannot reach.

`model_pass_authors.py` judges an author once and expands the verdict over every book that author
wrote. That covered ~96% of the section, and then it ran out of authors: what is left carries no
usable author-level signal —

  * authors the author pass answered `mixed` or `unknown` (the `deferred-authors.txt` roster), and
  * rows whose creator is blank, "unknown", "anonymous" or "various" — never an author at all.

Those books still have TITLES, and a title plus a publisher is often enough on its own. So this is
the same machine keyed on `Item.Id` instead of a name: emit a window of undecided books, the model
writes one verdict per id, and `--apply` expands them into the same insight JSONL that
`books-insight-import` already eats. Same kid-clearance guard (a maturity-0 verdict below High
confidence is demoted to teen), same ADULT_TAGS block (a book's own tags outrank a judgement that
would clear it for children).

Usage
-----
  python scripts/books/model_pass_books.py --emit 300 --out bbatch-01.json
  #   the model writes bverdicts-01.jsonl, one object per line:
  #     {"id": 41233, "maturity": 3, "audience": "adult", "genres": ["erotica"], "confidence": "High"}
  #     {"id": 41234, "verdict": "unknown"}        -> abstained, stays unclassified
  python scripts/books/model_pass_books.py --apply bverdicts-01.jsonl --out binsights-01.jsonl
  MovieTheater.BooksHost.exe books-insight-import --file binsights-01.jsonl --apply
  MovieTheater.BooksHost.exe books-resolve

Resumable by construction, exactly like the author pass: the candidate query excludes any book that
already carries an insight row, so an imported batch drops out of the set immediately and the next
`--emit` continues past it. `--after-id` only walks further down the same ordering in one sitting.
"""
import argparse
import io
import json
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tag_vocab import canon_genres, derived_tags
from model_pass_authors import (
    DEFAULT_DB, AUDIENCES, CONFIDENCES, ADULT_TAGS, UNDECIDED, scope_clause, connect)

MODEL_ID = "claude-opus-5-book"   # contains "opus" -> Transforms.ModelRank = 3


def deferred_path(args):
    """Ids the model abstained on. Without this they resurface at the top of every later batch."""
    return os.path.join(os.path.dirname(os.path.abspath(args.out)) or ".", "deferred-books.txt")


def read_deferred(args):
    path = deferred_path(args)
    if not os.path.isfile(path):
        return set()
    with io.open(path, encoding="utf-8") as f:
        return {int(line) for line in f if line.strip().isdigit()}


def emit(args):
    con = connect(args.db)
    deferred = read_deferred(args)
    rows = con.execute(f"""
        SELECT i.Id, i.ResolvedTitle, i.ResolvedCreatorsCsv, b.Publisher, b.SeriesName, i.ResolvedYear
        FROM Item i LEFT JOIN BookDetail b ON b.ItemId = i.Id
        WHERE {UNDECIDED} AND {scope_clause(args.scope)} AND i.Id > ?
        ORDER BY i.Id""", (args.after_id,)).fetchall()
    rows = [r for r in rows if r[0] not in deferred]
    remaining = len(rows)
    rows = rows[:args.emit]

    batch = [{"id": iid, "title": title, "author": creators, "publisher": pub,
              "series": series, "year": year}
             for iid, title, creators, pub, series, year in rows]
    out = {
        "scope": args.scope,
        "afterId": args.after_id,
        "books": len(batch),
        "remainingBooks": max(0, remaining - len(batch)),
        "nextAfterId": batch[-1]["id"] if batch else args.after_id,
        "batch": batch,
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    print(f"{{ books: {len(batch)}, remainingBooks: {out['remainingBooks']}, "
          f"nextAfterId: {out['nextAfterId']} }}")
    print(f"wrote {args.out}")
    return 0


def apply(args):
    verdicts = {}
    abstained = bad = 0
    abstained_ids = []
    with open(args.verdicts, encoding="utf-8") as f:
        for line_no, line in enumerate(f, 1):
            line = line.strip()
            if not line or line.startswith("//"):
                continue
            v = json.loads(line)
            iid = v.get("id")
            if not isinstance(iid, int):
                print(f"  line {line_no}: no id — skipped", file=sys.stderr)
                bad += 1
                continue
            if v.get("verdict") in ("unknown", "mixed"):
                abstained += 1
                abstained_ids.append(iid)
                continue

            audience = v.get("audience")
            maturity = v.get("maturity")
            confidence = v.get("confidence", "Medium")
            if audience not in AUDIENCES:
                print(f"  line {line_no}: bad audience {audience!r} — skipped", file=sys.stderr)
                bad += 1
                continue
            if maturity is None:
                maturity = AUDIENCES[audience]
            if maturity not in (0, 1, 2, 3):
                print(f"  line {line_no}: bad maturity {maturity!r} — skipped", file=sys.stderr)
                bad += 1
                continue
            if confidence not in CONFIDENCES:
                confidence = "Medium"
            if maturity == 0 and confidence != "High":
                print(f"  id {iid}: maturity 0 at {confidence} confidence — demoted to teen", file=sys.stderr)
                maturity, audience = 1, "teen"
            verdicts[iid] = {
                "maturity": maturity, "audience": audience, "confidence": confidence,
                "genres": [g for g in (v.get("genres") or []) if isinstance(g, str)],
            }

    con = connect(args.db)
    undecided = {iid for (iid,) in con.execute(f"""
        SELECT i.Id FROM Item i LEFT JOIN BookDetail b ON b.ItemId = i.Id
        WHERE {UNDECIDED} AND {scope_clause(args.scope)}""")}

    adult_tagged = set()
    q = ",".join("?" for _ in ADULT_TAGS)
    for (iid,) in con.execute(f"SELECT DISTINCT ItemId FROM ItemTag WHERE Value IN ({q})", ADULT_TAGS):
        adult_tagged.add(iid)

    written = blocked = stale = 0
    with open(args.out, "w", encoding="utf-8") as out:
        for iid, v in verdicts.items():
            if iid not in undecided:
                stale += 1          # already judged by another pass since the emit — leave it alone
                continue
            if v["maturity"] <= 1 and iid in adult_tagged:
                blocked += 1
                continue
            genres = canon_genres(v["genres"])
            tags = {"audience": [v["audience"]]}
            if genres:
                tags["genre"] = genres
            tags.update(derived_tags(genres))
            out.write(json.dumps({
                "subject": "book", "id": iid, "model": MODEL_ID,
                "confidence": v["confidence"], "maturity": v["maturity"], "tags": tags,
            }, ensure_ascii=False) + "\n")
            written += 1

    if abstained_ids:
        path = deferred_path(args)
        existing = read_deferred(args)
        with io.open(path, "a", encoding="utf-8") as f:
            for iid in abstained_ids:
                if iid not in existing:
                    f.write(str(iid) + chr(10))
        print(f"recorded {len(set(abstained_ids) - existing)} id(s) in {path}")

    print(f"{{ judged: {len(verdicts)}, abstained: {abstained}, malformed: {bad}, "
          f"booksWritten: {written}, blockedByOwnTags: {blocked}, alreadyJudged: {stale} }}")
    print(f"wrote {args.out}")
    return 0


def main():
    ap = argparse.ArgumentParser(description="Per-book model pass (the tail the author pass cannot reach)")
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--scope", choices=("no-isbn", "has-isbn", "all"), default="all")
    ap.add_argument("--emit", type=int, help="Emit this many undecided books to judge")
    ap.add_argument("--after-id", type=int, default=0, help="Walk further down the same ordering")
    ap.add_argument("--apply", dest="verdicts", help="Expand a verdicts JSONL into insight JSONL")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    if args.verdicts:
        return apply(args)
    if args.emit:
        return emit(args)
    ap.error("one of --emit or --apply is required")


if __name__ == "__main__":
    sys.exit(main())
