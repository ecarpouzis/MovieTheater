"""Explain every vanished item by a file that is on the share now. Read-only; writes proposals.

Every removal is presumed a REPLACEMENT (project ruling, 2026-10-02): a MOVE (same bytes elsewhere), a NEW
VERSION of the same issue (another rip, an (F) fix, a rename), or a COLLECTION that now holds it. This tool
PROPOSES; the stronger proofs are mechanical, the weaker ones are written for reading (Proof column says which).

Stages, strongest first; a new file is claimed once:
  A  move      same file name (case-folded) + same size                    proof: name+size
  A2 rename    same size + same series|issue key, different name           proof: size+issue
  -- folder map learned from A/A2 votes (old dir -> new dir) --
  B  replace   same file name, different size, in the mapped folder        proof: name+folder
  C  fixed     names equal once (F)/(fixed)/(F2) tags are removed          proof: fixtag+folder
  D  version   same series|issue key (or same tag-free stem) in the mapped folder   READ
  E  contained an issue whose run's new collection sits in the mapped folder         READ
  unexplained  nothing found                                                READ
usage: python pair.py --run data/books/rescan/20261002
"""
import argparse, csv, os, re, sqlite3, collections, sys
sys.path.insert(0, os.path.dirname(__file__))
import comicname as cn

P = "\\\\Library\\Public\\5 - Comics\\"
FIX = re.compile(r"\s*[\(\[]\s*(?:f\d?|fixed|fix)\s*[\)\]]", re.I)


def fold(s):
    return cn._fold(s)


def rel(p):
    return p[len(P):] if p.startswith(P) else p


def leaf_key(d):
    # a folder's identity for correspondence: folded leaf name without a trailing year group
    leaf = d.rsplit("\\", 1)[-1]
    return fold(re.sub(r"\(\s*\d{4}[^)]*\)", "", leaf))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--db", default="data/books/v2/books.db")
    a = ap.parse_args()
    rd = lambda n: list(csv.DictReader(open(os.path.join(a.run, n), encoding="utf-8"), delimiter="\t"))
    van, new = rd("vanished.tsv"), rd("new.tsv")

    c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    det = {r[0]: r[1:] for r in c.execute(
        "select ItemId, IssueNo, IsCollection, ParsedSeriesKey, Format from ComicDetail")}
    ser = {r[0]: r[1] for r in c.execute("select i.Id, coalesce(s.Name,'') from Item i left join Series s on s.Id=i.SeriesId where i.RootId=1")}

    for v in van:
        v["id"] = int(v["ItemId"]); v["name"] = os.path.basename(v["Path"]); v["dir"] = os.path.dirname(v["Path"])
        v["p"] = cn.parse(v["name"]); v["size"] = int(v["DbSize"])
    for n in new:
        n["name"] = os.path.basename(n["Path"]); n["dir"] = os.path.dirname(n["Path"])
        n["p"] = cn.parse(n["name"]); n["size"] = int(n["Size"]); n["claimed"] = False

    by_name = collections.defaultdict(list); by_size = collections.defaultdict(list)
    by_dir = collections.defaultdict(list)
    for n in new:
        by_name[n["name"].lower()].append(n); by_size[n["size"]].append(n); by_dir[n["dir"]].append(n)

    pairs, done = [], set()

    def claim(v, n, cls, proof, note=""):
        n["claimed"] = True; done.add(v["id"])
        pairs.append({"ItemId": v["id"], "Class": cls, "Proof": proof, "OldPath": rel(v["Path"]), "NewPath": rel(n["Path"]),
                      "OldSize": v["size"], "NewSize": n["size"], "Shelf": ser.get(v["id"], ""), "Note": note})

    def pick(v, cands):
        cands = [n for n in cands if not n["claimed"]]
        if len(cands) <= 1:
            return cands[0] if cands else None, 0
        # prefer the candidate whose folder leaf matches the old folder's leaf, then the longest common path suffix
        lk = leaf_key(v["dir"])
        same = [n for n in cands if leaf_key(n["dir"]) == lk]
        if len(same) == 1:
            return same[0], 0
        return None, len(cands)

    amb = []
    # A: name + size
    for v in van:
        n, k = pick(v, [x for x in by_name[v["name"].lower()] if x["size"] == v["size"]])
        if n: claim(v, n, "move", "name+size")
        elif k: amb.append((v, "A", k))
    # A2: size + issue key
    for v in van:
        if v["id"] in done: continue
        n, k = pick(v, [x for x in by_size[v["size"]] if x["p"]["k_alias"] == v["p"]["k_alias"]
                        or x["p"]["k_tight"] == v["p"]["k_tight"]])
        if n: claim(v, n, "rename", "size+issue")
        elif k: amb.append((v, "A2", k))

    # folder map from votes
    votes = collections.defaultdict(collections.Counter)
    for pr in pairs:
        votes[os.path.dirname(P + pr["OldPath"])][os.path.dirname(P + pr["NewPath"])] += 1
    dmap = {d: c.most_common(1)[0][0] for d, c in votes.items()}
    # parent-level map: an old parent whose children moved to children of one new parent
    pvotes = collections.defaultdict(collections.Counter)
    for o, nn in dmap.items():
        pvotes[os.path.dirname(o)][os.path.dirname(nn)] += 1
    pmap = {d: c.most_common(1)[0][0] for d, c in pvotes.items()}
    newdirs_by_leaf = collections.defaultdict(set)
    for n in new:
        newdirs_by_leaf[leaf_key(n["dir"])].add(n["dir"])

    def targets(v):
        d = v["dir"]; out = []
        if d in dmap: out.append(dmap[d])
        if d in by_dir: out.append(d)                      # the old folder itself still receives files
        par = os.path.dirname(d)
        if par in pmap:
            cand = os.path.join(pmap[par], os.path.basename(d))
            if cand in by_dir: out.append(cand)
            for nd in newdirs_by_leaf.get(leaf_key(d), ()):
                if os.path.dirname(nd) == pmap[par]: out.append(nd)
        if not out:
            out.extend(newdirs_by_leaf.get(leaf_key(d), ()))
        seen = []
        [seen.append(x) for x in out if x not in seen]
        return seen

    def infolders(v):
        return [n for t in targets(v) for n in by_dir.get(t, []) if not n["claimed"]]

    # B: same name, different size, mapped folder (or unique name anywhere)
    for v in van:
        if v["id"] in done: continue
        same = [n for n in infolders(v) if n["name"].lower() == v["name"].lower()]
        if len(same) == 1: claim(v, same[0], "replace", "name+folder"); continue
        anyw = [n for n in by_name[v["name"].lower()] if not n["claimed"]]
        if len(anyw) == 1 and leaf_key(anyw[0]["dir"]) == leaf_key(v["dir"]):
            claim(v, anyw[0], "replace", "name+leaf")
    # C: fix tags
    def unfix(s): return FIX.sub("", s).lower()
    for v in van:
        if v["id"] in done: continue
        same = [n for n in infolders(v) if unfix(n["name"]) == unfix(v["name"])]
        if len(same) == 1: claim(v, same[0], "fixed", "fixtag+folder")
    # D: same issue key / stem in mapped folder
    for v in van:
        if v["id"] in done: continue
        cands = infolders(v)
        p = v["p"]
        hit = [n for n in cands if p["issue"] and (n["p"]["k_alias"] == p["k_alias"] or n["p"]["k_tight"] == p["k_tight"])]
        if not hit and not p["issue"]:
            hit = [n for n in cands if n["p"]["k_stem"] == p["k_stem"]]
        if len(hit) == 1: claim(v, hit[0], "version", "READ:issue+folder")
        elif len(hit) > 1:
            # several rips of the same issue arrived: prefer an (F)/digital one deterministically? no -- read it
            amb.append((v, "D", len(hit)))

    # E: contained -- a new collection of the same run in the mapped folder
    contained = []
    for v in van:
        if v["id"] in done: continue
        cands = [n for n in infolders(v) if n["p"]["is_collection"]]
        if cands:
            contained.append({"ItemId": v["id"], "OldPath": rel(v["Path"]), "Shelf": ser.get(v["id"], ""),
                              "IssueNo": (det.get(v["id"]) or ("",))[0],
                              "Candidates": " || ".join(rel(n["Path"]) for n in cands[:8]), "N": len(cands)})
            done.add(v["id"])

    unexplained = [v for v in van if v["id"] not in done]
    ambd = {v["id"]: (s, k) for v, s, k in amb}

    def w(name, rows, cols):
        with open(os.path.join(a.run, name), "w", encoding="utf-8", newline="") as f:
            ww = csv.DictWriter(f, cols, delimiter="\t", lineterminator="\n", extrasaction="ignore")
            ww.writeheader(); ww.writerows(rows)
    w("pairs.tsv", pairs, ["ItemId", "Class", "Proof", "OldPath", "NewPath", "OldSize", "NewSize", "Shelf", "Note"])
    w("contained.tsv", contained, ["ItemId", "OldPath", "Shelf", "IssueNo", "N", "Candidates"])
    w("unexplained.tsv", [{"ItemId": v["id"], "OldPath": rel(v["Path"]), "Size": v["size"], "Shelf": ser.get(v["id"], ""),
                           "Ambiguous": "%s:%s" % ambd[v["id"]] if v["id"] in ambd else "",
                           "Targets": " | ".join(rel(t) for t in targets(v))} for v in unexplained],
      ["ItemId", "OldPath", "Size", "Shelf", "Ambiguous", "Targets"])
    w("newfiles.tsv", [{"Path": rel(n["Path"]), "Size": n["size"], "Coll": int(n["p"]["is_collection"])} for n in new if not n["claimed"]],
      ["Path", "Size", "Coll"])
    print({"pairs": collections.Counter(p["Class"] for p in pairs), "contained": len(contained),
           "unexplained": len(unexplained), "ambiguous": len(amb), "new_unclaimed": sum(1 for n in new if not n["claimed"])})


if __name__ == "__main__":
    main()
