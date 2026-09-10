"""Infer AUDIENCE and ADULT-ROMANCE classification for novels, from evidence already in the library.

Why this exists
---------------
`/books/novels` seeds a default "not adult-romance" chip (`NOVELS_DEFAULT_EXCLUDE_TAG`), which hides only
books carrying an `ItemTag` whose value is exactly `adult-romance`. On 2026-09-06 that was 204 books out of
125,262, so the chip filtered nothing and the adult romance shelf was in front of everyone. The same gap makes
the maturity ceiling unusable: `MaturityFilter` reads `Insight.Maturity` for a book and HIDES anything without
one, so a kid account set to ceiling 0 would today see almost nothing at all.

Both are one missing thing: books have no insight rows. This script writes the ones the library can already
justify, from Calibre subjects, Open Library subjects (ISBN-keyed, via `books-isbn-enrich`) and the publisher.

What it deliberately does NOT do
--------------------------------
It classifies only what the evidence decides: erotica/adult romance, and children's/teen. A book whose
evidence says nothing about its audience gets NO row — leaving it unclassified, which is the fail-safe the
gate already implements, rather than a guess that would either expose it or hide it wrongly. Ordinary adult
fiction is not "mature" by default and is not claimed to be. That remainder is the model pass's job.

Everything it emits is `Rank 0` (`Transforms.ModelRank` of `calibre-tags` / `openlibrary`), so ANY later
model pass — haiku 1, sonnet 2, opus 3 — supersedes it without deleting it: `InsightCurrency` just stops
choosing it.

Output
------
  --out    JSONL for `books-isbn-enrich`'s sibling, `books-insight-import` (dry run by default; --apply writes)
  --report CSV of every decision, one row per book, with the evidence that drove it

Chunked and resumable per the house rule: `--after <itemId>` and `--limit`, progress printed per chunk, and
the JSONL is idempotent by SourceKey so a re-import after a kill inserts only what the kill lost.

Usage
-----
  python scripts/books/infer_book_tags.py --out data/books/v2/inferred-insights.jsonl \
      --report data/books/v2/inferred-insights.csv
  MovieTheater.BooksHost.exe books-insight-import --file <jsonl>            # dry run
  MovieTheater.BooksHost.exe books-insight-import --file <jsonl> --apply
  MovieTheater.BooksHost.exe books-resolve                                  # folds -> ItemTag(Source=AI)
"""
import argparse
import csv
import json
import os
import re
import sqlite3
import sys
from collections import Counter

DEFAULT_DB = r"F:\Work\MovieTheater\data\books\v2\books.db"
DEFAULT_LEGS = r"F:\Work\MovieTheater\data\books\v2\books-legs.db"

CALIBRE = 2   # TagSource.Calibre
BOOK = 1      # ItemKind.Book


# ── the vocabularies ────────────────────────────────────────────────────────────────────────────────
#
# Matched as SUBSTRINGS against the lowercased subject strings, so "Erotic stories; American" and
# "FICTION / Erotica" both hit "erotic".
#
# THE TWO LEGS ARE NOT EQUAL AND ARE NOT MERGED. A Calibre tag is the owner's own genre assignment on the
# owner's own shelf. An Open Library subject is a LIBRARY SUBJECT HEADING attached to whatever edition
# shares that ISBN, which is a different and much noisier thing: the first cut of these rules read a bare
# "Romance" heading as the romance shelf and classified The House of Mirth and The Picture of Dorian Gray
# as adult romance, read "Adult fiction" (which only means "not a children's book") as erotica and caught
# Ready Player One and Where the Red Fern Grows, and read a polluted heading list as children's fiction and
# put Upton Sinclair's The Jungle on the kids' shelf. So: Open Library alone may only CORROBORATE, never
# decide, and every rule below needs either an unambiguous marker or two independent signals.

# Unambiguous. "adult fiction" is deliberately NOT here — see above.
EROTICA = (
    "erotic", "erotica", "bdsm", "menage", "ménage", "sexually explicit", "explicit sexual",
)

# Imprints that publish ONLY erotica/adult romance. A dedicated imprint is the strongest signal there is:
# it is the shelf the book was literally sold on. General trade houses are excluded on purpose — Kensington
# and Avon publish cozy mysteries and thrillers too, and treating them as romance labelled Bake Sale Murder
# and All The Pretty Dead Girls as adult romance.
EROTICA_PUBLISHERS = (
    "ellora's cave", "elloras cave", "loose id", "changeling press", "siren-bookstrand",
    "siren publishing", "bookstrand", "black lace", "total-e-bound", "totally bound",
    "cleis", "red sage", "xcite", "harlequin blaze", "harlequin spice", "spice briefs",
)

# A genre assignment, not a subject heading: these name the romance SHELF and decide on their own.
STRONG_ROMANCE = (
    "historical romance", "paranormal romance", "contemporary romance", "regency romance",
    "romantic suspense", "erotic romance", "romance fiction", "category romance",
    "romance - ", "romance:", "romance &", "vampire romance", "supernatural romance",
    "fantasy romance", "science fiction romance", "sci fi romance", "gay romance",
)

# True of a great many books that are not the romance shelf, so these only ever corroborate.
WEAK_ROMANCE = ("romance", "love stories", "man-woman relationships", "chick lit")

# The category-romance houses. Unlike the general trade houses these publish essentially one thing.
ROMANCE_PUBLISHERS = ("harlequin", "silhouette", "mills & boon", "mills and boon")

# Romance for a different audience — vetoes the adult-romance tag rather than adding to it.
SWEET_ROMANCE = (
    "christian", "inspirational", "clean romance", "sweet romance", "amish",
    "religious fiction", "love inspired",
)

# Markers that mean a SMALL child, and nothing else. "Juvenile Fiction" and "Juvenile literature" are
# deliberately absent: in BISAC and in this library's Calibre tags they span ages 8–18 and are applied
# loosely, and reading them as maturity 0 put Virals, Infinite Days and The Wednesday Wars — and, through a
# stray tag, the Harlequin historical The Viking's Captive — on the children's shelf. They live in TEEN.
CHILDREN = (
    "children's fiction", "childrens fiction", "children's stories", "childrens stories",
    "picture book", "board book", "early reader", "beginning reader",
    "readers (primary)", "children's poetry", "nursery rhyme", "children's literature",
)

TEEN = (
    "young adult", "teenagers", "teenage", "high school students", "young adult fiction",
    "juvenile fiction", "juvenile literature", "juvenile nonfiction", "juvenile non-fiction",
)


def hit(blob, needles):
    """The needles present in the blob — the evidence, not just a boolean, so the report can show it."""
    return [n for n in needles if n in blob]


def classify(cal_subjects, ol_subjects, publisher):
    """(maturity, audience, genres, confidence, source, why) — or None when the evidence decides nothing.

    Undecided is the common and correct answer. `MaturityFilter` hides a book with no maturity below
    ceiling 3, so declining to classify costs nothing but a book staying invisible to a gated account,
    while a wrong verdict either exposes adult material or mislabels a novel permanently.
    """
    cal = " | ".join(s.lower() for s in cal_subjects if s)
    ol = " | ".join(s.lower() for s in ol_subjects if s)
    both = cal + " | " + ol
    pub = (publisher or "").lower()

    ero = hit(both, EROTICA)
    ero_pub = hit(pub, EROTICA_PUBLISHERS)
    strong_rom = hit(both, STRONG_ROMANCE)
    weak_cal = hit(cal, WEAK_ROMANCE)
    weak_ol = hit(ol, WEAK_ROMANCE)
    rom_pub = hit(pub, ROMANCE_PUBLISHERS)
    sweet = hit(both, SWEET_ROMANCE)
    kid_cal = hit(cal, CHILDREN)
    kid_ol = hit(ol, CHILDREN)
    teen_any = hit(both, TEEN)

    # Which leg is doing the work becomes the ModelId, so a verdict always records its provenance.
    def src(from_calibre):
        return "calibre-tags" if from_calibre else "openlibrary"

    # 1. Explicit adult material. Either marker is unambiguous on its own.
    if ero or ero_pub:
        romance_flavoured = bool(strong_rom or weak_cal or weak_ol or rom_pub or ero_pub)
        genres = ["adult-romance", "erotica"] if romance_flavoured else ["erotica"]
        conf = "High" if (ero_pub or hit(cal, EROTICA)) else "Medium"
        return 3, "adult", genres, conf, src(bool(hit(cal, EROTICA)) or bool(ero_pub)), ero + ero_pub

    # 2. Adult romance — the shelf the default "not adult-romance" chip exists to hide. It needs a genre
    #    assignment, not a subject heading: a named romance sub-genre, a category-romance house, or the
    #    owner's own Calibre "Romance" tag corroborated by Open Library saying the same thing.
    adult_romance = None
    if strong_rom:
        adult_romance = ("Medium", strong_rom, src(bool(hit(cal, STRONG_ROMANCE))))
    elif rom_pub and (weak_cal or weak_ol or strong_rom):
        adult_romance = ("Medium", rom_pub + weak_cal + weak_ol, "calibre-tags")
    elif weak_cal and weak_ol:
        adult_romance = ("Low", weak_cal + weak_ol, "calibre-tags")

    if adult_romance and not (kid_cal or teen_any):
        conf, why, source = adult_romance
        if sweet:
            # Same genre, different audience — inspirational and sweet lines are not the adult shelf.
            return 1, "teen", ["romance"], "Low", source, why + sweet
        return 2, "mature", ["adult-romance", "romance"], conf, source, why

    # 3. A children's marker. THIS PASS NEVER RETURNS MATURITY 0.
    #
    #    Maturity 0 plus an allow-listed `audience: children` tag is what puts a book on /kids, so a false
    #    positive here is the worst outcome this script can produce — and the evidence cannot carry it. With
    #    the strictest rule that still matched anything, the whole library yielded eight books, of which The
    #    Land of Laughs (an adult dark-fantasy novel) and The Ultimate Harry Potter and Philosophy (an adult
    #    essay collection) were two: Calibre's "children's stories" tag is applied to books ABOUT children's
    #    fiction as readily as to children's fiction. Eight rows are not worth that failure mode.
    #
    #    So a children's marker earns the same verdict as a teen one — "not adult", which the evidence does
    #    support. Genuine kid classification is the model pass's job; until then /kids keeps exactly the
    #    books a model already cleared.
    if kid_cal and not (ero or ero_pub or strong_rom or rom_pub or weak_cal):
        return 1, "teen", [], "Low", "calibre-tags", kid_cal + kid_ol

    # 4. Teen.
    if teen_any and not (ero or ero_pub):
        genres = ["romance"] if (strong_rom or weak_cal) else []
        return 1, "teen", genres, "Medium" if hit(cal, TEEN) else "Low", src(bool(hit(cal, TEEN))), teen_any

    # 5. A children's marker that only Open Library asserts. Not enough to call it kid-safe (that is how
    #    The Jungle got there), but enough to say it is not adult: teen is the cautious middle.
    if kid_ol and not (ero or ero_pub or strong_rom or rom_pub):
        return 1, "teen", [], "Low", "openlibrary", kid_ol

    return None


def norm_isbn(s):
    if not s:
        return None
    t = re.sub(r"[^0-9Xx]", "", s).upper()
    return t if len(t) in (10, 13) else None


def load_openlibrary(legs_path):
    """{normalized ISBN -> [subjects]} from the warehouse. One pass; the table is one row per ISBN."""
    out = {}
    if not os.path.isfile(legs_path):
        return out
    con = sqlite3.connect(f"file:{legs_path}?mode=ro", uri=True, timeout=60)
    try:
        for isbn, subs in con.execute(
                "SELECT Isbn, SubjectsJson FROM OpenLibraryEdition WHERE SubjectsJson IS NOT NULL"):
            key = norm_isbn(isbn)
            if not key:
                continue
            try:
                out[key] = json.loads(subs) or []
            except (ValueError, TypeError):
                continue
    finally:
        con.close()
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--legs", default=DEFAULT_LEGS)
    ap.add_argument("--out", required=True, help="JSONL for books-insight-import")
    ap.add_argument("--report", help="CSV of every decision and its evidence")
    ap.add_argument("--after", type=int, default=0, help="Resume: only Item.Id greater than this")
    ap.add_argument("--limit", type=int, default=0, help="Stop after this many books examined (0 = all)")
    ap.add_argument("--chunk", type=int, default=5000, help="Books per progress line")
    ap.add_argument("--include-tagged", action="store_true",
                    help="Also emit for books that already have a current insight (default: skip them)")
    args = ap.parse_args()

    ol = load_openlibrary(args.legs)
    print(f"open library editions: {len(ol)}", flush=True)

    con = sqlite3.connect(f"file:{args.db}?mode=ro", uri=True, timeout=60)
    con.execute("PRAGMA temp_store=MEMORY")

    # ANY insight row, not a CURRENT one: the importer writes IsCurrent = 0 and only books-resolve stamps
    # currency, so testing currency would re-emit everything applied since the last resolve.
    where_tagged = "" if args.include_tagged else (
        " AND NOT EXISTS(SELECT 1 FROM Insight n WHERE n.SubjectKind=0 AND n.SubjectId=i.Id)")
    rows = con.execute(f"""
        SELECT i.Id, i.ResolvedTitle, b.Isbn, b.Publisher
        FROM Item i LEFT JOIN BookDetail b ON b.ItemId = i.Id
        WHERE i.Kind = {BOOK} AND i.IsExcluded = 0 AND i.Id > ?{where_tagged}
        ORDER BY i.Id""", (args.after,))

    # Calibre subjects, pulled once into memory: 130k rows keyed by item, which is far cheaper than a
    # correlated subquery per book.
    cal = {}
    for iid, val in con.execute(f"SELECT t.ItemId, t.Value FROM ItemTag t WHERE t.Source = {CALIBRE}"):
        cal.setdefault(iid, []).append(val)
    print(f"calibre-tagged books: {len(cal)}", flush=True)

    verdicts = Counter()
    examined = written = 0
    last_id = args.after

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
    report = None
    if args.report:
        report = csv.writer(open(args.report, "w", newline="", encoding="utf-8"))
        report.writerow(["itemId", "title", "maturity", "audience", "genres", "confidence", "source", "evidence"])

    with open(args.out, "w", encoding="utf-8") as out:
        for iid, title, isbn, publisher in rows:
            examined += 1
            last_id = iid

            cal_subjects = cal.get(iid, [])
            ol_subjects = ol.get(norm_isbn(isbn), []) if isbn else []

            verdict = classify(cal_subjects, ol_subjects, publisher)
            if verdict is None:
                verdicts["undecided"] += 1
            else:
                maturity, audience, genres, confidence, source, why = verdict
                verdicts[f"{maturity} {audience}"] += 1
                tags = {"audience": [audience]}
                if genres:
                    tags["genre"] = genres
                out.write(json.dumps({
                    "subject": "book",
                    "id": iid,
                    "model": source,
                    "confidence": confidence,
                    "maturity": maturity,
                    "tags": tags,
                }, ensure_ascii=False) + "\n")
                written += 1
                if report:
                    report.writerow([iid, title or "", maturity, audience, "|".join(genres),
                                     confidence, source, "; ".join(why)])

            if examined % args.chunk == 0:
                print(f"{{ examined: {examined}, written: {written}, nextAfter: {last_id} }}", flush=True)
            if args.limit and examined >= args.limit:
                break

    print()
    print(f"done: examined {examined}, wrote {written} insights -> {args.out}")
    print(f"resume with --after {last_id}")
    print()
    for k, v in sorted(verdicts.items(), key=lambda kv: -kv[1]):
        print(f"  {v:8d}  {k}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
