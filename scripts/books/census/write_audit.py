"""R0 step 8: compose docs/books/v1-data-model-audit.md from the census artifacts.

Inputs (data/books/census): baseline.json, columns/*.json, usage_summary.json, usage.csv,
summary_fields.csv, queries.json (optional). The plan's provisional per-table verdicts (program plan
section 6.1 as amended by section D) are listed beside the evidence and an automatic CONFLICT column
says where the data disagrees with the plan. No usernames or machine names are written.
"""
from __future__ import annotations
import csv, json
from collections import defaultdict
from common import CENSUS, DOCS

# plan verdict per v1 table (drop / merge / split / keep / trim); free text after ':' = target
PLAN = {
    **{t: "drop: empty CV entity" for t in ("ComicvineCharacters", "ComicvinePeople", "ComicvineTeams", "ComicvineStoryArcs",
                                              "ComicvineIssueCharacters", "ComicvineIssuePeople", "ComicvineIssueTeams",
                                              "ComicvineIssueStoryArcs", "ComicvineSeries", "ComicvineVolumeSeries")},
    "LocgApiCaches": "drop: never written", "Tags": "drop: dead tag system", "ComicTagAssociation": "drop: dead tag system",
    "SeriesUserLists": "drop: 0 rows, superseded by GroupUserMetadata", "Sessions": "drop: identity moves to the site",
    "Users": "drop: identity moves to the site", "SiteSettings": "drop -> SystemState", "SeriesAliases": "drop: manual map unused by resolution",
    "Folders": "merge -> Folder (+FolderAggregates)", "FolderAggregates": "merge -> Folder",
    **{t: "merge -> CollectedEditionSpan(Source)" for t in ("ComicvineCollectedEditions", "GcdCollectedEditions", "LocgCollectedEditions", "CuratedCollectedEditions")},
    **{t: "merge -> LibraryRating(TargetKind, IsOverride)" for t in ("LibraryComicRatings", "LibrarySeriesRatings", "LibraryRatingOverrides")},
    **{t: "merge -> ItemProviderLink(Provider)" for t in ("ComicvineMatches", "LocgMatches", "GcdMatches", "BarneyMatches", "MarvelMatches", "InducksMatches")},
    **{t: "merge -> SeriesProviderLink(Provider) keyed by SeriesId" for t in ("ComicvineSeriesLinks", "ExternalSeriesLinks", "MangaUpdatesMatches", "MarvelSeriesMatches")},
    "ComicvineApiCaches": "merge -> ProviderResponseCache(Provider)",
    "Bookmarks": "merge -> UserItemState (one row per user x item)", "ComicUserLists": "merge -> UserItemState",
    "GroupUserMetadata": "merge -> GroupMark(GroupType)",
    "ClaudeSeriesMetadata": "merge -> SeriesInsight keyed by SeriesId (collapse rule)", "ClaudeSeriesTags": "merge -> SeriesTag (real M:N)",
    "ClaudeBookMetadata": "merge -> BookInsight", "ClaudeBookTags": "merge -> ItemTag (real M:N)",
    "Comics": "split -> Item + ItemEmbeddedMetadata + BookDetail", "ComicParsedDetails": "split -> ComicDetail (provider FKs move to Series)",
    "Series": "keep", "SeriesParsedKeys": "keep -> SeriesAlias", "SeriesMergeLogs": "keep -> SeriesMerge (migrated in full as the old-id redirect)",
    "Publishers": "keep", "LibraryPaths": "keep -> LibraryRoot", "ComicReadingOrder": "keep -> ReadingOrderEntry(SeriesId)",
    "ComicCollectionNodes": "keep -> CollectionNode", "ComicvineVolumes": "keep -> CvVolume", "ComicvineIssues": "keep -> CvIssue",
    "LocgComics": "trim -> LocgComic (drop blobs nothing reads)", "LocgSeries": "keep (new entity)", "LocgContainments": "keep -> LocgContainment",
    "GcdIssues": "keep -> GcdIssue", "GcdSeries": "keep (new entity)", "MangaUpdatesSeries": "keep -> MuSeries (runtime leg)",
    "BarneyProgs": "keep -> BarneyProg (reading-order recompute)", "ExternalWorks": "keep -> ExternalWork",
    "OpenLibraryEditions": "keep (new entity)", "OpenLibraryWorks": "keep (new entity)",
    "MarvelSeries": "move -> links + disk cache", "MarvelIssues": "move -> links + disk cache", "BarcodeScans": "keep (new entity)",
    "OlSeriesInference": "keep (new entity)", "LocgSeriesInference": "keep (new entity)", "CvdbResolutions": "keep -> CvdbResolution",
    "TagAliases": "keep -> TagAlias", "KidSafeTags": "keep -> KidSafeTag", "SeriesInferenceDecisions": "keep", "SeriesMatchReviews": "keep",
    "DuplicateGroups": "keep", "DuplicateMembers": "keep", "SystemState": "keep", "ComicFts": "keep -> ItemFts (rebuilt from Resolved*)",
}


def load():
    base = json.load(open(CENSUS / "baseline.json", encoding="utf-8"))
    cols = {}
    for f in (CENSUS / "columns").glob("*.json"):
        d = json.load(open(f, encoding="utf-8"))
        cols[d["table"]] = d
    usage = json.load(open(CENSUS / "usage_summary.json", encoding="utf-8"))
    fields = list(csv.DictReader(open(CENSUS / "summary_fields.csv", encoding="utf-8")))
    urows = list(csv.DictReader(open(CENSUS / "usage.csv", encoding="utf-8")))
    q = None
    if (CENSUS / "queries.json").exists():
        q = json.load(open(CENSUS / "queries.json", encoding="utf-8"))
    return base, cols, usage, fields, urows, q


def main():
    base, cols, usage, fields, urows, q = load()
    rows = base["rows"]
    L = ["# Books v1 (MyBooks) data-model audit — census evidence for the v2 design", ""]
    L.append("Produced by R0 of the Books merge program from the frozen snapshot (see `v1-baseline-counts.md`). "
             "Three censuses: **column** (`column_census.py`: null/constant/distinct/bytes per column), **code usage** "
             "(`usage_census.py`: which entity properties the runtime, startup, tools and offline code reference — a runtime "
             "reference is *scoped* when the file also names the entity or its DbSet; names shared by several entities carry "
             "an ambiguity count), and **query** (`capture-sql.ps1` + `query_census.py`: the hot SQL the live binary runs at "
             "warm-up and under the browse/reader/shelf endpoints, with `EXPLAIN QUERY PLAN`). Raw artifacts live under "
             "`data/books/census/` (gitignored). The **plan verdict** column is the program plan's provisional v2 decision; "
             "**conflict** is where the evidence disagrees and R3 must rule.")
    L += ["", "## 0. Headline findings (R0, 2026-08-25) — what the census overturned or confirmed", "",
          "1. **`LocgComics.RawJson` is ALWAYS NULL** — the plan assumed it was the 741 MB's bulk. The weight is `LocgComics.CreatorsJson` "
          "(78.6 MB, up to 82 KB/row, projected and parsed client-side), `Comics.Description` (45.7 MB), `LocgComics.Description` (32 MB), "
          "`ComicvineSeriesLinks.CandidatesJson` (29 MB), `ComicvineApiCaches.ResponseJson` (27 MB). v2 must normalize `CreatorsJson` (a `LocgCreator` table or trimmed roles) — dropping RawJson saves nothing.",
          "2. **`LocgContainments` (391k rows, 30 MB) has NO runtime reader** — only `Mybooks.Tools` (`locg-containment`) and the boot block touch it; the runtime reads the derived `LocgCollectedEditions`. It is offline input, not hot-DB data.",
          "3. **Facets are full `GROUP BY` scans over `Comics`** (21 TEMP B-TREE statements, 15–98 ms each warm, per user) — the 48 h facet cache exists to hide this. A real tag table + resolved columns (plan §D c7/c8) removes the scans.",
          "4. **Only 15 of the 60 named indexes are touched by the hot set**; the browse path leans on `idx_comics_category` plus PK/auto indexes. Most of the other 45 serve writes, admin or offline paths — re-derive v2 indexes from the query census, do not inherit.",
          "5. **MangaUpdates IS a runtime leg** (`ComicSummary` projects `MuDescription/MuGenresJson/MuTagsCsv`) and **Barney feeds reading-order recompute** — confirmed; Marvel/Inducks (14 links each) have no EF entity and no runtime reader.",
          "6. **Dead columns**: 16 always-NULL (`Comics.UserRating/StoryArc/StoryArcNumber/AlternateCount/PageSignature`, `LocgComics.ReleaseDate/KeyType/KeyReason/EstimatedValue/Url/RawJson`, `LocgMatches.Slug`, `GroupUserMetadata.Notes`, …), 39 constant (all five `ComicvineVolumes.*Json` are `'[]'`, every `ModelId`, most `CreatedAt/ScrapedAt`), and two DB columns with no entity property (`GroupUserMetadata.IsFavorite`, `ClaudeSeriesMetadata.ReviewFlag`).",
          "7. **`ClaudeBookMetadata` is mostly empty**: `Rating` 98 % NULL, `Synopsis` 99 % NULL, `TagsCsv` constant — 6,168 rows of which only the audience/maturity fields carry data. The books insight leg is thinner than the comics one by two orders of magnitude.",
          "8. **The per-folder ACL is uniform**: 2 distinct lists over 54,418 folders (lengths 7 and 8, differing by the test account) — confirms the plan's drop.",
          "9. **`ComicSummary` fields the client never reads**: `Web`, `BlackAndWhite`. Everything else in the ~100-field projection has a client consumer.",
          "10. **`SeriesUserLists`, `Tags`, `Sessions`, `Users`, `SeriesAliases`, `SiteSettings` and the nine empty ComicVine tables are still referenced by runtime code** (auth, scrapers, dead tag endpoints) — dropping them means deleting that code in the port, not just the tables.",
          "11. Warm-up on this box: `Cache warm (startup): 10 users, 82 targets warmed, 34.1 s` on a cold copy — the per-user cache-key design (§8.1 `KnownIdentity`) must keep that shape."]
    # ---- size
    total_bytes = sum(d.get("bytes_total", 0) for d in cols.values())
    L += ["", "## 1. Where the bytes are", "",
          f"Column payload {total_bytes/1e6:,.1f} MB of a {base['db_bytes']/1e6:,.1f} MB file (the rest is indexes, FTS and page overhead).", "",
          "| Table | Rows | Column bytes (MB) | Heaviest columns |", "|---|---:|---:|---|"]
    for t, d in sorted(cols.items(), key=lambda kv: -kv[1].get("bytes_total", 0))[:20]:
        heavy = sorted(d.get("columns", []), key=lambda c: -c.get("bytes", 0))[:3]
        L.append(f"| `{t}` | {d['rows']:,} | {d.get('bytes_total',0)/1e6:,.1f} | " +
                 ", ".join(f"`{c['name']}` {c.get('bytes',0)/1e6:.1f} MB" for c in heavy if c.get('bytes')) + " |")
    # ---- columns
    an, cn, hi = [], [], []
    for t, d in cols.items():
        for c in d.get("columns", []):
            if c.get("always_null"):
                an.append(f"`{t}.{c['name']}`")
            elif c.get("constant"):
                cn.append(f"`{t}.{c['name']}`={c['samples'][0] if c.get('samples') else ''!s:.40}")
            elif 90 <= c.get("null_pct", 0) < 100:
                hi.append(f"`{t}.{c['name']}` ({c['null_pct']:.0f}%)")
    L += ["", "## 2. Column census", "",
          f"- **Always NULL ({len(an)})** — nothing has ever been written; drop unless a writer is planned: " + ", ".join(an),
          f"- **Constant ({len(cn)})** — one value across every row (≥100 rows); fold into config/enum or drop: " + ", ".join(cn),
          f"- **≥90 % NULL ({len(hi)})** — sparse; candidates for a 1:1 side table or removal: " + ", ".join(hi)]
    # ---- usage
    L += ["", "## 3. Code-usage census", ""]
    L.append(f"- Entities parsed: {usage['entities']}; properties: {usage['properties']}.")
    L.append("- **Tables with no scoped runtime reader** (only startup/tools/offline touch them): " +
             ", ".join(f"`{t}`" for t in usage["zero_runtime_tables"]))
    L.append("- **Tables with no EF entity at all** (python/offline-only): " + ", ".join(f"`{t}`" for t in usage["entityless_tables"]))
    L.append("- **DB columns with no entity property** (dead columns): " +
             ", ".join(f"`{t}.{c}`" for t, cs in usage["db_columns_without_entity_property"].items() for c in cs))
    L.append("- **`ComicSummary` fields the client never reads**: " + ", ".join(f"`{f}`" for f in usage["unused_summary_fields"]))
    L += ["", "Zero-runtime-reader columns per table (column exists in the DB, no scoped runtime file and not in the projection):", ""]
    for t, v in sorted(usage["tables"].items()):
        if v["zero_runtime_columns"]:
            L.append(f"- `{t}` ({len(v['zero_runtime_columns'])}/{v['columns_in_db']}): " + ", ".join(f"`{c}`" for c in v["zero_runtime_columns"]))
    # ---- queries
    L += ["", "## 4. Query census", ""]
    if q:
        flagged = [r for r in q["results"] if r["flags"]]
        L.append(f"- Captured {q['statements_captured']} statements ({q['distinct']} distinct). Flagged {len(flagged)} "
                 f"(full scans of tables ≥ 50k rows, automatic indexes, temp b-trees). Named indexes used: "
                 f"{len(q['indexes_used'])} of {q['indexes_defined']}.")
        L.append("- **Unused by the hot set**: " + ", ".join(f"`{i}`" for i in q["indexes_unused"]))
        for r in flagged[:40]:
            L.append(f"- {', '.join(r['flags'])} — {r['kind']} ×{r['count']} max {r['max_ms']} ms: `{r['sql'][:220]}`")
        L.append("- Full detail: `data/books/census/queries.md`.")
    else:
        L.append("- (query census not yet run)")
    # ---- per-table verdict
    L += ["", "## 5. Per-table evidence vs plan verdict", "",
          "| Table | Rows | MB | Runtime files (scoped) | Tools | Offline | Plan verdict | Conflict |", "|---|---:|---:|---:|---:|---:|---|---|"]
    conflicts = []
    for t in sorted(rows):
        if t.startswith("ComicFts_") or t == "sqlite_sequence":
            continue
        u = usage["tables"].get(t, {})
        rt = len(u.get("runtime_scoped_files", [])) if u else 0
        tools = u.get("tools_files", 0) if u else 0
        off = u.get("offline_files", 0) if u else 0
        entity = bool(u)
        verdict = PLAN.get(t, "?")
        c = []
        if verdict.startswith("drop") and rt:
            c.append(f"runtime code still references it ({rt} files)")
        if verdict.startswith("keep") and rows[t] == 0:
            c.append("0 rows")
        if verdict.startswith(("keep", "trim")) and entity and rt == 0 and tools == 0 and off == 0:
            c.append("no readers anywhere")
        if verdict.startswith(("keep", "trim")) and entity and rt == 0 and (tools or off):
            c.append("no runtime reader — offline/tools only")
        if not entity and not t.startswith("ComicFts"):
            c.append("no EF entity")
        if verdict == "?":
            c.append("no plan verdict")
        if c:
            conflicts.append((t, c))
        L.append(f"| `{t}` | {rows[t]:,} | {cols.get(t, {}).get('bytes_total', 0)/1e6:.1f} | {rt} | {tools} | {off} | {verdict} | {'; '.join(c)} |")
    L += ["", f"### Conflicts to rule on in R3 ({len(conflicts)})", ""]
    for t, c in conflicts:
        L.append(f"- `{t}`: {'; '.join(c)}")
    L += ["", "## 6. `ComicSummary` fields by client usage", "", "| Field | Client files |", "|---|---:|"]
    for f in fields:
        L.append(f"| `{f['field']}` | {f['client_files']} |")
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / "v1-data-model-audit.md").write_text("\n".join(L) + "\n", encoding="utf-8")
    print("wrote", DOCS / "v1-data-model-audit.md", "conflicts:", len(conflicts))


if __name__ == "__main__":
    main()
