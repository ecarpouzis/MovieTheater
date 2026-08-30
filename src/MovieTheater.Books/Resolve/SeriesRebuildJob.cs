using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-resolve --series</c> — the SERIES IDENTITY rebuild: <see cref="SeriesResolver.Compute"/> (a pure
    /// function of the inputs) APPLIED to the hot file. R4 shipped the computation and the
    /// <see cref="SeriesResolver.Diff"/> proof; this is the job that writes it.
    ///
    /// <para><b>What it derives.</b> `Series` survivors (CanonicalKey / Name / CvVolumeId / ExternalWorkId),
    /// `SeriesAlias` (every parsed spelling → its survivor), `Item.SeriesId`, the merge of the minority series'
    /// series-keyed rows onto the survivor, `SeriesMerge` (the old-id redirect), `Series.IssueCount` and the run
    /// span (`YearStart`/`YearEnd`/`IsOngoing`). Its INPUTS — the things an operator edits to change the answer —
    /// are `ComicDetail.ParsedSeriesKey`, `SeriesKeyLink`, `Series.DisplayNameOverride` and the CV/External
    /// record names. Never the derived rows themselves (v2-model §4).</para>
    ///
    /// <para><b>Chunked, resumable, observable</b> like every bulk job here. Three phases behind ONE cursor:
    /// <c>0</c> = the identity pass (bounded by the series count, one atomic transaction), <c>&gt;= RepointBase</c>
    /// = re-point `Item.SeriesId` a page of item ids at a time (this is the 141k-row part), <c>1</c> = the finish
    /// pass (merge, delete, counts, spans, stranded marks, registry stamp). The caller drives the loop.</para>
    ///
    /// <para><b>Idempotent.</b> A second run writes the same survivors, the same aliases and the same ids, and
    /// leaves <see cref="SeriesResolver.Diff"/> at 0. The merge map is not carried in memory between phases: it is
    /// RE-DERIVED from the alias table (a Series row whose own id no longer appears as a survivor is a
    /// merged-away row, and its ParsedKey names the survivor), which is what makes a killed run resumable.</para>
    /// </summary>
    public static class SeriesRebuildJob
    {
        /// <summary>Cursor space: 0 = identity pass, 1 = finish pass, RepointBase+id = "re-point items after id".</summary>
        public const long IdentityCursor = 0;
        public const long FinishCursor = 1;
        public const long RepointBase = 1_000_000;

        /// <summary>A survivor row that has not yet been given its canonical key holds this instead (unique per id,
        /// so the UNIQUE index on CanonicalKey never trips mid-pass, and never equal to a real key so the
        /// survivor-stability preference cannot be fooled by it on a resumed run).</summary>
        private const string TempKeyPrefix = "~tmp~";

        /// <summary>One bounded phase. Returns true when the whole job is done.</summary>
        public static bool RunStep(TargetWriter hot, long cursor, int batchSize, Action<string> log, UnitCounts counts, out long nextCursor)
        {
            batchSize = Math.Clamp(batchSize, 100, 50_000);
            switch (cursor)
            {
                case IdentityCursor:
                {
                    var (survivors, aliases) = Identity(hot, log);
                    counts.Bump("survivors", survivors);
                    counts.Bump("aliases", aliases);
                    nextCursor = RepointBase;
                    return false;
                }
                case FinishCursor:
                {
                    Finish(hot, log, counts);
                    nextCursor = FinishCursor;
                    return true;
                }
                default:
                {
                    var after = cursor - RepointBase;
                    var last = Repoint(hot, after, batchSize, out var seen, out var repointed);
                    counts.Bump("items-repointed", repointed);
                    if (seen == 0) { nextCursor = FinishCursor; return false; }
                    nextCursor = RepointBase + last;
                    return false;
                }
            }
        }

        /// <summary>
        /// Drain every phase (the CLI verb's default and the admin's "recompute" trigger).
        ///
        /// <para><b>It converges before it returns.</b> Deleting the merged-away rows can leave the alias map one
        /// pass behind — a parsed key whose only `Series` row has just gone stops resolving — so the job checks
        /// <see cref="SeriesResolver.Diff"/> at the end and, if it is not yet zero, runs the whole thing ONCE
        /// more. Bounded to <see cref="MaxPasses"/>: two passes is convergence, a third would be a defect, and
        /// looping until zero would be exactly the unbounded job this codebase refuses.</para>
        /// </summary>
        public const int MaxPasses = 2;

        public static UnitCounts RunAll(TargetWriter hot, int batchSize, Action<string> log)
        {
            var counts = new UnitCounts();
            for (var pass = 1; pass <= MaxPasses; pass++)
            {
                var cursor = IdentityCursor;
                var guard = 0;
                while (true)
                {
                    hot.Begin();
                    var done = RunStep(hot, cursor, batchSize, log, counts, out var next);
                    hot.Commit();
                    if (done) break;
                    // The no-progress safety break: a phase that does not move the cursor would spin.
                    if (next == cursor && ++guard > 2) break;
                    if (next != cursor) guard = 0;
                    cursor = next;
                }

                var diff = SeriesResolver.Diff(hot, sampleLimit: 5).Total;
                if (diff == 0) break;
                if (pass == MaxPasses) { log($"series: recompute diff is still {diff} after {MaxPasses} passes"); break; }
                log($"series: recompute diff {diff} after pass {pass} — converging with one more");
            }
            return counts;
        }

        // ── phase 1: identity ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Compute the canonical identity and write the two tables that ARE it: the survivors' `Series` fields and
        /// the whole of `SeriesAlias`. Bounded by the series count (~19k on the real file) and atomic — a kill
        /// rolls the phase back rather than leaving half an alias map.
        /// </summary>
        private static (int Survivors, int Aliases) Identity(TargetWriter hot, Action<string> log)
        {
            var r = SeriesResolver.Compute(hot);

            // Park every canonical key first. CanonicalKey is UNIQUE, and a survivor routinely takes the key
            // another row currently holds, so writing them in place would trip the index on an ordering we do not
            // control. Merged-away rows keep their parked key until the finish phase deletes them.
            hot.Exec($"UPDATE Series SET CanonicalKey = '{TempKeyPrefix}' || Id WHERE {SeriesResolver.NotBookSql}");

            foreach (var (id, (key, name, cvVolumeId, externalWorkId)) in r.Survivors)
                hot.Update("Series", "Id", id, new
                {
                    CanonicalKey = key,
                    Name = name,
                    CvVolumeId = cvVolumeId,
                    ExternalWorkId = externalWorkId,
                });

            hot.Exec("DELETE FROM SeriesAlias");
            foreach (var (parsedKey, seriesId) in r.AliasMap)
                hot.Upsert("SeriesAlias", new { ParsedKey = parsedKey, SeriesId = seriesId });

            log($"series: {r.AliasMap.Count} parsed keys -> {r.Survivors.Count} canonical series ({r.MergeMap.Count} to merge away)");
            return (r.Survivors.Count, r.AliasMap.Count);
        }

        // ── phase 2: re-point ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-point one page of items at their canonical survivor, through the alias map and the STABLE parsed
        /// string (never through the old SeriesId — that is what makes the pass idempotent). The page's ordering
        /// is `Item.Id` ascending, which is exactly the cursor, so a resume is exact and not approximate.
        /// </summary>
        private static long Repoint(TargetWriter hot, long after, int batchSize, out int seen, out int repointed)
        {
            var upto = hot.Scalar<long>(
                "SELECT coalesce(max(Id), 0) FROM (SELECT Id FROM Item WHERE Id > $after ORDER BY Id LIMIT $n)",
                ("$after", after), ("$n", batchSize));
            seen = (int)hot.Scalar<long>("SELECT count(*) FROM Item WHERE Id > $after AND Id <= $upto", ("$after", after), ("$upto", upto));
            if (seen == 0) { repointed = 0; return after; }

            repointed = hot.Exec(@"
UPDATE Item SET SeriesId = (
    SELECT a.SeriesId FROM SeriesAlias a JOIN ComicDetail cd ON cd.ItemId = Item.Id WHERE a.ParsedKey = cd.ParsedSeriesKey)
WHERE Id > $after AND Id <= $upto
  AND EXISTS (SELECT 1 FROM SeriesAlias a JOIN ComicDetail cd ON cd.ItemId = Item.Id WHERE a.ParsedKey = cd.ParsedSeriesKey)
  AND SeriesId IS NOT (
    SELECT a.SeriesId FROM SeriesAlias a JOIN ComicDetail cd ON cd.ItemId = Item.Id WHERE a.ParsedKey = cd.ParsedSeriesKey)",
                ("$after", after), ("$upto", upto));
            return upto;
        }

        // ── phase 3: finish ──────────────────────────────────────────────────────────────────────────────

        private static void Finish(TargetWriter hot, Action<string> log, UnitCounts counts)
        {
            var merged = MergeMinorities(hot, log);
            counts.Bump("series-merged", merged);

            // Everything that could still name a merged-away id has been re-keyed, so the rows go. Book series
            // are exempt: they have no alias row by construction and this delete would take every one of them.
            var deleted = hot.Exec($"DELETE FROM Series WHERE Id NOT IN (SELECT DISTINCT SeriesId FROM SeriesAlias) AND {SeriesResolver.NotBookSql}");
            counts.Bump("series-deleted", deleted);

            RecomputeIssueCounts(hot);
            RecomputeYearSpans(hot);

            var rehomed = RehomeStrandedMarks(hot);
            if (rehomed > 0) log($"series: re-homed marks stranded on {rehomed} emptied husk series");
            counts.Bump("marks-rehomed", rehomed);

            Stamp(hot);
            log($"series: {merged} merged away, {deleted} rows deleted, registry stamped");
        }

        /// <summary>
        /// The merge map, RE-DERIVED from the alias table: a `Series` row whose own id never appears as a
        /// survivor was merged away, and its own ParsedKey names the survivor it merged into.
        /// </summary>
        public static Dictionary<int, int> MergeMap(TargetWriter hot)
        {
            var map = new Dictionary<int, int>();
            foreach (var (oldId, newId) in hot.Pairs($@"
SELECT s.Id, CAST(a.SeriesId AS TEXT) FROM Series s
JOIN SeriesAlias a ON a.ParsedKey = s.ParsedKey
WHERE s.Id NOT IN (SELECT DISTINCT SeriesId FROM SeriesAlias) AND a.SeriesId <> s.Id AND s.{SeriesResolver.NotBookSql}"))
                map[(int)oldId] = int.Parse(newId!);
            return map;
        }

        /// <summary>
        /// Move every series-keyed row off the merged-away ids onto their survivor, with the §6.2 collision rules:
        /// links and the series rating keep the SURVIVOR's row, marks OR their flags / keep the higher rating /
        /// join the notes, tags UNION, and the derived per-issue tables are simply re-keyed (their own jobs
        /// recompute them). Then append the redirect row. Idempotent: merged-away ids are deleted right after and
        /// are never reused, and an already-logged `SeriesMerge` row is left alone.
        /// </summary>
        private static int MergeMinorities(TargetWriter hot, Action<string> log)
        {
            var map = MergeMap(hot);
            if (map.Count == 0) return 0;

            foreach (var (oldId, newId) in map)
            {
                // Item.SeriesId — the paged re-point above reaches an item THROUGH its parsed key, so an item
                // with no ComicDetail row (every book has none) or whose key is not in the alias map would keep
                // pointing at the merged-away id and fail the foreign key on delete. Re-point those directly:
                // the merge map is the authority, and this is indexed on SeriesId.
                hot.Exec("UPDATE Item SET SeriesId = $new WHERE SeriesId = $old", ("$old", oldId), ("$new", newId));

                // MuSeriesLink — PK is SeriesId: the survivor's row wins, the loser's is dropped.
                hot.Exec("UPDATE MuSeriesLink SET SeriesId = $new WHERE SeriesId = $old AND NOT EXISTS (SELECT 1 FROM MuSeriesLink m WHERE m.SeriesId = $new)",
                    ("$old", oldId), ("$new", newId));
                hot.Exec("DELETE FROM MuSeriesLink WHERE SeriesId = $old", ("$old", oldId));

                // SeriesTag — union: re-key what the survivor does not already carry, drop the rest.
                hot.Exec(@"UPDATE OR IGNORE SeriesTag SET SeriesId = $new WHERE SeriesId = $old", ("$old", oldId), ("$new", newId));
                hot.Exec("DELETE FROM SeriesTag WHERE SeriesId = $old", ("$old", oldId));

                // Insight(Series) — append-only, so the rows simply move; books-resolve --insights re-picks the
                // current one by rank -> confidence -> recency, which IS the collapse rule.
                hot.Exec("UPDATE Insight SET SubjectId = $new WHERE SubjectKind = 1 AND SubjectId = $old", ("$old", oldId), ("$new", newId));

                // Rating(Series) — the survivor's row wins; an Override row is never silently discarded, so a
                // loser's override moves when the survivor has none of that source.
                hot.Exec(@"UPDATE OR IGNORE Rating SET TargetId = $new WHERE TargetKind = 1 AND TargetId = $old", ("$old", oldId), ("$new", newId));
                hot.Exec("DELETE FROM Rating WHERE TargetKind = 1 AND TargetId = $old", ("$old", oldId));

                // GroupMark(Series) — per user: OR the flags, keep the higher rating, join the notes.
                MergeGroupMarks(hot, oldId, newId);

                // The derived per-issue tables are re-keyed here so nothing points at a deleted id; their own
                // jobs (books-reading-order, books-containment) recompute the values.
                hot.Exec("UPDATE ReadingOrderEntry SET SeriesId = $new WHERE SeriesId = $old", ("$old", oldId), ("$new", newId));
                hot.Exec("UPDATE CollectionNode SET SeriesId = $new WHERE SeriesId = $old", ("$old", oldId), ("$new", newId));
                hot.Exec("UPDATE CollectedEditionSpan SET SeriesId = $new WHERE SeriesId = $old", ("$old", oldId), ("$new", newId));

                hot.Exec("INSERT INTO SeriesMerge (OldSeriesId, NewSeriesId, MergedAt) VALUES ($old, $new, $at) ON CONFLICT(OldSeriesId) DO NOTHING",
                    ("$old", oldId), ("$new", newId), ("$at", TargetWriter.ToDb(DateTime.UtcNow)));
            }
            log($"series: merged {map.Count} minority series onto their survivors");
            return map.Count;
        }

        /// <summary>Per user: OR the flags, keep the higher rating, join both notes (never lose a reader's note).</summary>
        private static void MergeGroupMarks(TargetWriter hot, int oldId, int newId)
        {
            var oldKey = oldId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var newKey = newId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            hot.Exec(@"
UPDATE GroupMark SET
    IsRead     = IsRead     | (SELECT o.IsRead     FROM GroupMark o WHERE o.UserId = GroupMark.UserId AND o.GroupType = 0 AND o.GroupKey = $old),
    WantToRead = WantToRead | (SELECT o.WantToRead FROM GroupMark o WHERE o.UserId = GroupMark.UserId AND o.GroupType = 0 AND o.GroupKey = $old),
    IsFavorite = IsFavorite | (SELECT o.IsFavorite FROM GroupMark o WHERE o.UserId = GroupMark.UserId AND o.GroupType = 0 AND o.GroupKey = $old),
    Rating     = max(coalesce(Rating, -1), coalesce((SELECT o.Rating FROM GroupMark o WHERE o.UserId = GroupMark.UserId AND o.GroupType = 0 AND o.GroupKey = $old), -1)),
    Notes      = nullif(trim(coalesce(Notes, '') || CASE
                    WHEN coalesce(Notes,'') <> '' AND coalesce((SELECT o.Notes FROM GroupMark o WHERE o.UserId = GroupMark.UserId AND o.GroupType = 0 AND o.GroupKey = $old), '') <> ''
                    THEN char(10) || char(10) ELSE '' END
                 || coalesce((SELECT o.Notes FROM GroupMark o WHERE o.UserId = GroupMark.UserId AND o.GroupType = 0 AND o.GroupKey = $old), '')), ''),
    UpdatedAt  = $at
WHERE GroupType = 0 AND GroupKey = $new
  AND EXISTS (SELECT 1 FROM GroupMark o WHERE o.UserId = GroupMark.UserId AND o.GroupType = 0 AND o.GroupKey = $old)",
                ("$old", oldKey), ("$new", newKey), ("$at", TargetWriter.ToDb(DateTime.UtcNow)));

            // A rating that was stored as -1 by the max() above means "neither side had one".
            hot.Exec("UPDATE GroupMark SET Rating = NULL WHERE GroupType = 0 AND GroupKey = $new AND Rating < 0", ("$new", newKey));

            // Whatever had no survivor row to merge into simply moves; the rest are now redundant.
            hot.Exec("UPDATE OR IGNORE GroupMark SET GroupKey = $new WHERE GroupType = 0 AND GroupKey = $old", ("$old", oldKey), ("$new", newKey));
            hot.Exec("DELETE FROM GroupMark WHERE GroupType = 0 AND GroupKey = $old", ("$old", oldKey));
        }

        /// <summary>
        /// One grouped UPDATE..FROM pass — never a per-row correlated subquery (O(series x items)).
        /// The zeroing is comic-only: it would otherwise blank every book series' count, which
        /// <see cref="BookSeriesLinkJob"/> owns and computes over Kind = 1.
        /// </summary>
        private static void RecomputeIssueCounts(TargetWriter hot)
        {
            hot.Exec($"UPDATE Series SET IssueCount = 0 WHERE {SeriesResolver.NotBookSql}");
            hot.Exec(@"
UPDATE Series SET IssueCount = t.cnt
FROM (SELECT i.SeriesId AS sid, count(*) AS cnt FROM Item i
      WHERE i.SeriesId IS NOT NULL AND i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0
      GROUP BY i.SeriesId) AS t
WHERE Series.Id = t.sid");
        }

        /// <summary>
        /// The run span over the RE-POINTED ids: the owned issues' resolved dates (reading-order date first, then
        /// the parsed year), with the ComicVine volume start year allowed to pull YearStart EARLIER but never
        /// later — a partial collection must not shrink a run. `IsOngoing` is the recency heuristic (newest issue
        /// dated this or last calendar year), so it decays with the calendar on its own.
        /// </summary>
        private static void RecomputeYearSpans(TargetWriter hot)
        {
            hot.Exec(@"
WITH yrs AS (
    SELECT i.SeriesId AS sid,
           coalesce(
               CASE WHEN CAST(substr(ro.ReadDate, 1, 4) AS INTEGER) BETWEEN 1900 AND 2100
                    THEN CAST(substr(ro.ReadDate, 1, 4) AS INTEGER) END,
               CASE WHEN cd.Year BETWEEN 1900 AND 2100 THEN cd.Year END) AS y
    FROM Item i
    LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
    LEFT JOIN ReadingOrderEntry ro ON ro.ItemId = i.Id
    WHERE i.SeriesId IS NOT NULL AND i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0
)
UPDATE Series SET YearStart = t.minY, YearEnd = t.maxY
FROM (SELECT sid, min(y) AS minY, max(y) AS maxY FROM yrs WHERE y IS NOT NULL GROUP BY sid) AS t
WHERE Series.Id = t.sid");

            hot.Exec(@"
UPDATE Series SET YearStart = v.StartYear
FROM CvVolume v
WHERE v.Id = Series.CvVolumeId
  AND v.StartYear BETWEEN 1900 AND 2100
  AND (Series.YearStart IS NULL OR v.StartYear < Series.YearStart)");

            // Comic rows only — the two UPDATEs above are already narrowed by their Kind = 0 subquery, but this
            // one is unconditional and would flip a recent book series to ongoing, which books do not have.
            hot.Exec($@"
UPDATE Series SET IsOngoing = CASE
    WHEN YearEnd IS NOT NULL AND YearEnd >= CAST(strftime('%Y','now') AS INTEGER) - 1 THEN 1 ELSE 0 END
WHERE {SeriesResolver.NotBookSql}");
        }

        /// <summary>
        /// Collected-edition suffixes a de-shatter strips when it collapses a husk onto its base series. A marked
        /// series that now holds NO issues and whose name is a populated series' name plus one of these is the
        /// stranded case: the reader's mark stayed on the emptied husk.
        /// </summary>
        private static readonly string[] CollectedSuffixes =
        {
            " - book", " - the book", " - omnibus", " - the omnibus", " - hc", " - tpb",
            " - deluxe edition", " - the deluxe edition", " - complete collection",
            " - the complete collection", " - collected edition", " - collected",
            " - compendium", " - library edition", " - absolute", " - the whole shebang",
            " - sketchbook", " - the sketchbook",
        };

        /// <summary>
        /// Self-healing reconcile for marks stranded by a re-point (NOT a canonical-key merge, which
        /// <see cref="MergeMinorities"/> already carries). Conservative: only an UNAMBIGUOUS single populated
        /// series with the base name is a safe target. Cheap — an instant no-op when no marked series is empty —
        /// and idempotent, because a re-homed husk carries no mark on the next run.
        /// </summary>
        private static int RehomeStrandedMarks(TargetWriter hot)
        {
            // Comic rows on both sides: a mark must never be re-homed from a comic onto a book series or back.
            var huskIds = hot.Pairs($@"
SELECT DISTINCT CAST(g.GroupKey AS INTEGER), s.Name FROM GroupMark g
JOIN Series s ON s.Id = CAST(g.GroupKey AS INTEGER)
WHERE g.GroupType = 0 AND s.IssueCount = 0 AND s.{SeriesResolver.NotBookSql}");
            if (huskIds.Count == 0) return 0;

            var populatedByNorm = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var (id, name) in hot.Pairs($"SELECT Id, coalesce(DisplayNameOverride, Name, '') FROM Series WHERE IssueCount > 0 AND {SeriesResolver.NotBookSql}"))
            {
                var key = NormName(name);
                if (!populatedByNorm.TryGetValue(key, out var list)) populatedByNorm[key] = list = new List<int>();
                list.Add((int)id);
            }

            var moved = 0;
            foreach (var (huskId, huskName) in huskIds)
            {
                var target = ResolveCanonicalForHusk(NormName(huskName), populatedByNorm);
                if (target == null || target.Value == (int)huskId) continue;
                MergeGroupMarks(hot, (int)huskId, target.Value);
                moved++;
            }
            return moved;
        }

        private static int? ResolveCanonicalForHusk(string normHusk, Dictionary<string, List<int>> populatedByNorm)
        {
            foreach (var suffix in CollectedSuffixes)
            {
                if (!normHusk.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var baseName = normHusk[..^suffix.Length].Trim();
                if (baseName.Length > 0 && populatedByNorm.TryGetValue(baseName, out var ids) && ids.Count == 1)
                    return ids[0];
            }
            return null;
        }

        private static string NormName(string? s) =>
            System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

        /// <summary>The three registry rows this job owns (fingerprint + row count + when).</summary>
        private static readonly string[] Stamped = { "Series", "SeriesAlias", "Item.SeriesId" };

        internal static void Stamp(TargetWriter hot)
        {
            var now = DateTime.UtcNow;
            foreach (var e in DerivedTables.All)
            {
                if (Array.IndexOf(Stamped, e.Name) < 0) continue;
                var rows = e.Name switch
                {
                    "Series" => hot.Scalar<long>("SELECT count(*) FROM Series"),
                    "SeriesAlias" => hot.Scalar<long>("SELECT count(*) FROM SeriesAlias"),
                    "Item.SeriesId" => hot.Scalar<long>("SELECT count(*) FROM Item WHERE SeriesId IS NOT NULL"),
                    _ => 0L,
                };
                hot.Upsert("DerivedTable", new
                {
                    Name = e.Name,
                    RebuildJob = e.RebuildJob,
                    InputFingerprint = ResolvePipeline.Fingerprint(hot, e.FingerprintSql),
                    LastRebuiltAt = now,
                    RowCount = (int)rows,
                });
            }
        }
    }
}
