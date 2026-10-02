"""Containment decision lines for items the rescan marked MISSING (replaced) — comment them out, keep the record.

pass2 refuses a decision for an item that is no longer a live collection on the shelf. A replaced book is still
a row (marked, never deleted), so its line is not wrong — it is about a file that is gone. The line is kept
verbatim behind a `# [rescan 2026-10-02 missing: …]` prefix naming what replaced it (from replaced.tsv), so the
judged range survives as evidence for the replacing book. Only items in replaced.tsv AND excluded with reason
'missing' are touched. Dry run by default.
usage: python retire_missing_lines.py --run data/books/rescan/20261002 [--apply]
"""
import argparse, csv, os, re, sqlite3

HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.normpath(os.path.join(HERE, "..", "..", "containment", "decisions"))
ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--db", default="data/books/v2/books.db")
ap.add_argument("--apply", action="store_true")
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
missing = {r[0] for r in c.execute("""select i.Id from Item i join ItemState s on s.ItemId=i.Id
                                      where i.IsExcluded=1 and s.BrokenReason='missing'""")}
repl = {int(r["ItemId"]): r for r in csv.DictReader(open(os.path.join(a.run, "replaced.tsv"), encoding="utf-8"), delimiter="\t")}
reloc = {int(r["ItemId"]): r for r in csv.DictReader(open(os.path.join(a.run, "relocate.tsv"), encoding="utf-8"), delimiter="\t")}
n_files = n_lines = 0
for f in sorted(os.listdir(DEC)):
    if not re.match(r"^S\d+\.txt$", f): continue
    path = os.path.join(DEC, f)
    lines = open(path, encoding="utf-8").read().split("\n")
    changed = False
    for k, line in enumerate(lines):
        m = re.match(r"^([Su]) (\d+) ", line)
        if not m: continue
        iid = int(m.group(2))
        if iid not in missing or iid not in repl: continue
        r = repl[iid]
        what = f"{r['Verdict']} -> {os.path.basename(r['Target'])}" if r["Target"] else r["Verdict"]
        lines[k] = f"# [rescan 2026-10-02 missing: {what}] {line}"
        changed = True; n_lines += 1
        print(f"{f}: {line[:90]}")
    if changed:
        n_files += 1
        if a.apply: open(path, "w", encoding="utf-8").write("\n".join(lines))
print({"files": n_files, "lines": n_lines, "applied": a.apply})
