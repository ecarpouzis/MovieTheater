"""R0 step 3: baseline counts of the frozen v1 database + the live cache directory.

Writes data/books/census/baseline.json and docs/books/v1-baseline-counts.md. User activity is
reported by user id only. Compares against the independently measured numbers from planning and
prints any mismatch (never rewrites the expectations).
"""
from __future__ import annotations
import datetime as dt, json, os
from collections import Counter
from common import frozen_db, ro, tables, CENSUS, DOCS, LIVE_CACHE, dump_json

EXPECTED = {
    "tables": 85, "indexes_named": 60, "indexes_total": 87, "Comics": 141010, "comics_cat0": 118926, "comics_cat1": 22084,
    "Series": 19481, "Folders": 54418, "Bookmarks": 766, "GroupUserMetadata": 68, "ComicUserLists": 4,
    "cache_webp": 140983, "cache_f_jpg": 109, "cache_archives": 47,
}


def main():
    db = frozen_db()
    con = ro(db)
    out = {"db": str(db), "taken_utc": dt.datetime.now(dt.timezone.utc).isoformat()}
    rows = {}
    for t in tables(con):
        rows[t] = con.execute(f'SELECT count(*) FROM "{t}"').fetchone()[0]
    out["rows"] = rows
    out["tables"] = len(rows)
    idx = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'")]
    out["indexes_named"] = len(idx)
    out["indexes_total"] = con.execute("SELECT count(*) FROM sqlite_master WHERE type='index'").fetchone()[0]
    out["indexes"] = sorted(idx)
    out["fts_docs"] = con.execute("SELECT count(*) FROM ComicFts").fetchone()[0]
    out["comics_by_category"] = {str(k): v for k, v in con.execute("SELECT Category, count(*) FROM Comics GROUP BY Category").fetchall()}
    out["comics_by_ext"] = dict(con.execute("SELECT lower(FileExtension), count(*) FROM Comics GROUP BY 1").fetchall())
    out["comics_broken"] = con.execute("SELECT count(*) FROM Comics WHERE IsBroken=1").fetchone()[0]
    out["comics_excluded"] = con.execute("SELECT count(*) FROM Comics WHERE ExcludedFromLibrary=1").fetchone()[0]
    out["comics_keep_in_directory"] = con.execute("SELECT count(*) FROM Comics WHERE KeepInDirectory=1").fetchone()[0]
    out["comics_with_comicinfo"] = con.execute(
        "SELECT count(*) FROM Comics WHERE MetadataVersion IS NOT NULL OR (SeriesName IS NOT NULL AND SeriesName<>'')").fetchone()[0]
    acl = Counter()
    for (j,) in con.execute("SELECT AuthorizedUsersJson FROM Folders"):
        try:
            n = len(json.loads(j)) if j else 0
        except Exception:
            n = -1
        acl[str(n)] += 1
    out["folder_acl_by_list_length"] = dict(acl)
    out["folder_acl_distinct_lists"] = con.execute("SELECT count(DISTINCT AuthorizedUsersJson) FROM Folders").fetchone()[0]
    out["folders_depth_roots"] = con.execute("SELECT count(*) FROM Folders WHERE ParentId IS NULL").fetchone()[0]
    users = {r[0]: r[1] for r in con.execute("SELECT Username, Id FROM Users")}

    def by_user(table):
        c = Counter()
        for (u, n) in con.execute(f"SELECT Username, count(*) FROM {table} GROUP BY Username"):
            c[str(users.get(u, "unknown"))] += n
        return dict(c)

    out["users"] = {"count": len(users), "admins": con.execute("SELECT count(*) FROM Users WHERE IsAdmin=1").fetchone()[0],
                    "below_ceiling_3": con.execute("SELECT count(*) FROM Users WHERE MaxMaturity<3").fetchone()[0]}
    out["activity_by_user_id"] = {t: by_user(t) for t in ("Bookmarks", "ComicUserLists", "GroupUserMetadata", "SeriesUserLists", "Sessions")}
    out["bookmarks_status"] = {str(k): v for k, v in con.execute("SELECT Status, count(*) FROM Bookmarks GROUP BY Status").fetchall()}
    out["bookmarks_hidden"] = con.execute("SELECT count(*) FROM Bookmarks WHERE HiddenFromHistory=1").fetchone()[0]
    out["group_metadata_types"] = [dict(r) for r in con.execute(
        "SELECT GroupType, IsFavorite, IsRead, WantToRead, count(*) AS n FROM GroupUserMetadata GROUP BY 1,2,3,4")]
    out["system_state"] = dict(con.execute("SELECT Key, Value FROM SystemState").fetchall())
    out["series_stats"] = {
        "with_cv_volume": con.execute("SELECT count(*) FROM Series WHERE ComicvineVolumeId IS NOT NULL").fetchone()[0],
        "with_external_work": con.execute("SELECT count(*) FROM Series WHERE ExternalWorkId IS NOT NULL").fetchone()[0],
        "single_issue": con.execute("SELECT count(*) FROM Series WHERE IssueCount=1").fetchone()[0],
        "with_override": con.execute("SELECT count(*) FROM Series WHERE DisplayNameOverride IS NOT NULL AND DisplayNameOverride<>''").fetchone()[0],
        "with_franchise": con.execute("SELECT count(*) FROM Series WHERE Franchise IS NOT NULL AND Franchise<>''").fetchone()[0],
    }
    out["match_status"] = {
        "ComicvineMatches": [dict(r) for r in con.execute("SELECT Status, Applied, count(*) AS n FROM ComicvineMatches GROUP BY 1,2")],
        "LocgMatches": [dict(r) for r in con.execute("SELECT Status, MatchQuality, count(*) AS n FROM LocgMatches GROUP BY 1,2")],
        "GcdMatches": [dict(r) for r in con.execute("SELECT Status, count(*) AS n FROM GcdMatches GROUP BY 1")],
        "ComicvineSeriesLinks": [dict(r) for r in con.execute("SELECT Status, count(*) AS n FROM ComicvineSeriesLinks GROUP BY 1")],
        "ExternalSeriesLinks": [dict(r) for r in con.execute("SELECT Status, count(*) AS n FROM ExternalSeriesLinks GROUP BY 1")],
        "MangaUpdatesMatches": [dict(r) for r in con.execute("SELECT Status, count(*) AS n FROM MangaUpdatesMatches GROUP BY 1")],
    }
    out["claude"] = {
        "series_meta_by_confidence": dict(con.execute("SELECT Confidence, count(*) FROM ClaudeSeriesMetadata GROUP BY 1").fetchall()),
        "series_meta_referenced_by_a_series": con.execute(
            "SELECT count(*) FROM ClaudeSeriesMetadata m WHERE EXISTS (SELECT 1 FROM ComicParsedDetails pd WHERE pd.ClaudeSeriesMetadataId=m.Id AND pd.SeriesId IS NOT NULL)").fetchone()[0],
        "series_meta_unreferenced": con.execute(
            "SELECT count(*) FROM ClaudeSeriesMetadata m WHERE NOT EXISTS (SELECT 1 FROM ComicParsedDetails pd WHERE pd.ClaudeSeriesMetadataId=m.Id)").fetchone()[0],
        "series_ids_with_multiple_meta": con.execute(
            "SELECT count(*) FROM (SELECT pd.SeriesId FROM ComicParsedDetails pd WHERE pd.ClaudeSeriesMetadataId IS NOT NULL AND pd.SeriesId IS NOT NULL GROUP BY pd.SeriesId HAVING count(DISTINCT pd.ClaudeSeriesMetadataId)>1)").fetchone()[0],
        "comics_with_meta": con.execute("SELECT count(*) FROM ComicParsedDetails WHERE ClaudeSeriesMetadataId IS NOT NULL").fetchone()[0],
        "series_tag_categories": dict(con.execute("SELECT Category, count(*) FROM ClaudeSeriesTags GROUP BY 1").fetchall()),
    }
    out["db_bytes"] = db.stat().st_size
    con.close()
    webp = fjpg = other = 0
    with os.scandir(LIVE_CACHE) as it:
        for e in it:
            if e.is_file():
                n = e.name.lower()
                if n.endswith(".webp"):
                    webp += 1
                elif n.startswith("f_") and n.endswith(".jpg"):
                    fjpg += 1
                else:
                    other += 1
    arch = LIVE_CACHE / "archives"
    arch_files = [e for e in os.scandir(arch) if e.is_file()] if arch.exists() else []
    out["cache"] = {"webp": webp, "f_jpg": fjpg, "other_files": other, "archives": len(arch_files),
                    "archives_bytes": sum(e.stat().st_size for e in arch_files)}
    got = {"tables": out["tables"], "indexes_named": out["indexes_named"], "indexes_total": out["indexes_total"], "Comics": rows["Comics"],
           "comics_cat0": out["comics_by_category"].get("0"), "comics_cat1": out["comics_by_category"].get("1"),
           "Series": rows["Series"], "Folders": rows["Folders"], "Bookmarks": rows["Bookmarks"],
           "GroupUserMetadata": rows["GroupUserMetadata"], "ComicUserLists": rows["ComicUserLists"],
           "cache_webp": webp, "cache_f_jpg": fjpg, "cache_archives": len(arch_files)}
    mism = {k: (EXPECTED[k], got[k]) for k in EXPECTED if EXPECTED[k] != got[k]}
    out["expected"] = EXPECTED
    out["mismatches"] = mism
    dump_json(CENSUS / "baseline.json", out)
    write_doc(out, rows)
    print("mismatches vs planning numbers:", mism or "none")
    print("wrote", CENSUS / "baseline.json", "and", DOCS / "v1-baseline-counts.md")


def write_doc(out, rows):
    DOCS.mkdir(parents=True, exist_ok=True)
    L = ["# Books v1 (MyBooks) baseline counts", ""]
    L.append(f"Taken {out['taken_utc']} from the frozen snapshot `{os.path.basename(out['db'])}` "
             f"({out['db_bytes']:,} bytes; {out['tables']} tables, {out['indexes_named']} named indexes, FTS docs {out['fts_docs']:,}). "
             "Every later migration verifier compares against these numbers. Generated by "
             "`scripts/books/census/baseline.py`; raw data in `data/books/census/baseline.json` (gitignored).")
    L += ["", "## Row counts", "", "| Table | Rows |", "|---|---:|"]
    for t, n in sorted(rows.items(), key=lambda kv: (-kv[1], kv[0])):
        L.append(f"| `{t}` | {n:,} |")
    L += ["", "## Catalog shape", ""]
    L.append(f"- Comics by Category: {out['comics_by_category']} (0 = comics, 1 = books)")
    L.append(f"- Comics by extension: {out['comics_by_ext']}")
    L.append(f"- Broken {out['comics_broken']:,} / Excluded (shadow duplicates) {out['comics_excluded']:,} / "
             f"KeepInDirectory {out['comics_keep_in_directory']:,} / with embedded series metadata {out['comics_with_comicinfo']:,}")
    L.append(f"- Folders: {rows['Folders']:,} ({out['folders_depth_roots']} roots); ACL: {out['folder_acl_distinct_lists']} distinct lists, "
             f"by list length {out['folder_acl_by_list_length']}")
    L.append(f"- Series: {out['series_stats']}")
    L.append(f"- Match statuses: {json.dumps(out['match_status'])}")
    L.append(f"- AI series metadata: {json.dumps(out['claude'])}")
    L += ["", "## User activity (by v1 user id; user 2 is the account that migrates)", ""]
    L.append(f"- Users: {out['users']}")
    for t, d in out["activity_by_user_id"].items():
        L.append(f"- `{t}`: {d}")
    L.append(f"- Bookmarks by Status (0 unread / 1 in progress / 2 finished): {out['bookmarks_status']}; hidden from history: {out['bookmarks_hidden']}")
    L.append(f"- GroupUserMetadata shapes: {out['group_metadata_types']}")
    L += ["", "## Live cache directory", ""]
    c = out["cache"]
    L.append(f"- `{{id}}.webp` thumbnails: {c['webp']:,} / `f_*.jpg` collection icons (not regenerable): {c['f_jpg']} / "
             f"other files: {c['other_files']} / `archives` subfolder: {c['archives']} files, {c['archives_bytes']:,} bytes")
    L += ["", "## SystemState fingerprints", ""]
    for k, v in out["system_state"].items():
        L.append(f"- `{k}` = `{v}`")
    L += ["", "## Cross-check against the planning-time measurements", ""]
    L.append("Mismatches: " + (json.dumps(out["mismatches"]) if out["mismatches"] else "none - all planning numbers reproduced."))
    (DOCS / "v1-baseline-counts.md").write_text("\n".join(L) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
