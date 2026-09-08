using System.Globalization;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-dedup-contained</c> — the fourth duplicate relationship, the one the de-duplication service has
    /// always declared and never produced: <c>ContainedIn</c>. A single issue is redundant with a collected
    /// edition that holds it, and no signature can see that. Only containment can.
    ///
    /// <para><b>Why this is separate from the signature pass.</b> <c>DuplicateDetectionService</c> groups files
    /// that ARE each other — same bytes, same pages, same cover. Two files that are not alike at all can still
    /// be the same reading: Saga #1-18 as eighteen floppies and as Book 1. That is not a fingerprint question,
    /// it is a containment question, so it is a different producer over the same tables.</para>
    ///
    /// <para><b>These groups are flag-only, deliberately.</b> <c>DuplicateDetectionService.ResolveAsync</c>
    /// refuses to bulk-resolve relationship 3 — owning both the floppies and the collection is legitimate, and
    /// often wanted. This job exists to SHOW the overlap, ranked, with its evidence; the decision stays a
    /// person's.</para>
    ///
    /// <para><b>What it will and will not trust.</b> Only a container whose winning span is <c>Curated</c> — a
    /// judged answer, not a provider leg the containment pass looked at and declined — and only at or above
    /// <see cref="MinConfidence"/>, unless the row is v1 gold quoting the edition's own indicia. A container
    /// carrying an undecided <see cref="ContainmentFlag"/>, or sitting in a series flagged
    /// <c>overlap-in-series</c> or <c>conflated-series</c>, is skipped outright: those are the shelves where
    /// several relaunch ladders each number from #1, and an issue "inside" a span there may belong to a
    /// different run entirely. Chunked by <c>Series.Id</c>, resumable, dry-run by default.</para>
    /// </summary>
    public static class ContainedDuplicateJob
    {
        public const string CursorKey = "books:dedup-contained:cursor";

        /// <summary>Below this a judged span is evidence for a human, not grounds for a duplicate group.</summary>
        public const double MinConfidence = 0.8;

        /// <summary>Flags that condemn a whole series: inside them an issue number is ambiguous.</summary>
        public static readonly string[] SeriesPoisonFlags = ["overlap-in-series", "conflated-series"];

        public sealed record BatchResult(int Series, long Remaining, long? NextCursor, int Groups, int Members, int Skipped)
        {
            public bool Done => NextCursor is null;
            public override string ToString() =>
                $"{{ processed: {Series}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", "
                + $"counts: {{ groups: {Groups}, members: {Members}, skippedContainers: {Skipped} }} }}  [dedup-contained]";
        }

        /// <summary>Wipe every group this job has produced, so a re-run after a containment rebuild is clean.</summary>
        public static int Reset(TargetWriter hot)
        {
            var n = hot.Scalar<long>(
                $"SELECT count(*) FROM DuplicateGroup WHERE Relationship = {DuplicateRelationship.ContainedIn}");
            hot.Exec($@"DELETE FROM DuplicateMember WHERE DuplicateGroupId IN
                        (SELECT Id FROM DuplicateGroup WHERE Relationship = {DuplicateRelationship.ContainedIn})");
            hot.Exec($"DELETE FROM DuplicateGroup WHERE Relationship = {DuplicateRelationship.ContainedIn}");
            JobCursor.Clear(hot, CursorKey);
            return (int)n;
        }

        public static BatchResult RunBatch(TargetWriter hot, long after, int batchSize, double minConfidence,
                                           Action<string>? sample = null)
        {
            var seriesIds = hot.Pairs(
                $"SELECT Id, '' FROM Series WHERE Id > {after} ORDER BY Id LIMIT {batchSize}")
                .Select(p => p.Item1).ToList();
            if (seriesIds.Count == 0) return new BatchResult(0, 0, null, 0, 0, 0);
            var idList = string.Join(",", seriesIds);

            // Series that a flag condemns wholesale, and items that carry an undecided flag of their own.
            // Both go through Item.SeriesId, never the flag's own denormalized copy: a series fold moves the
            // item and leaves the copy stale, which is how the span query used to lose a container's own span.
            var poisonList = string.Join(",", SeriesPoisonFlags.Select(f => $"'{f}'"));
            var poisonedSeries = hot.Pairs(
                $@"SELECT DISTINCT i.SeriesId, '' FROM ContainmentFlag f JOIN Item i ON i.Id = f.ItemId
                   WHERE f.ReviewState = '{ContainmentFlagImport.Pending}' AND f.Flag IN ({poisonList})
                     AND i.SeriesId IN ({idList})")
                .Select(p => p.Item1).ToHashSet();
            var flaggedItems = hot.Pairs(
                $@"SELECT DISTINCT f.ItemId, '' FROM ContainmentFlag f JOIN Item i ON i.Id = f.ItemId
                   WHERE f.ReviewState = '{ContainmentFlagImport.Pending}' AND i.SeriesId IN ({idList})")
                .Select(p => (int)p.Item1).ToHashSet();

            // Containers already grouped by an earlier run: this job is re-runnable without duplicating itself.
            var alreadyGrouped = hot.Pairs(
                $@"SELECT m.ItemId, '' FROM DuplicateMember m JOIN DuplicateGroup g ON g.Id = m.DuplicateGroupId
                   JOIN CollectionNode n ON n.ItemId = m.ItemId
                   WHERE g.Relationship = {DuplicateRelationship.ContainedIn} AND m.Role = '{RoleCollection}'
                     AND n.SeriesId IN ({idList})")
                .Select(p => (int)p.Item1).ToHashSet();

            // Every container in the batch that resolved to real issues, with the span that decided it.
            var containers = hot.Pairs(
                $@"SELECT n.ItemId,
                          n.SeriesId || char(31) || coalesce(n.SpanLabel,'') || char(31)
                       || coalesce(s.Confidence,'') || char(31) || coalesce(s.ProviderRef,'') || char(31)
                       || coalesce(CAST(s.IssueStart AS TEXT),'') || char(31) || coalesce(CAST(s.IssueEnd AS TEXT),'')
                       || char(31) || coalesce(s.EditionTitle,'')
                   FROM CollectionNode n
                   JOIN CollectedEditionSpan s ON s.ItemId = n.ItemId AND s.Source = {(int)EditionSource.Curated}
                   WHERE n.SeriesId IN ({idList}) AND n.TrackRole = {(int)TrackRole.Container}
                     AND n.ContainsCount > 0 AND n.SpanSource = {(int)SpanSource.Curated}");

            int groups = 0, members = 0, skipped = 0;
            foreach (var (itemIdL, payload) in containers)
            {
                var itemId = (int)itemIdL;
                var p = payload!.Split(TargetWriter.Sep);
                var seriesId = long.Parse(p[0], CultureInfo.InvariantCulture);
                var label = p[1];
                var conf = p[2].Length == 0 ? (double?)null : double.Parse(p[2], CultureInfo.InvariantCulture);
                var providerRef = p[3];
                var start = p[4];
                var end = p[5];
                var title = p[6];

                if (alreadyGrouped.Contains(itemId)) continue;
                var isGold = !providerRef.StartsWith("model:", StringComparison.Ordinal);
                if (poisonedSeries.Contains(seriesId) || flaggedItems.Contains(itemId)
                    || (!isGold && (conf ?? 0) < minConfidence))
                {
                    skipped++;
                    continue;
                }

                var children = hot.Pairs(
                    $@"SELECT i.Id, coalesce(r.ReadNumber,'') FROM CollectionNode n
                       JOIN Item i ON i.Id = n.ItemId
                       LEFT JOIN ReadingOrderEntry r ON r.ItemId = i.Id
                       WHERE n.ParentItemId = {itemId} AND n.TrackRole = {(int)TrackRole.Primary}
                         AND coalesce(i.IsExcluded, 0) = 0")
                    .Select(c => (int)c.Item1).Distinct().ToList();
                if (children.Count == 0) { skipped++; continue; }

                var evidence = $"{(title.Length > 0 ? title : label)} covers #{Trim(start)}-{Trim(end)}; "
                             + $"the library holds {children.Count} of those issues as separate files"
                             + (isGold ? " (span from the edition's own indicia)"
                                       : $" (judged span, confidence {conf?.ToString("0.##", CultureInfo.InvariantCulture)})");

                hot.Exec(
                    @"INSERT INTO DuplicateGroup (Relationship, Confidence, Evidence, SuggestedKeeperItemId, ReviewState, DetectedAt)
                      VALUES ($rel, $conf, $ev, NULL, $state, $now)",
                    ("$rel", DuplicateRelationship.ContainedIn),
                    ("$conf", isGold || (conf ?? 0) >= 0.85 ? "High" : "Medium"),
                    ("$ev", evidence), ("$state", "Pending"),
                    ("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
                var groupId = hot.Scalar<long>("SELECT last_insert_rowid()");

                hot.Exec(@"INSERT INTO DuplicateMember (DuplicateGroupId, ItemId, Role, SoleFileInFolder)
                           VALUES ($g, $i, $r, 0)",
                         ("$g", groupId), ("$i", itemId), ("$r", RoleCollection));
                foreach (var child in children)
                    hot.Exec(@"INSERT INTO DuplicateMember (DuplicateGroupId, ItemId, Role, SoleFileInFolder)
                               VALUES ($g, $i, $r, 0)",
                             ("$g", groupId), ("$i", child), ("$r", RoleContained));

                groups++;
                members += children.Count + 1;
                sample?.Invoke($"  group {groupId}: item {itemId} {evidence}");
            }

            var next = seriesIds[^1];
            return new BatchResult(seriesIds.Count,
                hot.Scalar<long>($"SELECT count(*) FROM Series WHERE Id > {next}"), next, groups, members, skipped);
        }

        public static (int Groups, int Members, int Skipped) RunAll(TargetWriter hot, int batchSize, double minConfidence,
            Action<string> log, bool resume = false, int sampleTop = 25)
        {
            long cursor = resume ? JobCursor.Read(hot, CursorKey) : 0;
            int groups = 0, members = 0, skipped = 0, printed = 0;
            while (true)
            {
                hot.Begin();
                var r = RunBatch(hot, cursor, batchSize, minConfidence, s => { if (printed++ < sampleTop) log(s); });
                if (r.NextCursor is long next) JobCursor.Write(hot, CursorKey, next);
                hot.Commit();
                groups += r.Groups; members += r.Members; skipped += r.Skipped;
                if (r.Done) break;
                log(r.ToString());
                cursor = r.NextCursor!.Value;
            }
            hot.Begin();
            JobCursor.Clear(hot, CursorKey);
            hot.Commit();
            return (groups, members, skipped);
        }

        /// <summary>The member roles this job writes. `Keeper`/`Duplicate` are the signature pass's verdict words.</summary>
        public const string RoleCollection = "Collection";
        public const string RoleContained = "Contained";

        private static string Trim(string n) =>
            double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d.ToString("0.##", CultureInfo.InvariantCulture) : n;
    }

    /// <summary>
    /// The <c>DuplicateGroup.Relationship</c> codes, mirrored from <c>DuplicateDetectionService</c> so this job
    /// does not depend on the EF service (and so the number 3 appears somewhere with a name on it).
    /// </summary>
    public static class DuplicateRelationship
    {
        public const int IdenticalFile = 0, IdenticalContents = 1, SameComicDifferentScan = 2, ContainedIn = 3;
    }
}
