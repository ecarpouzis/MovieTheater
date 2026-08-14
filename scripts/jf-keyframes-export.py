# Bank Jellyfin's KeyframeData corpus to disk — the second copy of the 25 TB backfill's results.
#
# Why this exists (2026-08-13): KeyframeData rows carry ON DELETE CASCADE against BaseItems, so a
# renamed folder — or any library cleanup — physically DELETES the keyframe lists for every item
# whose path died, before sync-jellyfin ever runs. jellyfin.db was the only copy; a stock Jellyfin
# reinstall or db reset would have erased the entire four-day backfill (backfill-marathon skill).
# This export is read-only against the live service (SQLite URI mode=ro) and lands on F:\, a
# different disk from Jellyfin's C:\ProgramData home.
#
# Run standalone or from extract-jf-keyframes-nightly.ps1 (which appends it after each night's
# extractions so new stamps are banked within a day). Rotation keeps the newest KEEP files.
#
# Each line: {itemId, totalDuration, ticks, path, size}. Path+size ride along so a restore can
# rebind lists by content identity (filename+size — the key the sync's move detection trusts)
# after the item ids themselves have died with their paths.
import glob
import gzip
import json
import os
import sqlite3
import time

OUT_DIR = r"F:\Work\MovieTheater\data\jf-keyframes-backup"
KEEP = 14

os.makedirs(OUT_DIR, exist_ok=True)
out_path = os.path.join(OUT_DIR, f"keyframedata-{time.strftime('%Y%m%d-%H%M%S')}.jsonl.gz")

src = sqlite3.connect("file:C:/ProgramData/Jellyfin/Server/data/jellyfin.db?mode=ro", uri=True)
total = src.execute("SELECT COUNT(*) FROM KeyframeData").fetchone()[0]
rows = src.execute(
    """
    SELECT k.ItemId, k.TotalDuration, k.KeyframeTicks, b.Path, b.Size
    FROM KeyframeData k
    LEFT JOIN BaseItems b ON b.Id = k.ItemId
    """
)

written = 0
with gzip.open(out_path, "wt", encoding="utf-8") as out:
    for item_id, duration, ticks, path, size in rows:
        out.write(json.dumps({
            "itemId": item_id,
            "totalDuration": duration,
            "ticks": ticks,
            "path": path,
            "size": size,
        }) + "\n")
        written += 1

print(f"jf-keyframes-export: {written}/{total} rows -> {out_path} ({os.path.getsize(out_path):,} bytes)")

# Rotate oldest-first; never touch anything that is not one of ours.
backups = sorted(glob.glob(os.path.join(OUT_DIR, "keyframedata-*.jsonl.gz")))
for stale in backups[:-KEEP]:
    os.remove(stale)
    print(f"jf-keyframes-export: rotated out {os.path.basename(stale)}")
