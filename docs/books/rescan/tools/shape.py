import csv, collections, os, sys
run = sys.argv[1]
P = "\\\\Library\\Public\\5 - Comics\\"
def rd(n): return list(csv.DictReader(open(os.path.join(run, n), encoding="utf-8"), delimiter="\t"))
van, new, back = rd("vanished.tsv"), rd("new.tsv"), rd("back.tsv")
def top(p, k=1): return "\\".join(p[len(P):].split("\\")[:k])
for name, rows in (("vanished", van), ("new", new)):
    c = collections.Counter(top(r["Path"], 2) for r in rows)
    print("==", name, len(rows)); [print(f"  {v:5} {k}") for k, v in c.most_common(45)]
print("== back reasons", collections.Counter(r["Reason"][:30] for r in back).most_common(8))
vn = collections.Counter(os.path.basename(r["Path"]).lower() for r in van)
nn = {}
for r in new: nn.setdefault(os.path.basename(r["Path"]).lower(), []).append(r)
exact = sum(1 for r in van if os.path.basename(r["Path"]).lower() in nn)
print("vanished whose filename exists among new:", exact)
size = collections.defaultdict(list)
for r in new: size[r["Size"]].append(r)
print("vanished whose exact size exists among new:", sum(1 for r in van if r["DbSize"] in size))
