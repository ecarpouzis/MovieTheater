"""Diff the share inventory (inventory_walk.py) against books.db root 1. Read-only.

Writes into <run>/:
  unchanged.tsv   path matches, size + mtime match                       (no work)
  inplace.tsv     path matches, size or mtime differ  -> same book, new bytes
  vanished.tsv    DB item (not excluded) whose path is gone              -> must be explained
  back.tsv        DB item marked excluded whose path exists again
  new.tsv         file on the share with no DB item at that path         -> pair or genuinely new
  skipped.tsv     files whose extension the scanner ignores (counted only)
usage: python diff_inventory.py --run data/books/rescan/20261002 [--db data/books/v2/books.db]
"""
import argparse, csv, datetime, os, sqlite3, collections

EXT = {".cbz", ".cbr", ".zip", ".rar", ".pdf", ".epub", ".mobi", ".azw3", ".cb7", ".7z"}


def utc(ts):
    return datetime.datetime.fromtimestamp(int(ts), datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--db", default="data/books/v2/books.db")
    a = ap.parse_args()

    inv = {}
    with open(os.path.join(a.run, "inventory.tsv"), encoding="utf-8") as f:
        for line in f:
            p, s, m = line.rstrip("\n").split("\t")
            inv[p] = (int(s), int(m))          # last line per path wins
    files, skipped = {}, collections.Counter()
    for p, v in inv.items():
        ext = os.path.splitext(p)[1].lower()
        if ext in EXT:
            files[p] = v
        else:
            skipped[ext] += 1

    c = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    rows = c.execute("""select i.Id, i.Path, i.FileSize, i.FileModifiedAt, i.IsExcluded, coalesce(s.ExclusionReason,''),
                               coalesce(s.BrokenReason,'')
                        from Item i left join ItemState s on s.ItemId = i.Id where i.RootId = 1""").fetchall()
    db = {}
    for r in rows:
        db.setdefault(r[1], []).append(r)
    dupes = {p: v for p, v in db.items() if len(v) > 1}

    # Case-insensitive view of the share (Windows/SMB paths are case-insensitive; the DB index is binary).
    lower = {}
    for p in files:
        lower.setdefault(p.lower(), p)

    out = {k: open(os.path.join(a.run, k + ".tsv"), "w", encoding="utf-8", newline="") for k in
           ("unchanged", "inplace", "vanished", "back", "new", "casechange")}
    w = {k: csv.writer(v, delimiter="\t", lineterminator="\n") for k, v in out.items()}
    w["inplace"].writerow(["ItemId", "Path", "DbSize", "DiskSize", "DbMtime", "DiskMtime"])
    w["vanished"].writerow(["ItemId", "Path", "DbSize", "DbMtime"])
    w["back"].writerow(["ItemId", "Path", "Reason", "DbSize", "DiskSize"])
    w["new"].writerow(["Path", "Size", "Mtime"])
    w["casechange"].writerow(["ItemId", "DbPath", "DiskPath"])
    claimed = set()
    n = collections.Counter()
    for path, rs in db.items():
        r = rs[0]
        iid, _, size, mt, excl, reason, broken = r
        disk = files.get(path)
        dpath = path
        if disk is None:
            alt = lower.get(path.lower())
            if alt is not None:
                disk, dpath = files[alt], alt
                w["casechange"].writerow([iid, path, alt]); n["casechange"] += 1
        if disk is None:
            if not excl:
                w["vanished"].writerow([iid, path, size, mt]); n["vanished"] += 1
            else:
                n["excluded_and_gone"] += 1
            continue
        claimed.add(dpath)
        if excl:
            w["back"].writerow([iid, path, reason or broken, size, disk[0]]); n["back"] += 1
            continue
        if disk[0] == size and utc(disk[1]) == mt:
            w["unchanged"].writerow([iid]); n["unchanged"] += 1
        else:
            w["inplace"].writerow([iid, path, size, disk[0], mt, utc(disk[1])]); n["inplace"] += 1
    for p, (s, m) in sorted(files.items()):
        if p not in claimed:
            w["new"].writerow([p, s, utc(m)]); n["new"] += 1
    for v in out.values():
        v.close()
    print(dict(n), "db_items", len(rows), "db_dupe_paths", len(dupes), "share_files", len(files),
          "skipped_ext", dict(skipped.most_common(15)))


if __name__ == "__main__":
    main()
