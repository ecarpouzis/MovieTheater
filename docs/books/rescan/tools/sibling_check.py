"""After the scan: does each NEW comic item carry the same run key as the existing books in its folder?

The identity pass re-keyed runs by hand (books-series-split writes ComicDetail.ParsedSeriesKey). A new file the
parser keys from its filename lands on the parser's shelf, not on the curated run its siblings were moved to.
This lists, per new item: its parsed key, the dominant curated key of its PRE-EXISTING folder siblings, whether
the new file's name reads as the same run as those siblings (same comicname series), and a proposal.
Read-only. Output: <run>/sibling_check.tsv and <run>/split_proposals.jsonl (only the strict cases:
siblings ≥ 80% on one key, the new file names the same run as ≥ 1 sibling, and its own key differs).
usage: python sibling_check.py --run data/books/rescan/20261002 --first-new-id 245531
"""
import argparse, collections, csv, json, os, sqlite3, sys
sys.path.insert(0, os.path.dirname(__file__))
import comicname as cn


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--db", default="data/books/v2/books.db")
    ap.add_argument("--first-new-id", type=int, required=True)
    a = ap.parse_args()
    c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    rows = c.execute("""select i.Id, i.FolderId, i.FileName, d.ParsedSeriesKey, coalesce(d.IsCollection,0), i.SeriesId
                        from Item i join ComicDetail d on d.ItemId=i.Id where i.RootId=1 and i.IsExcluded=0""").fetchall()
    by_folder = collections.defaultdict(list)
    for r in rows: by_folder[r[1]].append(r)
    out, props, stats = [], [], collections.Counter()
    for fid, items in by_folder.items():
        new = [r for r in items if r[0] >= a.first_new_id]
        if not new: continue
        old = [r for r in items if r[0] < a.first_new_id]
        if not old:
            stats["new_folder"] += len(new)
            for r in new: out.append([r[0], r[2], r[3], "", "", "", "new-folder"])
            continue
        keys = collections.Counter(r[3] for r in old)
        top, n = keys.most_common(1)[0]
        share = n / len(old)
        old_series = {cn.parse(r[2])["series"] for r in old if r[3] == top}
        for r in new:
            same_run = cn.parse(r[2])["series"] in old_series
            if r[3] == top: verdict = "same-key"
            elif share >= 0.8 and same_run: verdict = "PROPOSE"
            elif same_run: verdict = "differs-mixed-folder"
            else: verdict = "differs-other-title"
            stats[verdict] += 1
            out.append([r[0], r[2], r[3], top, f"{share:.2f}", int(same_run), verdict])
            if verdict == "PROPOSE":
                props.append({"itemId": r[0], "key": top, "why": f"folder siblings {n}/{len(old)} carry '{top}'; same run by name"})
    with open(os.path.join(a.run, "sibling_check.tsv"), "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f, delimiter="\t", lineterminator="\n")
        w.writerow(["ItemId", "FileName", "ParsedKey", "SiblingKey", "SiblingShare", "SameRunByName", "Verdict"]); w.writerows(out)
    with open(os.path.join(a.run, "split_proposals.jsonl"), "w", encoding="utf-8") as f:
        for p in props: f.write(json.dumps(p) + "\n")
    print(dict(stats), "proposals", len(props))


if __name__ == "__main__":
    main()
