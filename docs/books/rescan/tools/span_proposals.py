"""Judged ranges for the collections that replaced issues — from the reading verdicts, AFTER the scan indexed them.

For every `contained` verdict whose target is now an item: gather the replaced items' issue numbers (their
curated ComicDetail.IssueNo — the identity pass's numbering, not a re-parse), group by target, and propose the
range min..max when the replaced issues are CONTIGUOUS on the run and all sat on one shelf. Anything else (gaps,
several shelves, `contained?`, editions, non-numeric numbers) is listed for reading and never proposed.
A proposed range is a LOWER bound of what the book holds (it collects at least the issues it replaced), so it is
written only when the reading cited the book's full range (`#a-b` in the why) and that range equals the replaced
span — otherwise the line is held back for reading.
Output: <run>/span_proposals.jsonl (books-curated-spans-import input) + <run>/span_review.tsv.
usage: python span_proposals.py --run data/books/rescan/20261002
"""
import argparse, collections, csv, json, os, re, sqlite3

P = "\\\\Library\\Public\\5 - Comics\\"


def num(s):
    try: return float(s)
    except (TypeError, ValueError): return None


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--db", default="data/books/v2/books.db")
    ap.add_argument("--batch", default="rescan-20261002"); ap.add_argument("--confidence", type=float, default=0.85)
    a = ap.parse_args()
    c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    rows = list(csv.DictReader(open(os.path.join(a.run, "replaced.tsv"), encoding="utf-8"), delimiter="\t"))
    path_item = {r[0][len(P):].lower(): (r[1], r[2]) for r in c.execute("select Path, Id, SeriesId from Item where RootId=1 and IsExcluded=0")}
    by_target = collections.defaultdict(list)
    for r in rows:
        if r["Verdict"] in ("contained", "contained?"): by_target[r["Target"]].append(r)
    # A collection a containment decision file already decides (S or u) is that file's to range: pass2 re-imports
    # every file on each landing and its `u` would RETRACT a span written here. Those are left to the file.
    dec_dir = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "containment", "decisions"))
    in_files = set()
    for fn in os.listdir(dec_dir):
        if re.match(r"^S\d+\.txt$", fn):
            in_files |= {int(x) for x in re.findall(r"^[Su] (\d+)", open(os.path.join(dec_dir, fn), encoding="utf-8").read(), re.M)}
    props, review = [], []
    for tgt, rs in sorted(by_target.items()):
        hit = path_item.get(tgt.lower())
        if hit and int(hit[0]) in in_files:
            review.append([tgt, hit[0], len(rs), "", "decided by its shelf's containment decision file"]); continue
        ids = [int(r["ItemId"]) for r in rs]
        q = f"select d.ItemId, d.IssueNo, i.SeriesId from ComicDetail d join Item i on i.Id=d.ItemId where d.ItemId in ({','.join('?'*len(ids))})"
        facts = c.execute(q, ids).fetchall()
        nums = sorted(n for n in (num(f[1]) for f in facts) if n is not None)
        shelves = {f[2] for f in facts}
        why = rs[0]["Why"]
        m = re.search(r"#(\d+(?:\.\d+)?)\s*-\s*(\d+(?:\.\d+)?)", why)
        cited = (float(m.group(1)), float(m.group(2))) if m else None
        reason = None
        if hit is None: reason = "target not indexed (yet)"
        elif any(r["Verdict"] == "contained?" for r in rs): reason = "contained? (range unverified)"
        elif len(nums) != len(facts) or not nums: reason = "non-numeric issue numbers"
        elif len(shelves) != 1: reason = f"replaced issues sat on {len(shelves)} shelves"
        elif any(b - a_ > 1 for a_, b in zip(nums, nums[1:])): reason = "gap in the replaced issues"
        elif not cited: reason = "reading did not cite the book's full range"
        elif cited != (nums[0], nums[-1]): reason = f"cited #{cited[0]:g}-{cited[1]:g} != replaced #{nums[0]:g}-{nums[-1]:g}"
        if reason:
            review.append([tgt, hit[0] if hit else "", len(rs), f"{nums[0]:g}-{nums[-1]:g}" if nums else "", reason])
            continue
        props.append({"itemId": int(hit[0]), "start": nums[0], "end": nums[-1], "editionTitle": os.path.basename(tgt),
                      "confidence": a.confidence, "batch": a.batch,
                      "rationale": f"rescan: replaced our #{nums[0]:g}-{nums[-1]:g} on shelf S{next(iter(shelves))}; {why}"})
    with open(os.path.join(a.run, "span_proposals.jsonl"), "w", encoding="utf-8") as f:
        for p in props: f.write(json.dumps(p, ensure_ascii=False) + "\n")
    with open(os.path.join(a.run, "span_review.tsv"), "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f, delimiter="\t", lineterminator="\n"); w.writerow(["Target", "ItemId", "Replaced", "Range", "HeldBecause"]); w.writerows(review)
    print({"targets": len(by_target), "proposed": len(props), "held": len(review),
           "held_by_reason": collections.Counter(r[4].split(" (")[0].split(" #")[0] for r in review)})


if __name__ == "__main__":
    main()
