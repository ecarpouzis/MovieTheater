"""Reading packet for every vanished item pair.py did not explain mechanically (contained + unexplained).

Grouped by the OLD folder. For each group: the vanished files (with DB facts) and every still-unclaimed new
file whose folded folder-leaf + name contains all significant words of the group's series (or that sits in a
folder pair.py mapped). The reader writes one verdict line per vanished item into verdicts/*.tsv:
    ItemId <TAB> verdict <TAB> new path (relative) <TAB> why
verdict = move | version | contained | gone
usage: python review_packet.py --run data/books/rescan/20261002 [--max-cands 25]
"""
import argparse, csv, os, re, sqlite3, collections, sys
sys.path.insert(0, os.path.dirname(__file__))
import comicname as cn

P = "\\\\Library\\Public\\5 - Comics\\"
STOP = {"the", "a", "an", "of", "and", "vol", "v", "volume", "book", "digital", "comics", "collection", "edition"}


def words(s):
    return [w for w in cn._fold(s).split() if w not in STOP and not re.fullmatch(r"\d{4}|\d{1,3}", w)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--db", default="data/books/v2/books.db")
    ap.add_argument("--max-cands", type=int, default=30)
    a = ap.parse_args()
    rd = lambda n: list(csv.DictReader(open(os.path.join(a.run, n), encoding="utf-8"), delimiter="\t"))
    todo = [(r["ItemId"], r["OldPath"]) for r in rd("contained.tsv")] + [(r["ItemId"], r["OldPath"]) for r in rd("unexplained.tsv")]
    new = rd("newfiles.tsv")
    c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    facts = {}
    for iid, _ in todo:
        r = c.execute("""select i.FileSize, i.PageCount, d.IssueNo, d.IsCollection, d.Format, coalesce(s.Name,''), i.SeriesId
                         from Item i left join ComicDetail d on d.ItemId=i.Id left join Series s on s.Id=i.SeriesId where i.Id=?""", (iid,)).fetchone()
        facts[iid] = r
    for n in new:
        d, f = os.path.split(n["Path"])
        n["hay"] = set(cn._fold(os.path.basename(d) + " " + os.path.dirname(d).split("\\")[0] + " " + f).split())
        n["top"] = n["Path"].split("\\")[0]
    groups = collections.defaultdict(list)
    for iid, op in todo:
        groups[os.path.dirname(op)].append((iid, op))

    out = open(os.path.join(a.run, "review_packet.txt"), "w", encoding="utf-8")
    for gi, (d, items) in enumerate(sorted(groups.items())):
        sw = collections.Counter()
        for _, op in items:
            sw.update(set(words(cn.parse(os.path.basename(op))["series"])))
        leafw = words(os.path.basename(d))
        key = [w for w, k in sw.items() if k >= max(1, len(items) // 2)] or leafw
        top = d.split("\\")[0]
        cands = [n for n in new if key and all(w in n["hay"] for w in key)]
        if not cands and leafw:
            cands = [n for n in new if all(w in n["hay"] for w in leafw)]
        cands.sort(key=lambda n: (n["top"] != top, n["Path"]))
        out.write(f"\n### G{gi:04d}  OLD DIR: {d}   (key: {' '.join(key)})\n")
        for iid, op in items:
            f = facts[iid]
            out.write(f"  - {iid}  {os.path.basename(op)}  [{int(f[0])/2**20:.0f}MB {f[1]}pp #{f[2]} coll={f[3]} fmt={f[4]} shelf='{f[5]}' S{f[6]}]\n")
        for n in cands[: a.max_cands]:
            out.write(f"    + {n['Path']}  [{int(n['Size'])/2**20:.0f}MB coll={n['Coll']}]\n")
        if len(cands) > a.max_cands:
            out.write(f"    + ... {len(cands) - a.max_cands} more candidates\n")
        if not cands:
            out.write("    (no candidate on the share)\n")
    out.close()
    print("groups", len(groups), "items", len(todo))


if __name__ == "__main__":
    main()
