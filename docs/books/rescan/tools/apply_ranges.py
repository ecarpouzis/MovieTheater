"""Write read ranges into the containment decision files: each `u <item> …` refusal for a listed item becomes
`S <item> <start> <end> <conf> | <title> | <evidence>` — one line swapped, nothing else touched. An item with no `u`
line in its shelf's file (or already ranged by an `S` line) is reported and left alone. Dry run by default.
input TSV (header): ItemId  Start  End  Conf  Evidence
usage: python apply_ranges.py --in ranges.tsv [--apply] [--spans-out spans.jsonl]
"""
import argparse, csv, os, re, sqlite3

HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.normpath(os.path.join(HERE, "..", "..", "containment", "decisions"))
ap = argparse.ArgumentParser(); ap.add_argument("--in", dest="inp", required=True); ap.add_argument("--apply", action="store_true")
ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--spans-out")
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
todo = list(csv.DictReader(open(a.inp, encoding="utf-8"), delimiter="\t"))
by_file, missing = {}, []
for r in todo:
    iid = int(r["ItemId"])
    sid, fn = c.execute("SELECT SeriesId, FileName FROM Item WHERE Id=?", (iid,)).fetchone()
    by_file.setdefault(os.path.join(DEC, f"S{sid}.txt"), []).append((iid, fn, r))
done = 0
for path, items in by_file.items():
    if not os.path.isfile(path):
        missing += [(i, "no decision file " + os.path.basename(path)) for i, _f, _r in items]; continue
    lines = open(path, encoding="utf-8").read().split("\n")
    changed = False
    for iid, fn, r in items:
        k = next((n for n, l in enumerate(lines) if re.match(rf"^u {iid}\b", l)), None)
        if k is None:
            has_s = any(re.match(rf"^S {iid}\b", l) for l in lines)
            missing.append((iid, "already ranged" if has_s else "no u line in " + os.path.basename(path))); continue
        s, e = float(r["Start"]), float(r["End"])
        title = re.sub(r"\.(cbz|cbr|zip|rar|pdf)$", "", fn, flags=re.I)
        lines[k] = f"S {iid} {s:g} {e:g} {r['Conf']} | {title} | {r['Evidence']} (2026-10-02 rescan)"
        changed = True; done += 1
        print(f"{os.path.basename(path)}  {iid}  #{s:g}-{e:g}  {title[:70]}")
    if changed and a.apply:
        open(path, "w", encoding="utf-8").write("\n".join(lines))
for iid, why in missing: print(f"SKIP {iid}: {why}")
# a shelf with NO decision file is ranged by a curated span line instead (books-curated-spans-import), the lane the
# rescan's earlier ranges took; an item whose file exists but holds no `u` line is never routed there.
if a.spans_out:
    import json
    nofile = {i for i, why in missing if why.startswith("no decision file")}
    with open(a.spans_out, "w", encoding="utf-8") as f:
        for r in todo:
            iid = int(r["ItemId"])
            if iid not in nofile: continue
            fn = c.execute("SELECT FileName FROM Item WHERE Id=?", (iid,)).fetchone()[0]
            f.write(json.dumps({"itemId": iid, "start": float(r["Start"]), "end": float(r["End"]),
                                "editionTitle": re.sub(r"\.(cbz|cbr|zip|rar|pdf)$", "", fn, flags=re.I),
                                "confidence": float(r["Conf"]), "batch": "rescan-20261002",
                                "rationale": "rescan: " + r["Evidence"]}) + "\n")
    print(len(nofile), "span line(s) ->", a.spans_out)
print({"ranged": done, "skipped": len(missing)}, "applied" if a.apply else "(dry run)")
