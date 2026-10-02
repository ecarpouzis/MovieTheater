"""Expand reading verdicts + mechanical pairs into the two decision files, and refuse to proceed on any gap.

Inputs (in <run>):  pairs.tsv (pair.py), review_packet.txt (group -> items), verdicts.txt (the reading),
                    overrides.txt (optional: ItemId verdict | path | why — replaces a pairs.tsv row).
Outputs:
  relocate.tsv  ItemId, NewPath, Class, Proof, SameSize   -> books-relocate (the item keeps its id)
  replaced.tsv  ItemId, OldPath, Verdict, Target, TargetItem, Why  -> the record: contained / edition / gone
Checks (exit 1 on any): every vanished item has exactly one decision; every target exists on the share;
no new file is the relocation target of two items; a `move` whose sizes differ is reported (kept as version).
usage: python expand.py --run data/books/rescan/20261002
"""
import argparse, csv, os, re, sqlite3, sys, collections

P = "\\\\Library\\Public\\5 - Comics\\"


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--db", default="data/books/v2/books.db")
    a = ap.parse_args(); R = lambda n: os.path.join(a.run, n)
    van = {int(r["ItemId"]): r for r in csv.DictReader(open(R("vanished.tsv"), encoding="utf-8"), delimiter="\t")}
    inv = {}
    for line in open(R("inventory.tsv"), encoding="utf-8"):
        p, s, m = line.rstrip("\n").split("\t"); inv[p[len(P):] if p.startswith(P) else p] = int(s)
    inv_l = {k.lower(): k for k in inv}
    c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    held = {r[1][len(P):].lower(): r[0] for r in c.execute("select Id, Path from Item where RootId=1 and IsExcluded=0")}

    groups, cur = {}, None
    for line in open(R("review_packet.txt"), encoding="utf-8"):
        m = re.match(r"### (G\d{4})", line)
        if m: cur = m.group(1); groups[cur] = []; continue
        m = re.match(r"  - (\d+)  ", line)
        if m and cur: groups[cur].append(int(m.group(1)))

    dec, errs = {}, []

    def put(iid, verdict, path, why, src):
        if iid not in van: errs.append(f"{src}: item {iid} is not a vanished item"); return
        if iid in dec: errs.append(f"{src}: item {iid} decided twice ({dec[iid][3]} and {src})"); return
        dec[iid] = (verdict, path, why, src)

    over = {}
    if os.path.exists(R("overrides.txt")):
        for line in open(R("overrides.txt"), encoding="utf-8"):
            if not line.strip() or line.startswith("#"): continue
            head, path, why = [x.strip() for x in line.rstrip("\n").split(" | ", 2)]
            iid, verdict = head.split(" ", 1); over[int(iid)] = (verdict.strip(), path, why)
    for r in csv.DictReader(open(R("pairs.tsv"), encoding="utf-8"), delimiter="\t"):
        iid = int(r["ItemId"])
        if iid in over: put(iid, *over.pop(iid), "override"); continue
        put(iid, {"move": "move", "rename": "move", "replace": "version", "fixed": "version", "version": "version"}[r["Class"]],
            r["NewPath"], f"{r['Class']} ({r['Proof']})", "pairs")
    for iid, v in over.items(): put(iid, *v, "override")

    for n, line in enumerate(open(R("verdicts.txt"), encoding="utf-8"), 1):
        if not line.strip() or line.startswith("#"): continue
        parts = [x.strip() for x in line.rstrip("\n").split(" | ", 2)]
        if len(parts) != 3: errs.append(f"verdicts.txt:{n}: malformed"); continue
        head, path, why = parts
        key, verdict = head.split(" ", 1)
        verdict = verdict.strip()
        if verdict not in ("move", "version", "edition", "contained", "contained?", "gone"):
            errs.append(f"verdicts.txt:{n}: unknown verdict {verdict}"); continue
        ids = groups.get(key) if key.startswith("G") else [int(key)]
        if ids is None: errs.append(f"verdicts.txt:{n}: unknown group {key}"); continue
        for iid in ids:
            put(iid, verdict, path, why, f"verdicts.txt:{n}")

    missing = sorted(set(van) - set(dec))
    for iid in missing: errs.append(f"no decision for vanished item {iid}: {van[iid]['Path']}")

    reloc, repl, claimed = [], [], collections.defaultdict(list)
    for iid, (verdict, path, why, src) in sorted(dec.items()):
        old = van[iid]["Path"][len(P):]
        if verdict in ("move", "version"):
            real = inv_l.get(path.lower())
            if real is None: errs.append(f"{src}: target of {iid} not on the share: {path}"); continue
            same = int(van[iid]["DbSize"]) == inv[real]
            if verdict == "move" and not same: why += " [size differs: treated as version]"
            claimed[real.lower()].append(iid)
            reloc.append({"ItemId": iid, "NewPath": real, "Class": verdict, "Proof": why, "SameSize": int(same)})
        else:
            tgt, titem = "", ""
            if verdict != "gone":
                real = inv_l.get(path.lower())
                if real is None: errs.append(f"{src}: target of {iid} not on the share: {path}"); continue
                tgt, titem = real, held.get(real.lower(), "")
            repl.append({"ItemId": iid, "OldPath": old, "Verdict": verdict, "Target": tgt, "TargetItem": titem, "Why": why})
    for p, ids in claimed.items():
        if len(ids) > 1: errs.append(f"new file claimed by {len(ids)} items {ids}: {p}")

    def w(name, rows, cols):
        with open(R(name), "w", encoding="utf-8", newline="") as f:
            ww = csv.DictWriter(f, cols, delimiter="\t", lineterminator="\n"); ww.writeheader(); ww.writerows(rows)
    w("relocate.tsv", reloc, ["ItemId", "NewPath", "Class", "Proof", "SameSize"])
    w("replaced.tsv", repl, ["ItemId", "OldPath", "Verdict", "Target", "TargetItem", "Why"])
    print({"vanished": len(van), "decided": len(dec), "relocate": len(reloc),
           "relocate_same_bytes": sum(r["SameSize"] for r in reloc),
           "replaced": collections.Counter(r["Verdict"] for r in repl), "errors": len(errs)})
    for e in errs[:60]: print("ERR", e)
    sys.exit(1 if errs else 0)


if __name__ == "__main__":
    main()
