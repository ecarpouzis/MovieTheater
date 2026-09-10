"""Fill the maturity gap: books whose CURRENT insight carries tags but no `Maturity`.

The author and per-book passes both key off "has no insight row at all", so a book that the
deterministic legs (calibre-tags, openlibrary, epub-jacket) already tagged never entered either
queue — even though those legs write genres and no maturity. That left 1,614 books with shelves and
no audience rating, invisible to the queue and unrated for the kids ceiling.

This closes it the same way the author pass does: an author-level verdict expands over that author's
books in the gap set, with the same kid-clearance guard (maturity 0 needs High confidence) and the
same ADULT_TAGS block (a book's own adult tag outranks a verdict that would clear it for children).

  python scripts/books/nullmat_apply.py --emit  --out nullmat-authors.json
  python scripts/books/nullmat_apply.py --apply nullmat-verdicts-01.jsonl --out nullmat-insights-01.jsonl
"""
import argparse
import json
import os
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tag_vocab import canon_genres, derived_tags
from model_pass_authors import DEFAULT_DB, AUDIENCES, CONFIDENCES, ADULT_TAGS, connect

MODEL_ID = "claude-opus-5-author"   # same producer as the author pass; rank 3

GAP = """n.SubjectKind = 0 AND n.IsCurrent = 1 AND n.Maturity IS NULL"""


def rows(con):
    return con.execute(f"""
        SELECT i.Id, COALESCE(i.ResolvedCreatorsCsv,''), i.ResolvedTitle
        FROM Insight n JOIN Item i ON i.Id = n.SubjectId
        WHERE {GAP}""").fetchall()


def emit(args):
    con = connect(args.db)
    by = defaultdict(list)
    for iid, author, title in rows(con):
        by[author.strip()].append((iid, title))
    ordered = sorted(by.items(), key=lambda kv: (-len(kv[1]), kv[0]))
    out = {"books": sum(len(v) for v in by.values()), "authors": len(ordered),
           "authors_list": [{"author": a, "n": len(v), "samples": [t for _, t in v[:2]]}
                            for a, v in ordered]}
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    print(f"{{ books: {out['books']}, authors: {out['authors']} }}")
    return 0


def apply(args):
    verdicts = {}
    abstained = 0
    with open(args.verdicts, encoding="utf-8") as f:
        for line_no, line in enumerate(f, 1):
            line = line.strip()
            if not line or line.startswith("//"):
                continue
            v = json.loads(line)
            author = (v.get("author") or "").strip()
            if not author or v.get("verdict") in ("unknown", "mixed"):
                abstained += 1
                continue
            audience, maturity = v.get("audience"), v.get("maturity")
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
            if maturity == 0 and confidence != "High":
                print(f"  {author}: maturity 0 at {confidence} confidence — demoted to teen",
                      file=sys.stderr)
                maturity, audience = 1, "teen"
            verdicts[author] = {"maturity": maturity, "audience": audience,
                                "confidence": confidence,
                                "genres": [g for g in (v.get("genres") or []) if isinstance(g, str)]}

    con = connect(args.db)
    adult_tagged = set()
    q = ",".join("?" for _ in ADULT_TAGS)
    for (iid,) in con.execute(f"SELECT DISTINCT ItemId FROM ItemTag WHERE Value IN ({q})", ADULT_TAGS):
        adult_tagged.add(iid)

    written = blocked = 0
    with open(args.out, "w", encoding="utf-8") as out:
        for iid, author, _ in rows(con):
            v = verdicts.get(author.strip())
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
            out.write(json.dumps({"subject": "book", "id": iid, "model": MODEL_ID,
                                  "confidence": v["confidence"], "maturity": v["maturity"],
                                  "tags": tags}, ensure_ascii=False) + "\n")
            written += 1
    print(f"{{ authorsJudged: {len(verdicts)}, abstained: {abstained}, "
          f"booksWritten: {written}, blockedByOwnTags: {blocked} }}")
    print(f"wrote {args.out}")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--emit", action="store_true")
    ap.add_argument("--apply", dest="verdicts")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    return apply(args) if args.verdicts else emit(args)


if __name__ == "__main__":
    sys.exit(main())
