"""The identity pass's UNDECIDED shelves (file-holding comic shelves no decision file decides), as a revisit sheet
for `next_batch.py --revisit-file`, ordered by each shelf's dominant folder so neighbours share a batch. Read-only.
usage: python undecided_sheet.py --out docs/books/rescan/20261002/undecided.tsv
"""
import argparse, collections, os, sqlite3, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "identity", "tools"))
import idbase

ap = argparse.ArgumentParser(); ap.add_argument("--out", required=True)
a = ap.parse_args()
decides, winner, superseded, dupes = idbase.scan_decisions()
con = idbase.open_hot()
folders = collections.defaultdict(collections.Counter)
for sid, path in con.execute(f"""SELECT i.SeriesId, i.Path FROM Item i JOIN Series s ON s.Id = i.SeriesId
        WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL AND s.CanonicalKey NOT LIKE 'book:%'"""):
    folders[sid][idbase.short_path(os.path.dirname(path or ""))] += 1
und = sorted((c.most_common(1)[0][0], sid) for sid, c in folders.items() if sid not in winner)
with open(a.out, "w", encoding="utf-8", newline="") as f:
    f.write("# undecided after the 2026-10-02 comics rescan: new runs / books no decision file has read\n")
    for folder, sid in und:
        f.write(f"S{sid}\t{folder}\n")
print("undecided shelves:", len(und))
