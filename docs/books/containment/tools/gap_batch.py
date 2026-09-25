"""Cut the next containment-gap batch from reports/gap-20260925.tsv, and mark batches landed.

`python gap_batch.py next [--editions 150]`   take OPEN shelves in worklist order until the edition budget is spent,
                                               mark them BATCHED:G-NNN, write reports/gap-batches/G-NNN.ids, print it
`python gap_batch.py done G-NNN`              mark that batch's shelves DONE:G-NNN (after its wave_fix landed green)
`python gap_batch.py status`                   {open, batched, done} counts — the progress report

Bounded (one batch per call), resumable (status lives in the worklist file), idempotent (a second `next` never
re-hands a BATCHED shelf; `done` on a done batch changes nothing). Read-only on the database.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
WL = os.path.join(HERE, os.pardir, "reports", "gap-20260925.tsv")
BD = os.path.join(HERE, os.pardir, "reports", "gap-batches")


def load():
    head, rows = [], []
    for line in open(WL, encoding="utf-8").read().splitlines():
        (head if line.startswith("#") else rows).append(line if line.startswith("#") else line.split("\t"))
    return head, rows


def save(head, rows):
    with open(WL, "w", encoding="utf-8") as f:
        f.write("\n".join(head + ["\t".join(r) for r in rows]) + "\n")


def status(rows):
    c = {"open": 0, "batched": 0, "done": 0}
    for r in rows:
        s = r[6]
        c["open" if s == "OPEN" else "batched" if s.startswith("BATCHED") else "done"] += 1
    return c


def main(argv):
    head, rows = load()
    if not argv or argv[0] == "status":
        print(status(rows))
        return
    if argv[0] == "next":
        budget = int(argv[argv.index("--editions") + 1]) if "--editions" in argv else 150
        os.makedirs(BD, exist_ok=True)
        n = 1 + max([int(r[6].split("G-")[1]) for r in rows if "G-" in r[6]] or [0])
        name = f"G-{n:03d}"
        take, spent = [], 0
        for r in rows:
            if r[6] != "OPEN":
                continue
            eds = int(r[3])
            if take and spent + eds > budget:
                continue
            take.append(r)
            spent += eds
            if spent >= budget:
                break
        if not take:
            print({"batch": None, **status(rows)})
            return
        for r in take:
            r[6] = f"BATCHED:{name}"
        save(head, rows)
        with open(os.path.join(BD, name + ".ids"), "w", encoding="utf-8") as f:
            f.write("\n".join("\t".join(r[:6]) for r in take) + "\n")
        print({"batch": name, "shelves": len(take), "editions": spent, **status(rows)})
        return
    if argv[0] == "done":
        name = argv[1]
        for r in rows:
            if r[6] == f"BATCHED:{name}":
                r[6] = f"DONE:{name}"
        save(head, rows)
        print(status(rows))
        return
    raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
