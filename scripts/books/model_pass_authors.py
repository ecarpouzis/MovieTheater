"""The model pass, run per AUTHOR — emit candidates, expand verdicts into per-book insights.

Why per author
--------------
114,702 books carry no classification, and there is no API key in this project by design: the established
pattern (`LoadAiMetadataCommand`, `books-insight-import`) is that the MODEL IS THE SESSION and the verb is
the idempotent sink. One book per model judgement does not reach 114k in any reasonable number of sessions.

An author is the far better unit. It is more efficient — the top 1,000 authors cover 43% of the no-ISBN
pile, and Carolyn Keene alone is 250 books — and it is more RELIABLE, because a model knows "Franklin W
Dixon writes the Hardy Boys" with far more confidence than it knows any single obscure title. The catch is
that some authors write across audiences, so the verdict vocabulary includes `mixed`, which defers those
books to a per-book pass rather than smearing one verdict over them.

Order of work
-------------
Books with NO ISBN come first (`--scope no-isbn`, the default): `books-isbn-enrich` can never help them,
whereas a book with an ISBN may still be settled for free by the Open Library fold once the fetch lands.
That slice is also where the remaining adult romance is concentrated — erotica ebooks routinely ship
without an ISBN.

Usage
-----
  # 1. emit the next N authors to judge
  python scripts/books/model_pass_authors.py --emit 120 --out batch-01.json

  # 2. the model writes verdicts-01.jsonl, one JSON object per line:
  #      {"author": "Carolyn Keene", "maturity": 0, "audience": "children",
  #       "genres": ["mystery"], "confidence": "High", "note": "Nancy Drew"}
  #      {"author": "Neil Gaiman", "verdict": "mixed"}     -> deferred to a per-book pass
  #      {"author": "V. Obscure",  "verdict": "unknown"}   -> abstained, stays unclassified

  # 3. expand to per-book insight JSONL for books-insight-import
  python scripts/books/model_pass_authors.py --apply verdicts-01.jsonl --out insights-01.jsonl

  MovieTheater.BooksHost.exe books-insight-import --file insights-01.jsonl            # dry run
  MovieTheater.BooksHost.exe books-insight-import --file insights-01.jsonl --apply
  MovieTheater.BooksHost.exe books-resolve

Resumable by construction: every query excludes books that already carry ANY insight row, so an imported
batch drops out of the candidate set the moment it is imported — before `books-resolve` has stamped
currency — and the next `--emit` continues where the last one stopped. Nothing needs a stored cursor, and
re-running a batch is a no-op rather than a double-apply.
"""
import argparse
import json
import os
import sqlite3
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tag_vocab import canon_genres, derived_tags

DEFAULT_DB = r"F:\Work\MovieTheater\data\books\v2\books.db"

MODEL_ID = "claude-opus-5-author"   # contains "opus" -> Transforms.ModelRank = 3
AUDIENCES = {"children": 0, "teen": 1, "mature": 2, "adult": 3}
CONFIDENCES = {"High", "Medium", "Low"}

# Anything the deterministic pass already folded onto a book that CONTRADICTS a kid verdict. A per-author
# judgement is a generalisation, and a generalisation must never be the thing that clears a specific book
# for children when that book's own tags disagree.
ADULT_TAGS = ("adult-romance", "erotica", "Erotica", "mature", "adult")

# "Not yet judged" deliberately tests for ANY insight row, not a CURRENT one. `books-insight-import`
# inserts with `IsCurrent = 0` and only `books-resolve` stamps currency, so a currency test would re-emit
# every author from the batch just applied and judge them twice.
UNDECIDED = """i.Kind = 1 AND i.IsExcluded = 0
    AND NOT EXISTS(SELECT 1 FROM Insight n
                   WHERE n.SubjectKind = 0 AND n.SubjectId = i.Id)"""


def scope_clause(scope):
    if scope == "no-isbn":
        return "(b.Isbn IS NULL OR b.Isbn = '')"
    if scope == "has-isbn":
        return "(b.Isbn IS NOT NULL AND b.Isbn <> '')"
    return "1=1"


def connect(db):
    return sqlite3.connect(f"file:{db}?mode=ro", uri=True, timeout=60)


def deferred_path(args):
    """Where the `mixed`/`unknown` roster lives — beside the batch/verdict files by default."""
    if args.deferred:
        return args.deferred
    return os.path.join(os.path.dirname(os.path.abspath(args.out)) or ".", "deferred-authors.txt")


def read_deferred(args):
    path = deferred_path(args)
    if not os.path.isfile(path):
        return set()
    with open(path, encoding="utf-8") as f:
        return {line.strip() for line in f if line.strip()}


def emit(args):
    con = connect(args.db)
    rows = con.execute(f"""
        SELECT i.Id, i.ResolvedCreatorsCsv, i.ResolvedTitle, b.Publisher, b.SeriesName, i.ResolvedYear
        FROM Item i LEFT JOIN BookDetail b ON b.ItemId = i.Id
        WHERE {UNDECIDED} AND {scope_clause(args.scope)}""").fetchall()

    # Authors already answered `mixed` or `unknown` never gain an insight, so without this they resurface
    # at the top of EVERY subsequent batch and get re-read and re-judged forever.
    deferred = read_deferred(args)

    by_author = defaultdict(list)
    for iid, creators, title, publisher, series, year in rows:
        name = (creators or "").strip()
        if not name or name.lower() in ("unknown", "anonymous", "various"):
            continue        # not an author; these need a per-book pass, never one verdict
        if name in deferred:
            continue
        by_author[name].append((iid, title, publisher, series, year))

    # Authors already judged in an earlier batch are skipped by construction: their books have insights and
    # so never enter `rows`. `--after-rank` only exists to walk further down the same ordering in one sitting.
    ordered = sorted(by_author.items(), key=lambda kv: (-len(kv[1]), kv[0]))
    ordered = [a for a in ordered if len(a[1]) >= args.min_books]
    window = ordered[args.after_rank: args.after_rank + args.emit]

    batch = []
    for name, books in window:
        publishers = sorted({p for _, _, p, _, _ in books if p})
        seriess = sorted({s for _, _, _, s, _ in books if s})
        years = sorted({y for _, _, _, _, y in books if y})
        batch.append({
            "author": name,
            "books": len(books),
            "samples": [t for _, t, _, _, _ in books[:args.samples] if t],
            "publishers": publishers[:4],
            "series": seriess[:4],
            "years": f"{years[0]}-{years[-1]}" if years else None,
        })

    covered = sum(b["books"] for b in batch)
    remaining_authors = max(0, len(ordered) - (args.after_rank + len(batch)))
    remaining_books = sum(len(v) for _, v in ordered[args.after_rank + len(batch):])
    out = {
        "scope": args.scope,
        "afterRank": args.after_rank,
        "authors": len(batch),
        "booksCovered": covered,
        "remainingAuthors": remaining_authors,
        "remainingBooks": remaining_books,
        "nextAfterRank": args.after_rank + len(batch),
        "batch": batch,
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    print(f"{{ authors: {len(batch)}, booksCovered: {covered}, "
          f"remainingAuthors: {remaining_authors}, remainingBooks: {remaining_books}, "
          f"nextAfterRank: {out['nextAfterRank']} }}")
    print(f"wrote {args.out}")
    return 0


def apply(args):
    verdicts = {}
    deferred = abstained = 0
    deferred_names = []
    with open(args.verdicts, encoding="utf-8") as f:
        for line_no, line in enumerate(f, 1):
            line = line.strip()
            if not line or line.startswith("//"):
                continue
            v = json.loads(line)
            author = (v.get("author") or "").strip()
            if not author:
                print(f"  line {line_no}: no author — skipped", file=sys.stderr)
                continue
            kind = v.get("verdict")
            if kind == "mixed":
                deferred += 1
                deferred_names.append(author)
                continue
            if kind == "unknown":
                abstained += 1
                deferred_names.append(author)
                continue

            audience = v.get("audience")
            maturity = v.get("maturity")
            confidence = v.get("confidence", "Medium")
            if audience not in AUDIENCES:
                print(f"  line {line_no}: bad audience {audience!r} — skipped", file=sys.stderr)
                continue
            if maturity is None:
                maturity = AUDIENCES[audience]
            if maturity not in (0, 1, 2, 3):
                print(f"  line {line_no}: bad maturity {maturity!r} — skipped", file=sys.stderr)
                continue
            if confidence not in CONFIDENCES:
                confidence = "Medium"
            # Kid clearance is the one direction where a wrong generalisation puts adult material in front
            # of a child, so an author-level verdict may only produce it when the model was certain.
            if maturity == 0 and confidence != "High":
                print(f"  {author}: maturity 0 at {confidence} confidence — demoted to teen", file=sys.stderr)
                maturity, audience = 1, "teen"
            verdicts[author] = {
                "maturity": maturity, "audience": audience, "confidence": confidence,
                "genres": [g for g in (v.get("genres") or []) if isinstance(g, str)],
                "note": v.get("note"),
            }

    con = connect(args.db)
    rows = con.execute(f"""
        SELECT i.Id, i.ResolvedCreatorsCsv FROM Item i
        LEFT JOIN BookDetail b ON b.ItemId = i.Id
        WHERE {UNDECIDED} AND {scope_clause(args.scope)}""").fetchall()

    # Books already carrying a contradicting tag, so a kid verdict cannot override the book's own evidence.
    adult_tagged = set()
    q = ",".join("?" for _ in ADULT_TAGS)
    for (iid,) in con.execute(f"SELECT DISTINCT ItemId FROM ItemTag WHERE Value IN ({q})", ADULT_TAGS):
        adult_tagged.add(iid)

    written = blocked = 0
    per_author = defaultdict(int)
    with open(args.out, "w", encoding="utf-8") as out:
        for iid, creators in rows:
            v = verdicts.get((creators or "").strip())
            if v is None:
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
            per_author[(creators or "").strip()] += 1

    if deferred_names:
        path = deferred_path(args)
        existing = read_deferred(args)
        with open(path, "a", encoding="utf-8") as f:
            for name in deferred_names:
                if name not in existing:
                    f.write(name + "\n")
        print(f"recorded {len(set(deferred_names) - existing)} author(s) in {path}")

    print(f"{{ authorsJudged: {len(verdicts)}, deferredMixed: {deferred}, abstained: {abstained}, "
          f"booksWritten: {written}, blockedByOwnTags: {blocked} }}")
    print(f"wrote {args.out}")
    if args.verbose:
        for a, n in sorted(per_author.items(), key=lambda kv: -kv[1])[:25]:
            print(f"   {n:5d}  {a}  -> maturity {verdicts[a]['maturity']} ({verdicts[a]['confidence']})")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--scope", choices=["no-isbn", "has-isbn", "all"], default="no-isbn")
    ap.add_argument("--emit", type=int, help="Emit this many authors as a batch to judge")
    ap.add_argument("--after-rank", type=int, default=0, help="Skip this many authors in the ordering")
    ap.add_argument("--min-books", type=int, default=2, help="Ignore authors with fewer undecided books")
    ap.add_argument("--samples", type=int, default=6, help="Sample titles per author in the batch")
    ap.add_argument("--apply", dest="verdicts", help="A verdicts JSONL to expand into per-book insights")
    ap.add_argument("--out", required=True)
    ap.add_argument("--deferred", help="Roster of mixed/unknown authors to skip (default: beside --out)")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    if args.verdicts:
        return apply(args)
    if args.emit:
        return emit(args)
    ap.error("one of --emit or --apply is required")


if __name__ == "__main__":
    sys.exit(main())
