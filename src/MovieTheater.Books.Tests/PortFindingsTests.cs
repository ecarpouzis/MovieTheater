using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The 2026-09-01 port findings, each pinned: the catalog cache expires on a data change, the kids book
    /// gate reads AI tags only, a merged-away series id redirects, dedup groups across pages and is idempotent,
    /// the derived jobs persist and clear their cursor, and the two import lanes validate what they read.
    /// </summary>
    public class PortFindingsTests
    {
        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static TargetWriter Writer(V1Fixture f) => new(f.HotPath, MappingContract.Load(), dryRun: false);

        // ── the catalog cache generation ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Invalidate_evicts_every_entry_bound_to_the_generation_and_leaves_the_rest()
        {
            var version = new CatalogCacheVersion();
            using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 50 });
            cache.Set("bound", "a", new MemoryCacheEntryOptions { Size = 1 }.AddExpirationToken(version.Token));
            cache.Set("unbound", "b", new MemoryCacheEntryOptions { Size = 1 });
            Assert.True(cache.TryGetValue("bound", out _));

            var gen = version.Invalidate();
            Assert.Equal(1, gen);
            Assert.False(cache.TryGetValue("bound", out _));
            Assert.True(cache.TryGetValue("unbound", out _));

            // A NEW entry binds to the new generation and survives until the next invalidation.
            cache.Set("bound2", "c", new MemoryCacheEntryOptions { Size = 1 }.AddExpirationToken(version.Token));
            Assert.True(cache.TryGetValue("bound2", out _));
            version.Invalidate();
            Assert.False(cache.TryGetValue("bound2", out _));
        }

        // ── the series-merge redirect ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_merged_away_series_id_follows_the_redirect_chain_to_its_survivor()
        {
            using var f = Migrated();
            await using var db = f.HotDb();
            var survivor = await db.Series.AsNoTracking().Select(s => s.Id).OrderBy(id => id).FirstAsync();

            db.SeriesMerges.Add(new SeriesMerge { OldSeriesId = 777_001, NewSeriesId = 777_002, MergedAt = DateTime.UtcNow });
            db.SeriesMerges.Add(new SeriesMerge { OldSeriesId = 777_002, NewSeriesId = survivor, MergedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            Assert.Equal(survivor, await SeriesRedirect.FollowAsync(db, 777_001));   // two hops
            Assert.Equal(survivor, await SeriesRedirect.FollowAsync(db, survivor));   // a live id is itself
            Assert.Equal(555_555, await SeriesRedirect.FollowAsync(db, 555_555));     // no row, no redirect: unchanged
        }

        // ── dedup across pages, idempotent, dry-run cursor in the caller ─────────────────────────────────

        [Fact]
        public async Task Dedup_groups_two_copies_that_sit_in_different_pages_and_a_rerun_adds_nothing()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                foreach (var sig in await db.ItemSignatures.ToListAsync()) { sig.ContentFingerprint = null; sig.PageSignature = null; sig.CoverPHash = null; }
                foreach (var id in new[] { 2, 5 })
                    (await db.ItemSignatures.FirstAsync(s => s.ItemId == id)).ContentFingerprint = "far-apart-twins";
                await db.SaveChangesAsync();
                await db.DuplicateMembers.ExecuteDeleteAsync();
                await db.DuplicateGroups.ExecuteDeleteAsync();
            }
            var dedup = new DuplicateDetectionService(NullLogger<DuplicateDetectionService>.Instance);

            // Dry run, ONE item per page, the cursor carried by the caller: the pair is reported exactly once.
            await using (var db = f.HotDb())
            {
                long? after = null;
                var claimed = new HashSet<int>();
                var groups = 0;
                while (true)
                {
                    var r = await dedup.RunBatchAsync(db, 1, apply: false, after: after, claimed: claimed);
                    if (r.Done) break;
                    groups += r.Groups;
                    after = r.NextCursor;
                }
                Assert.Equal(1, groups);
                Assert.Equal(0, await db.DuplicateGroups.CountAsync());   // a dry run writes nothing
                Assert.Null(await db.SystemStates.AsNoTracking().FirstOrDefaultAsync(s => s.Key == DuplicateDetectionService.CursorKey));
            }

            // Apply, still one item per page: one group, both members — then a re-run from the start finds nothing new.
            await using (var db = f.HotDb())
            {
                var groups = 0;
                while (true) { var r = await dedup.RunBatchAsync(db, 1, apply: true); if (r.Done) break; groups += r.Groups; }
                Assert.Equal(1, groups);
                var group = await db.DuplicateGroups.SingleAsync();
                Assert.Equal(new[] { 2, 5 }, (await db.DuplicateMembers.Where(m => m.DuplicateGroupId == group.Id).Select(m => m.ItemId).ToListAsync()).OrderBy(x => x).ToArray());

                await dedup.ResetAsync(db);
                var again = 0;
                while (true) { var r = await dedup.RunBatchAsync(db, 1000, apply: true); if (r.Done) break; again += r.Groups; }
                Assert.Equal(0, again);
                Assert.Equal(1, await db.DuplicateGroups.CountAsync());
            }
        }

        // ── the derived jobs' cursor ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Reading_order_persists_its_cursor_per_batch_and_clears_it_when_the_run_completes()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                ReadingOrderJob.RunAll(hot, 1, _ => { });
                Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM SystemState WHERE Key = $k", ("$k", ReadingOrderJob.CursorKey)));
                var written = hot.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry");
                Assert.True(written > 0);

                // A resume from a cursor past every series does no series at all — and still completes cleanly.
                var maxSeries = hot.Scalar<long>("SELECT max(Id) FROM Series");
                hot.Begin(); JobCursor.Write(hot, ReadingOrderJob.CursorKey, maxSeries); hot.Commit();
                var rows = ReadingOrderJob.RunAll(hot, 50, _ => { }, resume: true);
                Assert.Equal(0, rows);
                Assert.Equal(written, hot.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry"));
                Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM SystemState WHERE Key = $k", ("$k", ReadingOrderJob.CursorKey)));
            }

            // A dry-run writer walks and counts but writes nothing.
            using (var dry = f.Hot(dryRun: true))
            {
                var before = dry.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry");
                var rows = ReadingOrderJob.RunAll(dry, 50, _ => { });
                Assert.True(rows > 0);
                Assert.Equal(before, dry.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry"));
            }
        }

        // ── the insight import lane ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void Insight_lines_are_validated_against_the_bands_rules_and_the_tag_vocabulary()
        {
            var ok = InsightImportService.Parse(
                """{"subject":"series","id":1,"model":"claude-opus-4-1","confidence":"High","rating":82,"tags":{"genre":["Super Hero"],"audience":["all-ages"]}}""",
                "import:x#1");
            Assert.Null(ok.Error);
            Assert.Equal(SubjectKind.Series, ok.Kind);
            Assert.Equal(Confidence.High, ok.Confidence);
            Assert.Contains(("genre", "super-hero"), ok.Tags);
            Assert.Equal("import:x#1", ok.SourceKey);

            Assert.Contains("maturity", InsightImportService.Parse("""{"subject":"book","id":5,"model":"m"}""", "k").Error);
            Assert.Contains("rating 0", InsightImportService.Parse("""{"subject":"series","id":5,"model":"m","rating":0}""", "k").Error);
            Assert.Contains("vocabulary", InsightImportService.Parse("""{"subject":"series","id":5,"model":"m","tags":{"mood":["x"]}}""", "k").Error);
            Assert.Contains("subject", InsightImportService.Parse("""{"subject":"film","id":5,"model":"m"}""", "k").Error);
            Assert.NotNull(InsightImportService.Parse("not json", "k").Error);
        }

        [Fact]
        public async Task Insight_import_allocates_ids_in_band_appends_uncurrent_rows_and_is_idempotent_by_source_key()
        {
            using var f = Migrated();
            var path = Path.Combine(f.WorkDir, "insights.jsonl");
            await using (var db = f.HotDb())
            {
                var series = await db.Series.AsNoTracking().Select(s => s.Id).OrderBy(id => id).FirstAsync();
                var book = await db.Items.AsNoTracking().Where(i => i.Kind == ItemKind.Book).Select(i => i.Id).OrderBy(id => id).FirstAsync();
                await File.WriteAllLinesAsync(path, new[]
                {
                    "{\"subject\":\"series\",\"id\":" + series + ",\"model\":\"claude-sonnet-4-5\",\"confidence\":\"Medium\",\"rating\":70,\"synopsis\":\"A run.\",\"tags\":{\"genre\":[\"crime\"]}}",
                    "{\"subject\":\"book\",\"id\":" + book + ",\"model\":\"claude-sonnet-4-5\",\"confidence\":\"High\",\"maturity\":1,\"tags\":[{\"category\":\"theme\",\"value\":\"Coming of Age\"}]}",
                    """{"subject":"series","id":99999999,"model":"m","confidence":"Low"}""",
                });
            }
            var service = new InsightImportService(NullLogger<InsightImportService>.Instance);

            await using (var db = f.HotDb())
            {
                var before = await db.Insights.CountAsync();
                var dry = await service.RunBatchAsync(db, path, 10, apply: false, after: 0);
                Assert.Equal((3, 2, 0, 1), (dry.Processed, dry.Inserted, dry.Skipped, dry.Invalid));
                Assert.Equal(before, await db.Insights.CountAsync());

                var run = await service.RunBatchAsync(db, path, 10, apply: true, after: 0);
                Assert.Equal((2, 0, 1), (run.Inserted, run.Skipped, run.Invalid));
                Assert.Equal(0, run.Remaining);
                var added = await db.Insights.Where(n => n.SourceKey != null && n.SourceKey.StartsWith("import:insights.jsonl#")).ToListAsync();
                Assert.Equal(2, added.Count);
                Assert.All(added, n => Assert.False(n.IsCurrent));
                var s = added.Single(n => n.SubjectKind == SubjectKind.Series);
                var b = added.Single(n => n.SubjectKind == SubjectKind.Item);
                Assert.True(s.Id < Migration.Units.InsightIds.BookBase);
                Assert.InRange(b.Id, Migration.Units.InsightIds.BookBase, MigrationContext.CloneBase - 1);
                Assert.Equal(1, b.Maturity);
                Assert.Equal(2, s.Rank);
                Assert.Equal("coming-of-age", (await db.InsightTags.SingleAsync(t => t.InsightId == b.Id)).Value);

                var again = await service.RunBatchAsync(db, path, 10, apply: true, after: 0);
                Assert.Equal((0, 2, 1), (again.Inserted, again.Skipped, again.Invalid));
            }
        }

        // ── the curation lane ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Curation_rows_parse_quoted_values_and_reject_the_wrong_field_for_the_kind()
        {
            Assert.Equal(new[] { "item", "7", "eventName", "Crisis, Infinite" }, CurationImportService.ParseCsv("item,7,eventName,\"Crisis, Infinite\"").ToArray());
            var row = CurationImportService.ParseRow(2, "item,7,eventName,\"Crisis, Infinite\"");
            Assert.Null(row.Error);
            Assert.Equal(("item", 7, "eventname", "Crisis, Infinite"), (row.Kind, row.Id, row.Field, row.Value));
            Assert.Null(CurationImportService.ParseRow(3, "series,4,franchise,").Value);            // empty clears
            Assert.NotNull(CurationImportService.ParseRow(4, "series,4,eventName,X").Error);          // wrong kind/field pairing
            Assert.NotNull(CurationImportService.ParseRow(5, "movie,4,franchise,X").Error);
        }

        [Fact]
        public async Task Curation_import_sets_the_event_and_franchise_and_counts_a_repeat_as_unchanged()
        {
            using var f = Migrated();
            var path = Path.Combine(f.WorkDir, "curation.csv");
            int comic, series;
            await using (var db = f.HotDb())
            {
                comic = await db.ComicDetails.AsNoTracking().Select(d => d.ItemId).OrderBy(id => id).FirstAsync();
                series = await db.Series.AsNoTracking().Select(s => s.Id).OrderBy(id => id).FirstAsync();
            }
            await File.WriteAllLinesAsync(path, new[] { "kind,id,field,value", $"item,{comic},eventName,Secret Wars", $"series,{series},franchise,Batman", "series,99999999,franchise,X" });
            var service = new CurationImportService(NullLogger<CurationImportService>.Instance);
            await using var hot = f.HotDb();
            var dry = await service.RunBatchAsync(hot, path, 100, apply: false, after: 0);
            Assert.Equal((3, 2, 0, 1), (dry.Processed, dry.Applied, dry.Unchanged, dry.Invalid));
            var run = await service.RunBatchAsync(hot, path, 100, apply: true, after: 0);
            Assert.Equal((2, 0, 1), (run.Applied, run.Unchanged, run.Invalid));
            Assert.Equal("Secret Wars", (await hot.ComicDetails.AsNoTracking().FirstAsync(d => d.ItemId == comic)).EventName);
            Assert.Equal("Batman", (await hot.Series.AsNoTracking().FirstAsync(s => s.Id == series)).Franchise);
            var again = await service.RunBatchAsync(hot, path, 100, apply: true, after: 0);
            Assert.Equal((0, 2, 1), (again.Applied, again.Unchanged, again.Invalid));
        }
    }
}
