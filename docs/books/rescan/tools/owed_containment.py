"""Which containment decision files owe a line after the rescan, and the evidence for each owed edition.

pass2 stops at the first file with an undecided edition; this lists them all (read-only), with each edition's
file name, page count, and — from the rescan's replaced.tsv — the old issues it replaced (their curated issue
numbers and why the reading said so), plus the shelf's existing S ranges for the volume pattern.
usage: python owed_containment.py --run data/books/rescan/20261002
"""
import argparse, collections, csv, os, re, sqlite3

HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.normpath(os.path.join(HERE, "..", "..", "containment", "decisions"))
P = "\\\\Library\\Public\\5 - Comics\\"

ap = argparse.ArgumentParser(); ap.add_argument("--run", required=True); ap.add_argument("--db", default="data/books/v2/books.db")
a = ap.parse_args()
c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
coll = collections.defaultdict(dict)
for sid, iid, fn, pp in c.execute("""SELECT i.SeriesId, i.Id, i.FileName, i.PageCount FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
        WHERE cd.IsCollection=1 AND coalesce(i.IsExcluded,0)=0 AND i.SeriesId IS NOT NULL"""):
    coll[sid][iid] = (fn, pp)
path_of = {r[0]: r[1][len(P):].lower() for r in c.execute("select Id, Path from Item where RootId=1")}
repl = collections.defaultdict(list)
for r in csv.DictReader(open(os.path.join(a.run, "replaced.tsv"), encoding="utf-8"), delimiter="\t"):
    if r["Target"]: repl[r["Target"].lower()].append(r)
issue = {r[0]: r[1] for r in c.execute("select ItemId, IssueNo from ComicDetail")}
out = []
for f in sorted(os.listdir(DEC)):
    m = re.match(r"^S(\d+)\.txt$", f)
    if not m: continue
    sid = int(m.group(1)); want = coll.get(sid)
    if not want: continue
    decided, ranges = set(), []
    for raw in open(os.path.join(DEC, f), encoding="utf-8"):
        line = raw.strip()
        if not line or line.startswith("#"): continue
        kind, rest = line.split(None, 1)
        if kind == "S":
            head = rest.split("|")[0].split(); decided.add(int(head[0])); ranges.append(f"{head[0]} #{head[1]}-{head[2]} ({want.get(int(head[0]), ('?',))[0][:50]})")
        elif kind == "u": decided.add(int(rest.split()[0]))
        elif kind == "U": decided |= set(want)
    owed = sorted(set(want) - decided)
    if not owed: continue
    out.append(f"\n### S{sid}  ({len(owed)} owed)  existing ranges: {'; '.join(ranges[:8])}{' …' if len(ranges) > 8 else ''}")
    for iid in owed:
        fn, pp = want[iid]
        rs = repl.get(path_of.get(iid, ""), [])
        ev = ""
        if rs:
            nums = [issue.get(int(r["ItemId"])) for r in rs]
            ev = f"  REPLACED {len(rs)}: #{','.join(str(n) for n in nums)} [{rs[0]['Verdict']}] {rs[0]['Why'][:110]}"
        out.append(f"  - {iid}  {fn}  [{pp}pp]{ev}")
open(os.path.join(a.run, "owed_containment.txt"), "w", encoding="utf-8").write("\n".join(out))
print(sum(1 for l in out if l.startswith("\n###")), "files owe lines;", sum(1 for l in out if l.startswith("  - ")), "editions")
