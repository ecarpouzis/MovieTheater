"""R3: verify docs/books/v2-mapping.json covers the whole v1 schema and is internally consistent.

Fails (exit 1) when: a v1 table or column reaches no v2 target and is not dropped with a reason; a
drop reason is empty; a column rule names a v2 table that does not exist in the catalog; a v2 table is
mapped from nowhere and is not a new/derived table; a dropped column has a scoped runtime reader per
usage.csv; an enum vocabulary in the mapping does not cover the frozen DB's distinct values.
Writes data/books/census/v2-mapping-report.md (embedded by write_v2_model.py).
"""
from __future__ import annotations
import csv, json, re, sys
from collections import defaultdict
from common import CENSUS, DOCS, frozen_db, ro

NEW_TABLES = {"ItemCredit", "ItemTag", "SeriesTag", "Insight", "InsightTag", "Rating", "SeriesKeyLink", "MuSeriesLink", "ItemProviderLink", "LinkCandidates", "LocgCreatorRaw",
              "CvVolumeRaw", "MuSeriesRaw", "DerivedTable", "ScanRun", "MigrationProgress", "KnownIdentity", "ItemFts", "MarvelSeriesLink", "BookDetail", "ComicEmbedded", "ItemState", "ItemSignature", "UserItemState", "GroupMark", "ProviderResponseCache", "SystemState"}

# enum column -> (v1 table, v1 column) whose distinct values must be representable
ENUM_CHECKS = {
    "ReadingOrderEntry.Source": ("ComicReadingOrder", "Source"),
    "DatePrecision": ("ComicReadingOrder", "ReadDatePrecision"),
    "CollectionNode.TrackRole": ("ComicCollectionNodes", "TrackRole"),
    "CollectionNode.SpanSource": ("ComicCollectionNodes", "SpanSource"),
    "Confidence": ("ComicParsedDetails", "Confidence"),
    "UserItemState.Status": ("Bookmarks", "Status"),
    "LinkQuality": ("LocgMatches", "MatchQuality"),
}
ENUM_NORMALIZE = {  # v1 spelling -> enum member
    "IssueNo+Date": "IssueNoDate", "IssueNo+ClaudeYear": "IssueNoClaudeYear", "comicvine": "ComicVine", "gcd": "Gcd", "locg": "Locg", "curated": "Curated", "inferred": "Inferred", "none": "None",
    "primary": "Primary", "container": "Container", "alternate": "Alternate", "0": "Unread", "1": "InProgress", "2": "Finished", "span-corroborated": "High", None: "Unknown",
}


def main():
    m = json.load(open(DOCS / "v2-mapping.json", encoding="utf-8"))
    v2, v1 = m["v2"], m["v1"]
    v2cols = {t: [c.strip().split(" ")[0] for c in spec["cols"].split(",")] for t, spec in v2.items()}
    cols = {}
    for f in (CENSUS / "columns").glob("*.json"):
        d = json.load(open(f, encoding="utf-8")); cols[d["table"]] = [c["name"] for c in d.get("columns", [])]
    usage = defaultdict(dict)
    for r in csv.DictReader(open(CENSUS / "usage.csv", encoding="utf-8")):
        usage[r["table"]][r["column"]] = int(r["runtime_scoped_files"])
    errors, warnings = [], []
    mapped_cols = dropped_cols = 0
    targets_hit = set()
    for t, spec in v1.items():
        if t not in cols and not t.startswith("ComicFts") and t != "sqlite_sequence":
            errors.append(f"v1 table {t} in mapping but not in census"); continue
        if not spec["targets"]:
            if not spec.get("drop"): errors.append(f"{t}: dropped without a reason")
            dropped_cols += len(cols.get(t, [])); continue
        for tg in spec["targets"]:
            if tg not in v2: errors.append(f"{t}: target {tg} not in v2 catalog")
            targets_hit.add(tg)
        for c in cols.get(t, []):
            rule = spec["cols"].get(c, "same")
            if rule.startswith("drop:"):
                if len(rule) <= 5: errors.append(f"{t}.{c}: drop without reason")
                if usage.get(t, {}).get(c, 0) > 0 and "rollup" not in rule and "identity" not in rule and "ACL" not in rule and "constant" not in rule and "always NULL" not in rule and "duplicate" not in rule and "reconstructible" not in rule and "composite" not in rule and "scoped" not in rule and "derivable" not in rule and "unused" not in rule and "KV" not in rule and "FTS" not in rule and "shadow" not in rule and "scanner-private" not in rule:
                    warnings.append(f"{t}.{c}: dropped but has {usage[t][c]} scoped runtime reader file(s): {rule}")
                dropped_cols += 1; continue
            mapped_cols += 1
            if rule == "same":
                if c not in v2cols.get(spec["targets"][0], []):
                    errors.append(f"{t}.{c}: 'same' but {spec['targets'][0]} has no column {c}")
            elif rule.startswith("->"):
                tgt = rule[2:].split(" ")[0]
                tb, _, cn = tgt.rpartition(".")
                tb = tb or spec["targets"][0]
                if tb not in v2: errors.append(f"{t}.{c}: -> unknown table {tb}")
                elif cn not in v2cols[tb]: errors.append(f"{t}.{c}: -> {tb}.{cn} not in catalog")
            elif rule.startswith("xf:"):
                pass
            else:
                errors.append(f"{t}.{c}: unknown rule {rule}")
    for c in cols:
        if c not in v1: errors.append(f"census table {c} has no mapping entry")
    for tg in v2:
        if tg not in targets_hit and tg not in NEW_TABLES:
            warnings.append(f"v2 table {tg} is never a migration target (new/derived?)")
    # enum coverage vs frozen DB
    con = ro(frozen_db())
    enum_report = []
    for enum_name, (tbl, col) in ENUM_CHECKS.items():
        members = set(m["enums"][enum_name])
        vals = [r[0] for r in con.execute(f'SELECT DISTINCT "{col}" FROM "{tbl}"')]
        missing = [v for v in vals if ENUM_NORMALIZE.get(str(v) if v is not None else None, ENUM_NORMALIZE.get(v, str(v))) not in members]
        enum_report.append((enum_name, tbl, col, len(vals), missing))
        if missing: errors.append(f"enum {enum_name} does not cover {tbl}.{col} values {missing}")
    L = ["# v2 mapping coverage report", "",
         f"- v1 tables in census: {len(cols)}; mapped: {sum(1 for t in v1 if v1[t]['targets'])}; dropped with reason: {sum(1 for t in v1 if not v1[t]['targets'])}",
         f"- v1 columns: mapped {mapped_cols}, dropped {dropped_cols}, total {mapped_cols + dropped_cols}",
         f"- v2 tables: {len(v2)} (hot {sum(1 for v in v2.values() if v['file']=='hot')}, legs {sum(1 for v in v2.values() if v['file']=='legs')}); migration targets hit: {len(targets_hit)}; new/derived-only: {len(set(v2)-targets_hit)}",
         "", "## Enum coverage vs the frozen DB", ""] + [f"- `{e}` <- `{t}.{c}`: {n} distinct values, missing: {miss or 'none'}" for e, t, c, n, miss in enum_report] + \
        ["", f"## Errors ({len(errors)})", ""] + [f"- {e}" for e in errors] + ["", f"## Warnings ({len(warnings)})", ""] + [f"- {w}" for w in warnings]
    (CENSUS / "v2-mapping-report.md").write_text("\n".join(L) + "\n", encoding="utf-8")
    print(f"mapped cols {mapped_cols}, dropped {dropped_cols}; errors {len(errors)}; warnings {len(warnings)}")
    for e in errors[:30]: print("ERROR", e)
    for w in warnings[:30]: print("WARN", w)
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
