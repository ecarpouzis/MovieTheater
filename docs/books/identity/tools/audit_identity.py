"""Audit identity against the POPULATION, not against the queue. Every FAIL count must be zero.

`audit_containment.py` exists because both of the containment pass's real defects were found by a check
defined over the wrong set: a queue that skipped anything already decided read zero while hundreds of
files sat wrong. So this starts from the relationships that exist — the stored volume ids, the link rows,
the title tier, the decisions — and tests each one.

Two of PLAN §7's invariants are stated there as "must be 0" and are NOT zero on today's data, for the same
reason: as written they test v1's residue rather than this pass's writes.

  * "CvVolumeId on a conflated-series shelf still flagged open" — 80 shelves carry a v1 CvVolumeId under an
    open `conflated-series` flag. Not one of them was written by this pass, and clearing them is a decision
    for Phase S (a `wrong-cv-link` flag), not for an auditor. So the FAIL is scoped to what THIS pass may
    write — a Manual SeriesKeyLink or an `identity` decision on an open conflated shelf, which §3.3 forbids
    — and the v1 population is reported beside it as a lead with its count.
  * "ItemProviderLink on an excluded/gone item" — 0 rows point at an item that no longer exists (that is the
    real integrity question and it is clean); 966 point at an item that was EXCLUDED after the link was
    made. Excluded items never surface, so that is residue, not a defect. Same split: FAIL on gone and on
    anything this pass wrote, lead on v1's 966.

Presented, not resolved (PLAN §5.10): if the lead wants either stated the strict way, say so and the two
`lead=True` entries below become failures.

`python audit_identity.py [--verbose]`
"""
import sqlite3
import sys

import idbase

VERBOSE = "--verbose" in sys.argv
con = idbase.open_hot()
# The checker accepts a GCD series id from the full dump (check_identity.Checker.gcd_series_exists); the audit
# must judge the same ids by the same rule, so the dump's id column is loaded into a temp table once.
con.execute("CREATE TEMP TABLE dump_gcd_series (id INTEGER PRIMARY KEY)")
_dump = idbase.open_gcd_dump()
if _dump is not None:
    con.executemany("INSERT OR IGNORE INTO temp.dump_gcd_series VALUES (?)",
                    _dump.execute("SELECT id FROM gcd_series"))

SH = idbase.SHELF_SQL
OPEN_FLAG = "(f.ReviewState IS NULL OR f.ReviewState IN ('','Pending','Open'))"

CHECKS = [
    # (label, sql, lead?)
    ("one CvVolumeId stored on two file-holding shelves (linking them MERGES them at the next resolve, §6.5)", f"""
     SELECT s.CvVolumeId, count(*), group_concat(s.Id)
     FROM Series s WHERE s.CvVolumeId IS NOT NULL AND s.Id IN ({SH})
     GROUP BY s.CvVolumeId HAVING count(*) > 1""", False),

    # The guard exists for a pass that called a CONFLATED shelf one comic. A Manual link on an ALIAS key that only
    # some of the shelf's files carry (wave 3: 'Bloodstrike v1 (1993)' on S2988, which also holds Assassin,
    # Brutalists and Battle Blood) named a subset correctly; the split is still owed, and revisit.txt carries it.
    ("THIS PASS wrote a Manual CV/GCD SeriesKeyLink for a key on a shelf with an OPEN conflated-series flag", f"""
     SELECT k.ParsedKey, k.Provider, k.ProviderKey
     FROM SeriesKeyLink k JOIN SeriesAlias a ON a.ParsedKey = k.ParsedKey
     JOIN ContainmentFlag f ON f.SeriesId = a.SeriesId
     WHERE k.Status = 5 AND k.Provider IN (0,3) AND f.Flag = 'conflated-series' AND {OPEN_FLAG}
       AND EXISTS (SELECT 1 FROM SeriesInferenceDecision d
                   WHERE d.Class = 'identity' AND d.SeriesKey = k.ParsedKey)
       AND NOT EXISTS (SELECT 1 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                       WHERE i.SeriesId = a.SeriesId AND cd.ParsedSeriesKey <> k.ParsedKey)""", False),

    ("LEAD a Manual link on an ALIAS key of a shelf with an OPEN conflated-series flag — the key names a subset; the SPLIT is owed (revisit.txt)", f"""
     SELECT k.ParsedKey, k.Provider, k.ProviderKey, a.SeriesId
     FROM SeriesKeyLink k JOIN SeriesAlias a ON a.ParsedKey = k.ParsedKey
     JOIN ContainmentFlag f ON f.SeriesId = a.SeriesId
     WHERE k.Status = 5 AND k.Provider IN (0,3) AND f.Flag = 'conflated-series' AND {OPEN_FLAG}
       AND EXISTS (SELECT 1 FROM SeriesInferenceDecision d
                   WHERE d.Class = 'identity' AND d.SeriesKey = k.ParsedKey)
       AND EXISTS (SELECT 1 FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                   WHERE i.SeriesId = a.SeriesId AND cd.ParsedSeriesKey <> k.ParsedKey)""", True),

    # A decision whose shelf a landed wave MERGED AWAY is a decision that took effect, not a dangling
    # reference — `check_identity.py` already reads `SeriesMerge` to say so, and this must agree with it or
    # the two instruments disagree about the same row. Only a key that names no shelf and no merge is a
    # defect. (Wave 2 raised 3 of these for Grumble S8123, which merged legitimately.)
    ("an identity decision whose key names no shelf and no recorded merge", """
     SELECT d.Id, d.SeriesKey, d.Target FROM SeriesInferenceDecision d
     WHERE d.Class = 'identity' AND d.SeriesKey IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM SeriesAlias a WHERE a.ParsedKey = d.SeriesKey)
       AND NOT EXISTS (SELECT 1 FROM Series s WHERE s.ParsedKey = d.SeriesKey)
       -- a per-file key still carried by files on a live shelf is an alias in force (wave 3 raised 15 of
       -- these: refused shelves whose files parse to several keys, e.g. 'Love and Rockets - New Stories')
       AND NOT EXISTS (SELECT 1 FROM ComicDetail cd JOIN Item i ON i.Id = cd.ItemId
                       WHERE cd.ParsedSeriesKey = d.SeriesKey AND i.SeriesId IS NOT NULL)
       AND NOT EXISTS (SELECT 1 FROM SeriesMerge m
                       WHERE 'S' || m.OldSeriesId = d.Target OR cast(m.OldSeriesId AS TEXT) = d.Target
                          OR d.Target LIKE '%S' || m.OldSeriesId)""", False),

    ("LEAD an identity decision against a shelf a landed wave merged away (it took effect)", """
     SELECT d.Id, d.SeriesKey, d.Target FROM SeriesInferenceDecision d
     WHERE d.Class = 'identity' AND d.SeriesKey IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM SeriesAlias a WHERE a.ParsedKey = d.SeriesKey)
       AND NOT EXISTS (SELECT 1 FROM Series s WHERE s.ParsedKey = d.SeriesKey)
       AND EXISTS (SELECT 1 FROM SeriesMerge m
                   WHERE 'S' || m.OldSeriesId = d.Target OR cast(m.OldSeriesId AS TEXT) = d.Target
                      OR d.Target LIKE '%S' || m.OldSeriesId)""", True),

    ("an ItemProviderLink whose item no longer exists", """
     SELECT l.ItemId, l.Provider, l.ProviderKey FROM ItemProviderLink l
     LEFT JOIN Item i ON i.Id = l.ItemId WHERE i.Id IS NULL""", False),

    ("an identity-read ItemProviderLink on an excluded or missing item", """
     SELECT l.ItemId, l.Provider, l.ProviderKey FROM ItemProviderLink l
     LEFT JOIN Item i ON i.Id = l.ItemId
     WHERE l.Method = 'identity-read' AND (i.Id IS NULL OR coalesce(i.IsExcluded,0) = 1)""", False),

    ("a Series.TitleId pointing at a SeriesTitle that is not there", """
     SELECT s.Id, s.TitleId, s.Name FROM Series s
     WHERE s.TitleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM SeriesTitle t WHERE t.Id = s.TitleId)""", False),

    ("a SeriesTitle no run points at", """
     SELECT t.Id, t.Key, t.Name FROM SeriesTitle t
     WHERE NOT EXISTS (SELECT 1 FROM Series s WHERE s.TitleId = t.Id)""", False),

    ("a SeriesTitle.RunCount that disagrees with the runs that carry its id", """
     SELECT t.Id, t.RunCount, (SELECT count(*) FROM Series s WHERE s.TitleId = t.Id)
     FROM SeriesTitle t
     WHERE coalesce(t.RunCount,-1) <> (SELECT count(*) FROM Series s WHERE s.TitleId = t.Id)""", False),

    ("a title whose runs disagree on the franchise", """
     SELECT t.Id, t.Name, count(DISTINCT s.Franchise) FROM SeriesTitle t JOIN Series s ON s.TitleId = t.Id
     WHERE s.Franchise IS NOT NULL AND s.Franchise <> ''
     GROUP BY t.Id HAVING count(DISTINCT s.Franchise) > 1""", False),

    ("a Provider=Gcd SeriesKeyLink whose GcdSeriesId is in neither the legs GcdSeries table nor the GCD dump", """
     SELECT k.ParsedKey, k.Provider, k.ProviderKey FROM SeriesKeyLink k
     WHERE k.Provider = 3 AND k.ProviderKey IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM legs.GcdSeries g WHERE g.GcdSeriesId = k.ProviderKey)
       AND NOT EXISTS (SELECT 1 FROM temp.dump_gcd_series d WHERE d.id = k.ProviderKey)""", False),

    # ── leads: reported with their counts, never a stop ────────────────────────────────────────
    ("LEAD a Manual SeriesKeyLink whose ParsedKey names no shelf (v1 left 11 of these on 2026-05-30; a key "
     "with no shelf is inert, and the identity-decision form of the same question FAILs above)", """
     SELECT k.ParsedKey, k.Provider, k.ProviderKey FROM SeriesKeyLink k
     WHERE k.Status = 5 AND k.Provider IN (0,3)
       AND NOT EXISTS (SELECT 1 FROM SeriesAlias a WHERE a.ParsedKey = k.ParsedKey)
       AND NOT EXISTS (SELECT 1 FROM Series s WHERE s.ParsedKey = k.ParsedKey)""", True),

    ("LEAD v1 CvVolumeId standing on a shelf with an OPEN conflated-series flag (Phase S writes wrong-cv-link)", f"""
     SELECT DISTINCT s.Id, s.CvVolumeId, s.Name FROM Series s JOIN ContainmentFlag f ON f.SeriesId = s.Id
     WHERE f.Flag = 'conflated-series' AND {OPEN_FLAG} AND s.CvVolumeId IS NOT NULL AND s.Id IN ({SH})""", True),

    ("LEAD v1 ItemProviderLink left on an item that was later EXCLUDED", """
     SELECT l.ItemId, l.Provider, l.ProviderKey FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
     WHERE coalesce(i.IsExcluded,0) = 1""", True),

    ("LEAD shelf whose stored CV volume disagrees with our start year by more than a year", f"""
     SELECT s.Id, s.YearStart, v.StartYear FROM Series s JOIN CvVolume v ON v.Id = s.CvVolumeId
     WHERE s.Id IN ({SH}) AND s.YearStart IS NOT NULL AND v.StartYear IS NOT NULL
       AND abs(s.YearStart - v.StartYear) > 1""", True),

    ("LEAD shelf whose issue-file count against the CV volume's CountOfIssues is outside 0.5-2", f"""
     SELECT s.Id, v.CountOfIssues, (SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                                    WHERE i.SeriesId = s.Id AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                                      AND coalesce(cd.IsCollection,0) = 0) AS ours
     FROM Series s JOIN CvVolume v ON v.Id = s.CvVolumeId
     WHERE s.Id IN ({SH}) AND coalesce(v.CountOfIssues,0) > 0 AND ours > 0
       AND (ours * 1.0 / v.CountOfIssues < 0.5 OR ours * 1.0 / v.CountOfIssues > 2.0)""", True),

    ("LEAD shelf whose CvVolumeId points at a volume we never fetched (Phase C.1 fetches these)", f"""
     SELECT s.Id, s.CvVolumeId, s.Name FROM Series s
     WHERE s.Id IN ({SH}) AND s.CvVolumeId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM CvVolume v WHERE v.Id = s.CvVolumeId)""", True),

    ("LEAD shelf whose files name several GCD series (a conflation candidate, or GCD splitting one run)", f"""
     SELECT i.SeriesId, count(DISTINCT l.SecondaryKey), '' FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
     WHERE l.Provider = 3 AND l.Status = 1 AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
       AND l.SecondaryKey IS NOT NULL AND i.SeriesId IN ({SH})
     GROUP BY i.SeriesId HAVING count(DISTINCT l.SecondaryKey) > 1""", True),
]

fails = 0
for label, sql, lead in CHECKS:
    try:
        rows = list(con.execute(sql))
    except sqlite3.OperationalError as e:
        print(f"  SKIP  {label}\n        ({e})")
        continue
    tag = ("lead" if lead else "FAIL") if rows else " ok "
    print(f"  {tag}  {len(rows):>6,}  {label}")
    if not lead:
        fails += len(rows)
    if rows and VERBOSE:
        for r in rows[:20]:
            print("           ", r)
        if len(rows) > 20:
            print(f"            ... and {len(rows)-20:,} more")

print(f"\n{fails} failure(s)")
sys.exit(1 if fails else 0)
