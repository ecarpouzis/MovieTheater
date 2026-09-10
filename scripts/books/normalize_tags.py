"""Fold the author pass's genre tags onto the canonical vocabulary, in place.

The author pass wrote free-text genres before `tag_vocab` existed, so the same
shelf is split across synonyms (`non-fiction` / `nonfiction`, `sci-fi` /
`science-fiction`, `literary` / `literary-fiction`) and a browse chip only bites
on one half. This rewrites those rows to the canonical value, drops the values
that are not genres at all (`fiction`, `young-adult` — the `audience` tag already
carries the latter), and adds the `setting` / `tone` tags the canonical genre
implies, so the back-filled rows match what the emitter writes from now on.

Guards, because this writes to the live books database:
  * ONLY rows whose insight carries `--model` (default the author pass) are
    touched. The openlibrary / calibre / opus-4-8 rows are the vocabulary this
    folds ONTO and are never rewritten.
  * Dry run by default: it reports every transition and writes nothing.
  * Chunked and resumable — one bounded batch of insights per call, a cursor
    (`--after`, the last insight id) carried by the caller, so an interruption
    costs one batch and a re-run is idempotent (a row already canonical is a
    no-op, and a derived tag already present is not duplicated).

  python scripts/books/normalize_tags.py                 # dry run, whole population
  python scripts/books/normalize_tags.py --apply         # drive it to completion
"""

import argparse
import os
import sqlite3
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tag_vocab import canon_genre, derived_tags

DEFAULT_DB = r"F:\Work\MovieTheater\data\books\v2\books.db"


def plan_for(insight_tags):
    """(rewrites, deletes, adds) for one insight's tag rows.

    `insight_tags` is the list of (Category, Value) already on the insight."""
    genres_in = [v for c, v in insight_tags if c == "genre"]
    have = {(c, v) for c, v in insight_tags}

    canon, rewrites, deletes = [], [], []
    for raw in genres_in:
        g = canon_genre(raw)
        if g is None:
            deletes.append(("genre", raw))
            continue
        if g in canon:                       # folded onto a sibling already kept
            deletes.append(("genre", raw))
            continue
        canon.append(g)
        if g != raw:
            rewrites.append(("genre", raw, g))

    adds = []
    for cat, values in derived_tags(canon).items():
        for v in values:
            if (cat, v) not in have:
                adds.append((cat, v))
    return rewrites, deletes, adds


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--model", default="claude-opus-5-author")
    ap.add_argument("--batch", type=int, default=5000, help="insights per chunk")
    ap.add_argument("--after", type=int, default=0, help="resume cursor: last insight id done")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--max-batches", type=int, default=0, help="0 = run to completion")
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    totals = Counter()
    transitions = Counter()
    after = args.after
    batches = 0

    while True:
        ids = [r[0] for r in con.execute(
            "SELECT Id FROM Insight WHERE SubjectKind = 0 AND ModelId = ? AND Id > ? "
            "ORDER BY Id LIMIT ?", (args.model, after, args.batch))]
        if not ids:
            break

        marks = ",".join("?" * len(ids))
        by_insight = {}
        for iid, cat, val in con.execute(
                f"SELECT InsightId, Category, Value FROM InsightTag WHERE InsightId IN ({marks})", ids):
            by_insight.setdefault(iid, []).append((cat, val))

        writes = []
        for iid in ids:
            rewrites, deletes, adds = plan_for(by_insight.get(iid, []))
            # Deletes run FIRST: when an insight carries both a synonym and the
            # canonical value, rewriting the synonym would collide with the row
            # the delete is about to remove (the unique key is insight+cat+value).
            for cat, val in deletes:
                transitions[f"{val} -> (dropped)"] += 1
                totals["dropped"] += 1
                writes.append(("delete", iid, cat, val, None))
            for cat, old, new in rewrites:
                transitions[f"{old} -> {new}"] += 1
                totals["rewritten"] += 1
                writes.append(("rewrite", iid, cat, old, new))
            for cat, val in adds:
                transitions[f"+ {cat}:{val}"] += 1
                totals["added"] += 1
                writes.append(("add", iid, cat, val, None))

        if args.apply and writes:
            cur = con.cursor()
            for op, iid, cat, a, b in writes:
                if op == "rewrite":
                    cur.execute("UPDATE InsightTag SET Value = ? WHERE InsightId = ? AND Category = ? AND Value = ?",
                                (b, iid, cat, a))
                elif op == "delete":
                    cur.execute("DELETE FROM InsightTag WHERE InsightId = ? AND Category = ? AND Value = ?",
                                (iid, cat, a))
                else:
                    cur.execute("INSERT INTO InsightTag (InsightId, Category, Value) "
                                "SELECT ?, ?, ? WHERE NOT EXISTS (SELECT 1 FROM InsightTag "
                                "WHERE InsightId = ? AND Category = ? AND Value = ?)",
                                (iid, cat, a, iid, cat, a))
            con.commit()

        after = ids[-1]
        totals["insights"] += len(ids)
        batches += 1
        print(f"{{ processed: {totals['insights']}, nextCursor: {after}, "
              f"rewritten: {totals['rewritten']}, dropped: {totals['dropped']}, added: {totals['added']} }}"
              + ("" if args.apply else "  [dry run]"), flush=True)
        if args.max_batches and batches >= args.max_batches:
            break

    print()
    print("=== transitions (top 40) ===")
    for name, n in transitions.most_common(40):
        print(f"  {name:<44} {n}")
    print()
    print(f"insights seen {totals['insights']}, rewritten {totals['rewritten']}, "
          f"dropped {totals['dropped']}, added {totals['added']}"
          + ("" if args.apply else "  — DRY RUN, nothing written"))
    if args.apply:
        print("next: books-resolve (refolds ItemTag from the insight tags)")


if __name__ == "__main__":
    main()
