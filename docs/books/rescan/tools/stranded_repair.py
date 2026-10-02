"""Repair: shelves the first resolver fix created for keys whose items ALREADY sat on a curated shelf.

The 2026-10-02 SeriesRebuildJob change created a Series row for every live parsed key with no alias. Some such
keys are not new: their items are "stranded" on a curated shelf (Repoint never touches an item whose key has no
alias, so they keep the shelf the identity pass gave them). Creating a row for such a key gave it an alias, and the
next Repoint pulled those items off their curated shelf. This finds every such key by comparing with the backup
taken immediately BEFORE the faulty resolve, restores each pre-existing item's SeriesId, re-points new items with
that key to the restored majority shelf, and deletes the wrongly created Series rows (only rows created after the
backup, and only once nothing references them). Dry run by default; --apply writes, with a JSON undo journal.
usage: python stranded_repair.py --backup data/books/v2/backup-20261002-064600/books.db --first-new-id 245531 [--apply]
"""
import argparse, collections, json, os, sqlite3, time

ap = argparse.ArgumentParser()
ap.add_argument("--db", default="data/books/v2/books.db"); ap.add_argument("--backup", required=True)
ap.add_argument("--first-new-id", type=int, required=True); ap.add_argument("--apply", action="store_true")
ap.add_argument("--journal", default="data/books/rescan/20261002/stranded-repair-undo.json")
a = ap.parse_args()
bak = sqlite3.connect(f"file:{a.backup}?mode=ro", uri=True)
live = sqlite3.connect(a.db if a.apply else f"file:{a.db}?mode=ro", uri=True)
max_sid_before = bak.execute("select max(Id) from Series").fetchone()[0]
before = dict(bak.execute("select Id, SeriesId from Item where RootId=1"))
rows = live.execute("""select i.Id, i.SeriesId, d.ParsedSeriesKey from Item i join ComicDetail d on d.ItemId=i.Id
                       where i.RootId=1 and i.SeriesId > ?""", (max_sid_before,)).fetchall()
by_series = collections.defaultdict(list)
for iid, sid, key in rows: by_series[sid].append((iid, key))
bad, restore, repoint = [], {}, {}
for sid, items in by_series.items():
    old = [(i, k) for i, k in items if i < a.first_new_id and before.get(i) is not None]
    if not old: continue                                   # a genuinely new shelf: every item on it is new
    bad.append(sid)
    for i, _ in old: restore[i] = before[i]
    shelf = collections.Counter(before[i] for i, _ in old).most_common(1)[0][0]
    for i, _ in items:
        if i >= a.first_new_id or before.get(i) is None: repoint[i] = shelf
refs = {}
for t, col in [("CollectedEditionSpan", "SeriesId"), ("ContainmentFlag", "SeriesId"), ("ReadingOrderEntry", "SeriesId"),
               ("SeriesMerge", "NewSeriesId"), ("SeriesMerge", "OldSeriesId"), ("GroupMark", "GroupKey")]:
    try:
        n = live.execute(f"select count(*) from {t} where cast({col} as integer) in ({','.join(map(str, bad)) or '0'})").fetchone()[0]
        refs[f"{t}.{col}"] = n
    except sqlite3.OperationalError as e:
        refs[f"{t}.{col}"] = f"n/a ({e})"
print({"series_created_after_backup_holding_preexisting_items": len(bad), "items_restored": len(restore),
       "new_items_repointed": len(repoint), "references_to_bad_series": refs})
for sid in bad[:12]:
    nm = live.execute("select Name, ParsedKey from Series where Id=?", (sid,)).fetchone()
    tgt = collections.Counter(restore[i] for i, _ in by_series[sid] if i in restore).most_common(1)[0][0]
    tn = live.execute("select Name from Series where Id=?", (tgt,)).fetchone()
    print(f"  S{sid} {nm} -> back to S{tgt} {tn}  ({len(by_series[sid])} items)")
missing_targets = {s for s in set(restore.values()) | set(repoint.values()) if live.execute("select 1 from Series where Id=?", (s,)).fetchone() is None}
print("restore targets that no longer exist:", sorted(missing_targets)[:20], len(missing_targets))
if a.apply:
    if missing_targets: raise SystemExit("refusing: some restore targets were deleted; resolve that first")
    journal = {"at": time.strftime("%Y-%m-%d %H:%M:%S"), "items": {i: live.execute("select SeriesId from Item where Id=?", (i,)).fetchone()[0] for i in list(restore) + list(repoint)},
               "series": [dict(zip(["Id", "CanonicalKey", "ParsedKey", "Name"], live.execute("select Id, CanonicalKey, ParsedKey, Name from Series where Id=?", (s,)).fetchone())) for s in bad]}
    os.makedirs(os.path.dirname(a.journal), exist_ok=True)
    json.dump(journal, open(a.journal, "w", encoding="utf-8"), indent=1)
    cur = live.cursor(); cur.execute("BEGIN")
    for i, s in list(restore.items()) + list(repoint.items()): cur.execute("update Item set SeriesId=? where Id=?", (s, i))
    cur.execute(f"delete from SeriesAlias where SeriesId in ({','.join(map(str, bad))})")
    cur.execute(f"delete from SeriesMerge where NewSeriesId in ({','.join(map(str, bad))}) or OldSeriesId in ({','.join(map(str, bad))})")
    left = cur.execute(f"select count(*) from Item where SeriesId in ({','.join(map(str, bad))})").fetchone()[0]
    if left: cur.execute("ROLLBACK"); raise SystemExit(f"refusing: {left} items still on the bad shelves")
    cur.execute(f"delete from Series where Id in ({','.join(map(str, bad))})")
    cur.execute("COMMIT")
    print("applied; journal", a.journal)
