using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// Slice 5's operator-facing services against the migrated synthetic file: the Calibre importer, the
    /// library-rating blend, dedup, tag normalization and the series reconciliation edits.
    /// </summary>
    public class AdminServiceTests
    {
        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static TargetWriter Writer(V1Fixture f) => new(f.HotPath, MappingContract.Load(), dryRun: false);

        // ── Calibre import ───────────────────────────────────────────────────────────────────────────────

        /// <summary>A minimal but REAL Calibre schema — the tables and join tables the importer's query reads.</summary>
        private static string BuildCalibre(V1Fixture f)
        {
            var dir = Path.Combine(f.WorkDir, "calibre");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "metadata.db");
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            conn.Open();
            void Exec(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            Exec(@"
CREATE TABLE books (id INTEGER PRIMARY KEY, title TEXT, path TEXT, pubdate TEXT, series_index REAL);
CREATE TABLE identifiers (id INTEGER PRIMARY KEY, book INTEGER, type TEXT, val TEXT);
CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE books_authors_link (book INTEGER, author INTEGER);
CREATE TABLE series (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE books_series_link (book INTEGER, series INTEGER);
CREATE TABLE publishers (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE books_publishers_link (book INTEGER, publisher INTEGER);
CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE books_tags_link (book INTEGER, tag INTEGER);
CREATE TABLE languages (id INTEGER PRIMARY KEY, lang_code TEXT);
CREATE TABLE books_languages_link (book INTEGER, lang_code INTEGER);
CREATE TABLE comments (id INTEGER PRIMARY KEY, book INTEGER, text TEXT);
CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER, format TEXT, name TEXT);");
            // Book 844 is the one calibre_link.json pairs with item 101 in the fixture.
            Exec(@"
INSERT INTO books (id, title, path, pubdate, series_index) VALUES (844, 'Brave New World', 'Aldous Huxley/Brave New World (844)', '2006-10-17', 3.0);
INSERT INTO identifiers (book, type, val) VALUES (844, 'isbn', '9780060850524'), (844, 'google', 'abc');
INSERT INTO authors (id, name) VALUES (1, 'Aldous Huxley'), (2, 'A Collaborator');
INSERT INTO books_authors_link (book, author) VALUES (844, 1), (844, 2);
INSERT INTO series (id, name) VALUES (1, 'Dystopias');
INSERT INTO books_series_link (book, series) VALUES (844, 1);
INSERT INTO publishers (id, name) VALUES (1, 'Harper Perennial');
INSERT INTO books_publishers_link (book, publisher) VALUES (844, 1);
INSERT INTO tags (id, name) VALUES (1, 'Classics'), (2, 'Science Fiction');
INSERT INTO books_tags_link (book, tag) VALUES (844, 1), (844, 2);
INSERT INTO languages (id, lang_code) VALUES (1, 'eng');
INSERT INTO books_languages_link (book, lang_code) VALUES (844, 1);
INSERT INTO comments (id, book, text) VALUES (1, 844, 'A dystopia.');
INSERT INTO data (id, book, format, name) VALUES (1, 844, 'EPUB', 'Brave New World - Aldous Huxley');
INSERT INTO books (id, title, path, series_index) VALUES (999, 'Not In The Library', 'Nobody/Nothing (999)', 1.0);
INSERT INTO data (id, book, format, name) VALUES (2, 999, 'EPUB', 'Nothing');");
            return path;
        }

        private static CalibreImportService Importer() => new(NullLogger<CalibreImportService>.Instance);

        [Fact]
        public async Task TheCalibreImportFillsTheSeriesNameV1NeverHad()
        {
            using var f = Migrated();
            var metadata = BuildCalibre(f);

            await using (var db = f.HotDb())
            {
                // On the REAL file this is NULL for all 22,084 books (v1 had no column for it). The synthetic
                // fixture carries v1's own Comics.SeriesName instead, so the assertion here is that the import
                // REPLACES whatever was there with Calibre's own answer.
                Assert.Equal("Classics", (await db.BookDetails.FirstAsync(b => b.ItemId == 101)).SeriesName);
            }

            await using (var db = f.HotDb())
            {
                var r = await Importer().RunBatchAsync(db, metadata, f.CalibreLinkPath, 100);
                Assert.Equal(2, r.Processed);
                Assert.Equal(1, r.Matched);        // book 844 pairs with item 101 through the link file
                Assert.Equal(1, r.Unmatched);      // book 999 is not in this library and is REPORTED, not guessed
            }

            await using (var after = f.HotDb())
            {
                var detail = await after.BookDetails.FirstAsync(b => b.ItemId == 101);
                Assert.Equal("Dystopias", detail.SeriesName);
                Assert.Equal(3.0, detail.SeriesIndex);
                Assert.Equal("Harper Perennial", detail.Publisher);
                Assert.Equal("2006-10-17", detail.PublishedOn);
                Assert.Equal("eng", detail.Language);
                Assert.Equal("9780060850524", detail.Isbn);
                Assert.Equal(844, (await after.Items.FirstAsync(i => i.Id == 101)).CalibreBookId);

                // Calibre's " & "-joined authors become ROWS, which is what lets either name find the book.
                var credits = await after.ItemCredits.Where(c => c.ItemId == 101 && c.Source == TagSource.Calibre).ToListAsync();
                Assert.Equal(2, credits.Count);
                Assert.All(credits, c => Assert.Equal("Author", c.Role));
                Assert.Contains(credits, c => c.Name == "A Collaborator");

                var tags = await after.ItemTags.Where(t => t.ItemId == 101 && t.Source == TagSource.Calibre).ToListAsync();
                Assert.Equal(2, tags.Count);
                Assert.Contains(tags, t => t.Value == "Science Fiction");
            }
        }

        [Fact]
        public async Task TheCalibreImportIsIdempotentAndItsDryRunWritesNothing()
        {
            using var f = Migrated();
            var metadata = BuildCalibre(f);

            await using (var db = f.HotDb())
            {
                await Importer().RunBatchAsync(db, metadata, f.CalibreLinkPath, 100, apply: false);
                Assert.Equal("Classics", (await db.BookDetails.AsNoTracking().FirstAsync(b => b.ItemId == 101)).SeriesName);
                await Importer().ResetAsync(db);
            }

            await using (var db = f.HotDb()) await Importer().RunBatchAsync(db, metadata, f.CalibreLinkPath, 100);
            var once = await SnapshotBookAsync(f);
            await using (var db = f.HotDb())
            {
                await Importer().ResetAsync(db);
                await Importer().RunBatchAsync(db, metadata, f.CalibreLinkPath, 100);
            }
            Assert.Equal(once, await SnapshotBookAsync(f));
        }

        [Fact]
        public void TheCalibreLinkFileIsReadForItsIdsOnly()
        {
            using var f = Migrated();
            var links = CalibreImportService.ReadLinks(f.CalibreLinkPath);
            Assert.Single(links);
            Assert.Equal(101, links[0].ComicId);
            Assert.Equal(844, links[0].CalibreId);
            // A missing file is not an error — the importer falls back to the stored id and then the path.
            Assert.Empty(CalibreImportService.ReadLinks(Path.Combine(f.WorkDir, "nope.json")));
        }

        private static async Task<string> SnapshotBookAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            var d = await db.BookDetails.AsNoTracking().FirstAsync(b => b.ItemId == 101);
            var credits = await db.ItemCredits.AsNoTracking().Where(c => c.ItemId == 101).OrderBy(c => c.Ordinal).Select(c => c.Name).ToListAsync();
            var tags = await db.ItemTags.AsNoTracking().Where(t => t.ItemId == 101).OrderBy(t => t.Value).Select(t => t.Value).ToListAsync();
            return $"{d.SeriesName}|{d.SeriesIndex}|{d.Publisher}|{string.Join(",", credits)}|{string.Join(",", tags)}";
        }

        // ── the library rating blend ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheBlendMatchesAHandComputedRowAndAnOverrideWinsOutright()
        {
            using var f = Migrated();
            using (var hot = Writer(f)) LibraryRatingJob.RunAll(hot, 500, _ => { });

            using var w = f.Hot();
            var population = LibraryRatingJob.Measure(w);

            // The fixture hand-sets an override of 95 on series 2, and an override REPLACES the blend outright.
            Assert.Equal(95, w.Scalar<long>($"SELECT Value FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 2 AND Source = {(int)RatingSource.Override}"));
            Assert.Equal(95, w.Scalar<long>($"SELECT Value FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 2 AND Source = {(int)RatingSource.Library}"));
            Assert.Contains("adjudication: hand-set", w.Scalar<string>($"SELECT Note FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 2 AND Source = {(int)RatingSource.Library}"));

            // Underneath it, series 2's only real signal is a High-confidence insight rating of 92. One weighted
            // part is its own mean, so with the override gone the blend must land exactly on it.
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Exec($"DELETE FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 2 AND Source = {(int)RatingSource.Override}");
                hot.Commit();
                LibraryRatingJob.RunAll(hot, 500, _ => { });
            }
            Assert.Equal(92, w.Scalar<long>($"SELECT Value FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 2 AND Source = {(int)RatingSource.Library}"));
            var note = w.Scalar<string>($"SELECT Note FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 2 AND Source = {(int)RatingSource.Library}");
            Assert.Contains("model assessment 92/100 (High confidence)", note);
            Assert.Contains("[insight]", note);

            // Item 1 has BOTH a LOCG rating (4.2 over 12 votes) and its series' score; the hand computation:
            var seriesScore = (double)w.Scalar<long>($"SELECT Value FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 1 AND Source = {(int)RatingSource.Library}");
            const double rating = 4.2, votes = 12;
            var shrunk = (rating * votes + LibraryRatingJob.IssueShrinkK * population.Mean) / (votes + LibraryRatingJob.IssueShrinkK);
            var locgScore = population.To100(shrunk);
            var locgWeight = Math.Min(1.0, Math.Log10(1 + votes) / 2.5);
            var expected = (int)Math.Round(Math.Clamp((locgScore * locgWeight + seriesScore * 0.8) / (locgWeight + 0.8), 1, 99));
            Assert.Equal(expected, (int)w.Scalar<long>($"SELECT Value FROM Rating WHERE TargetKind = {(int)SubjectKind.Item} AND TargetId = 1 AND Source = {(int)RatingSource.Library}"));
        }

        [Fact]
        public void AnOverrideReplacesTheBlendRatherThanAveragingWithIt()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Upsert("Rating", new
                {
                    TargetKind = SubjectKind.Item, TargetId = 1, Source = RatingSource.Override,
                    Value = 12, Note = "hand-set", IsOverride = true, ModelId = "admin", GeneratedAt = DateTime.UtcNow,
                });
                hot.Commit();
                LibraryRatingJob.RunAll(hot, 500, _ => { });
            }
            using var w = f.Hot();
            Assert.Equal(12, w.Scalar<long>($"SELECT Value FROM Rating WHERE TargetKind = {(int)SubjectKind.Item} AND TargetId = 1 AND Source = {(int)RatingSource.Library}"));
            Assert.Contains("adjudication: hand-set", w.Scalar<string>($"SELECT Note FROM Rating WHERE TargetKind = {(int)SubjectKind.Item} AND TargetId = 1 AND Source = {(int)RatingSource.Library}"));
        }

        [Fact]
        public void AwardTagsBoostAfterTheMeanAndAreCappedAtFive()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                // The fixture hand-sets an override on series 2, and an override REPLACES the blend — so it has
                // to go before the boost can be observed at all.
                hot.Exec($"DELETE FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 2 AND Source = {(int)RatingSource.Override}");
                foreach (var tag in new[] { "pulitzer", "eisner-winner", "hugo-winner" })
                    hot.Upsert("SeriesTag", new { SeriesId = 2, Category = "award", Value = tag, Source = TagSource.AI });
                hot.Commit();
                LibraryRatingJob.RunAll(hot, 500, _ => { });
            }
            using var w = f.Hot();
            // 92 + min(5, 4+3+3) = 97, not 102.
            Assert.Equal(97, w.Scalar<long>($"SELECT Value FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND TargetId = 2 AND Source = {(int)RatingSource.Library}"));
        }

        [Fact]
        public void TheBlendIsIdempotentAndStampsItsRegistryRow()
        {
            using var f = Migrated();
            const string sql = "SELECT TargetId, TargetKind || '|' || Value FROM Rating WHERE Source = 4 ORDER BY TargetKind, TargetId";
            using (var hot = Writer(f)) LibraryRatingJob.RunAll(hot, 500, _ => { });
            var once = Snapshot(f, sql);
            using (var hot = Writer(f)) LibraryRatingJob.RunAll(hot, 500, _ => { });
            Assert.Equal(once, Snapshot(f, sql));

            using var w = f.Hot();
            Assert.Equal("books-library-ratings", w.Scalar<string>("SELECT RebuildJob FROM DerivedTable WHERE Name = 'Rating(Source=Library)'"));
        }

        // ── dedup ────────────────────────────────────────────────────────────────────────────────────────

        private static DuplicateDetectionService Dedup() => new(NullLogger<DuplicateDetectionService>.Instance);

        [Fact]
        public async Task DedupGroupsOnAPresentSignatureAndNeverOnAMissingOne()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                // Items 2 and 3 are the same file twice; item 4 has no signature at all.
                foreach (var id in new[] { 2, 3 })
                {
                    var sig = await db.ItemSignatures.FirstAsync(s => s.ItemId == id);
                    sig.ContentFingerprint = "same-bytes";
                }
                (await db.ItemSignatures.FirstAsync(s => s.ItemId == 4)).ContentFingerprint = null;
                (await db.ItemSignatures.FirstAsync(s => s.ItemId == 5)).ContentFingerprint = null;
                await db.SaveChangesAsync();
                await db.DuplicateMembers.ExecuteDeleteAsync();
                await db.DuplicateGroups.ExecuteDeleteAsync();
            }

            await using (var db = f.HotDb())
            {
                var r = await Dedup().RunBatchAsync(db, 1000);
                Assert.Equal(1, r.Groups);        // exactly one — two nulls are NOT a match
                Assert.Equal(1, r.Duplicates);
            }

            await using var after = f.HotDb();
            var group = await after.DuplicateGroups.FirstAsync();
            Assert.Equal(DuplicateDetectionService.IdenticalFile, group.Relationship);
            Assert.Equal("Pending", group.ReviewState);
            var members = await after.DuplicateMembers.Where(m => m.DuplicateGroupId == group.Id).ToListAsync();
            Assert.Equal(2, members.Count);
        }

        [Fact]
        public void TheKeeperHeuristicPutsTheReadersCopyFirst()
        {
            var plain = new DuplicateDetectionService.Candidate(1, @"\\x\Series\a.cbz", "a.cbz", 900, 30, 1, "fp", null, null, 90000, true, false);
            var read = new DuplicateDetectionService.Candidate(2, @"\\x\Unsorted\b.cbz", "b.cbz", 100, 30, 2, "fp", null, null, 10, false, true);
            var keeper = DuplicateDetectionService.PickKeeper(new List<DuplicateDetectionService.Candidate> { plain, read }, DuplicateDetectionService.IdenticalFile);
            Assert.Equal(2, keeper!.Id);   // the copy carrying the reader's state wins even from a holding folder
        }

        [Fact]
        public void WithNoReaderStateTheCanonicalFolderBeatsTheEventTreeAndTheHoldingFolder()
        {
            var canonical = new DuplicateDetectionService.Candidate(1, @"\\x\DC\Batman (1940)\a.cbz", "a.cbz", 100, 30, 1, "fp", null, null, 10, false, false);
            var eventTree = new DuplicateDetectionService.Candidate(2, @"\\x\DC\#DC Events\a.cbz", "a.cbz", 900, 30, 2, "fp", null, null, 90000, true, false);
            var unsorted = new DuplicateDetectionService.Candidate(3, @"\\x\Unsorted\a.cbz", "a.cbz", 900, 30, 3, "fp", null, null, 90000, true, false);
            var keeper = DuplicateDetectionService.PickKeeper(new List<DuplicateDetectionService.Candidate> { eventTree, unsorted, canonical }, DuplicateDetectionService.IdenticalFile);
            Assert.Equal(1, keeper!.Id);
            Assert.True(DuplicateDetectionService.LooksLikeEventTree(@"\\x\DC\#DC Events\a.cbz"));
            Assert.True(DuplicateDetectionService.LooksUnsorted(@"\\x\Unsorted\a.cbz"));
        }

        [Fact]
        public void AContainmentGroupSuggestsNoKeeperBecauseOwningBothIsLegitimate() =>
            Assert.Null(DuplicateDetectionService.PickKeeper(
                new List<DuplicateDetectionService.Candidate>
                {
                    new(1, "a", "a", 1, 30, 1, null, null, null, 0, false, false),
                    new(2, "b", "b", 1, 300, 1, null, null, null, 0, false, false),
                },
                DuplicateDetectionService.ContainedIn));

        [Fact]
        public async Task ResolvingAGroupHidesTheLosersAndTouchesNoFile()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                foreach (var id in new[] { 2, 3 }) (await db.ItemSignatures.FirstAsync(s => s.ItemId == id)).ContentFingerprint = "same-bytes";
                // Item 3 is the fixture's already-excluded shadow duplicate; un-hide it so resolving has
                // something to hide.
                (await db.Items.FirstAsync(i => i.Id == 3)).IsExcluded = false;
                await db.SaveChangesAsync();
                await db.DuplicateMembers.ExecuteDeleteAsync();
                await db.DuplicateGroups.ExecuteDeleteAsync();
                await Dedup().RunBatchAsync(db, 1000);
            }

            await using (var db = f.HotDb())
            {
                var group = await db.DuplicateGroups.FirstAsync();
                var hidden = await Dedup().ResolveAsync(db, group.Id, keeperItemId: 2);
                Assert.Equal(1, hidden);
            }

            await using var after = f.HotDb();
            Assert.False((await after.Items.FirstAsync(i => i.Id == 2)).IsExcluded);
            var loser = await after.Items.FirstAsync(i => i.Id == 3);
            Assert.True(loser.IsExcluded);
            // The Directory drill still lists it: the file genuinely lives in that folder.
            Assert.True(loser.KeepInDirectory);
            Assert.Equal("Resolved", (await after.DuplicateGroups.FirstAsync()).ReviewState);
        }

        // ── tag normalization ────────────────────────────────────────────────────────────────────────────

        private static DataNormalizationService Normalizer() => new(NullLogger<DataNormalizationService>.Instance);

        [Fact]
        public async Task NormalizationEditsTheInputTagsSoTheFoldPicksThemUp()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                // The fixture's alias map already folds genre:science-fiction onto sci-fi.
                Assert.True(await db.InsightTags.AnyAsync(t => t.Category == "genre" && t.Value == "science-fiction"));

                var dry = await Normalizer().NormalizeTagsAsync(db, apply: false);
                Assert.True(dry.Total > 0);
                Assert.True(await db.InsightTags.AnyAsync(t => t.Value == "science-fiction"), "a dry run must write nothing");

                var applied = await Normalizer().NormalizeTagsAsync(db, apply: true);
                Assert.True(applied.AliasesApplied > 0);
            }

            await using var after = f.HotDb();
            Assert.False(await after.InsightTags.AnyAsync(t => t.Category == "genre" && t.Value == "science-fiction"));
            Assert.True(await after.InsightTags.AnyAsync(t => t.Category == "genre" && t.Value == "sci-fi"));
        }

        [Fact]
        public async Task EraDateRangesAndCrossCategoryPollutantsAreDropped()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                var insightId = await db.Insights.Where(i => i.SubjectKind == SubjectKind.Series).Select(i => i.Id).FirstAsync();
                db.InsightTags.Add(new InsightTag { InsightId = insightId, Category = "era", Value = "1986-1992" });
                db.InsightTags.Add(new InsightTag { InsightId = insightId, Category = "genre", Value = "all-ages" });
                db.InsightTags.Add(new InsightTag { InsightId = insightId, Category = "tone", Value = "mature" });
                await db.SaveChangesAsync();
                await Normalizer().NormalizeTagsAsync(db, apply: true);
            }

            await using var after = f.HotDb();
            Assert.False(await after.InsightTags.AnyAsync(t => t.Value == "1986-1992"));
            Assert.False(await after.InsightTags.AnyAsync(t => t.Category == "genre" && t.Value == "all-ages"));
            // tone:mature MOVES to audience:mature rather than being dropped — it is an audience descriptor.
            Assert.False(await after.InsightTags.AnyAsync(t => t.Category == "tone" && t.Value == "mature"));
            Assert.True(await after.InsightTags.AnyAsync(t => t.Category == "audience" && t.Value == "mature"));
        }

        [Fact]
        public async Task AnAliasCannotPointAtItself()
        {
            using var f = Migrated();
            await using var db = f.HotDb();
            await Assert.ThrowsAsync<ArgumentException>(() => Normalizer().UpsertAliasAsync(db, "genre", "noir", "noir"));
            var row = await Normalizer().UpsertAliasAsync(db, "genre", "Film Noir", "noir");
            Assert.Equal("film noir", row.AliasTag);
            Assert.True(await Normalizer().DeleteAliasAsync(db, "genre", "film noir"));
        }

        // ── series reconciliation ────────────────────────────────────────────────────────────────────────

        private static SeriesMismatchService Mismatch() => new(NullLogger<SeriesMismatchService>.Instance);
        private static SeriesNamesService Names() => new(NullLogger<SeriesNamesService>.Instance);

        [Fact]
        public async Task ClearingALinkLeavesTheRowAsClearedSoARescrapeCannotRemakeIt()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                var result = await Mismatch().ClearLinkAsync(db, "2000 AD", Provider.Cv, "tester");
                Assert.Equal(1, result.RowsChanged);
                Assert.True(result.RebuildRequired);
            }
            await using var after = f.HotDb();
            var link = await after.SeriesKeyLinks.FirstAsync(l => l.ParsedKey == "2000 AD" && l.Provider == Provider.Cv);
            Assert.Equal(LinkStatus.Cleared, link.Status);
            Assert.Null(link.ProviderKey);
            // The decision is audited with an undo payload.
            Assert.True(await after.SeriesInferenceDecisions.AnyAsync(d => d.Action == "clear-link" && d.UndoJson != null));
        }

        [Fact]
        public async Task FoldingAParsedKeyEditsTheInputAndTheRebuildThenMergesTheSeries()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                var result = await Mismatch().FoldParsedKeyAsync(db, "Doppelganger", "Batman", "tester");
                Assert.Equal(1, result.RowsChanged);
            }

            // The fold touched ComicDetail only; Series is still stale until the identity job runs.
            await using (var db = f.HotDb())
                Assert.Equal("Batman", (await db.ComicDetails.FirstAsync(d => d.ItemId == 6)).ParsedSeriesKey);

            using (var hot = Writer(f)) SeriesRebuildJob.RunAll(hot, 100, _ => { });

            await using var after = f.HotDb();
            Assert.Equal(2, (await after.Items.FirstAsync(i => i.Id == 6)).SeriesId);
            Assert.Equal(3, (await after.Series.FirstAsync(s => s.Id == 2)).IssueCount);
            // Series 3 keeps its own canonical key (its parsed spelling still exists), so it is not MERGED —
            // it is simply EMPTY now. Removing an empty series is books-series-prune's job, not the rebuild's,
            // because an empty series may still be one the reader has marked.
            Assert.Equal(0, (await after.Series.FirstAsync(s => s.Id == 3)).IssueCount);
        }

        [Fact]
        public async Task AFoldCanBeRevertedFromItsStoredUndoPayload()
        {
            using var f = Migrated();
            int decisionId;
            await using (var db = f.HotDb())
            {
                await Mismatch().UnifyFolderAsync(db, 5, "Unified", "tester");
                decisionId = await db.SeriesInferenceDecisions.OrderByDescending(d => d.Id).Select(d => d.Id).FirstAsync();
                Assert.True(await db.ComicDetails.AnyAsync(d => d.ParsedSeriesKey == "Unified"));
            }
            await using (var db = f.HotDb())
            {
                var result = await Mismatch().RevertDecisionAsync(db, decisionId, "tester");
                Assert.True(result.RowsChanged > 0);
            }
            await using var after = f.HotDb();
            Assert.False(await after.ComicDetails.AnyAsync(d => d.ParsedSeriesKey == "Unified"));
            Assert.Equal("Reverted", (await after.SeriesInferenceDecisions.FirstAsync(d => d.Id == decisionId)).State);
        }

        [Fact]
        public async Task ADisplayOverrideSurvivesTheIdentityRebuild()
        {
            using var f = Migrated();
            await using (var db = f.HotDb()) await Names().SetOverrideAsync(db, 1, "  2000 AD (Prog)  ");
            using (var hot = Writer(f)) SeriesRebuildJob.RunAll(hot, 100, _ => { });
            await using var after = f.HotDb();
            var series = await after.Series.FirstAsync(s => s.Id == 1);
            Assert.Equal("2000 AD (Prog)", series.DisplayNameOverride);
            Assert.Equal("2000 AD (Prog)", series.Name);
        }

        [Fact]
        public async Task PruneNeverRemovesASeriesTheReaderHasMarked()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                // An empty series the reader marked, and an empty one nobody touched.
                db.Series.Add(new Series { Id = 900, CanonicalKey = "parsed:marked-husk", ParsedKey = "Marked Husk", Name = "Marked Husk" });
                db.Series.Add(new Series { Id = 901, CanonicalKey = "parsed:lonely-husk", ParsedKey = "Lonely Husk", Name = "Lonely Husk" });
                await db.SaveChangesAsync();
                db.GroupMarks.Add(new GroupMark { UserId = 1, GroupType = GroupType.Series, GroupKey = "900", IsRead = true, UpdatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();

                var (candidates, deleted) = await Names().PruneAsync(db, apply: false);
                Assert.Equal(0, deleted);
                Assert.True(candidates >= 1);

                var (_, actuallyDeleted) = await Names().PruneAsync(db, apply: true);
                Assert.True(actuallyDeleted >= 1);
            }
            await using var after = f.HotDb();
            Assert.True(await after.Series.AnyAsync(s => s.Id == 900), "a marked series is never pruned");
            Assert.False(await after.Series.AnyAsync(s => s.Id == 901));
        }

        [Fact]
        public async Task SplitOvermatchFindsASeriesHoldingFarMoreThanItsVolumeClaims()
        {
            using var f = Migrated();
            await using (var db = f.HotDb())
            {
                (await db.Series.FirstAsync(s => s.Id == 2)).IssueCount = 2000;
                await db.SaveChangesAsync();
            }
            await using var db2 = f.HotDb();
            var rows = await Names().SplitOvermatchAsync(db2, ratio: 2.0, minIssues: 20);
            Assert.Contains(rows, r => r.SeriesId == 2 && r.Held == 2000 && r.Claimed == 715);
        }

        [Fact]
        public async Task TheMismatchSummaryCountsWhatTheOperatorTriagesFirst()
        {
            using var f = Migrated();
            await using var db = f.HotDb();
            var summary = await Mismatch().SummaryAsync(db);
            Assert.True(summary.Series > 0);
            Assert.True(summary.LinkedSeries > 0);
            Assert.NotNull(await Mismatch().LinkCandidatesAsync(db, "2000 AD", Provider.Cv));
            Assert.Null(await Mismatch().LinkCandidatesAsync(db, "no such key", Provider.Cv));
        }

        [Fact]
        public async Task MarkingAReviewIsTriageAndAsksForNoRebuild()
        {
            using var f = Migrated();
            await using var db = f.HotDb();
            var result = await Mismatch().MarkReviewedAsync(db, "link", "2000 AD", "Fixed", "checked by hand", "tester");
            Assert.False(result.RebuildRequired);
            Assert.Equal("Fixed", (await db.SeriesMatchReviews.FirstAsync(r => r.Scope == "link" && r.Key == "2000 AD")).State);
        }

        private static string Snapshot(V1Fixture f, string sql)
        {
            using var w = f.Hot();
            return string.Join(";", w.Pairs(sql).Select(p => p.Item1 + "=" + p.Item2));
        }
    }
}
