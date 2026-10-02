import csv, os, sys
run, *classes = sys.argv[1:]
for r in csv.DictReader(open(os.path.join(run, "pairs.tsv"), encoding="utf-8"), delimiter="\t"):
    if r["Class"] in classes:
        print(f"{r['Class'][:3]} {r['ItemId']} | {r['OldPath']}\n      => {r['NewPath']}  [{r['OldSize']} -> {r['NewSize']}]")
