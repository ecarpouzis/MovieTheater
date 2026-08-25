using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Migration.Units;
using MovieTheater.Books.Resolve;
using MovieTheater.Books.Verify;

namespace MovieTheater.Books.Tests
{
    /// <summary>The copy-transform end to end on the synthetic v1 fixture: counts, ids, edges, the owner filter, resume, and the resolver's outputs.</summary>
    public class MigrationTests
    {
        [Fact]
        public void UnitListCoversTheContractExactly()
        {
            var mapping = MappingContract.Load();
            MigrationEngine.Validate(mapping, MigrationUnits.All());
            var driven = MigrationUnits.All().Where(u => u.SourceTable != null && u.Suffix == "").Select(u => u.SourceTable!).ToHashSet();
            var expected = mapping.V1.Values.Where(t => t.Targets.Count > 0 && t.Name != "ComicFts").Select(t => t.Name).ToHashSet();
            Assert.Equal(expected.OrderBy(x => x), driven.OrderBy(x => x));
        }

        [Fact]
        public void DryRunWritesNothing()
        {
            using var f = new V1Fixture();
            var log = new List<string>();
            var summary = f.Engine(f.Options(dryRun: true), log).Run();
            Assert.False(summary.Stopped, summary.StopReason);
            Assert.Equal(0, f.HotCount("Item"));
            Assert.Equal(0, f.HotCount("MigrationProgress"));
            Assert.Contains(log, l => l.Contains("unit: items/Comics"));
        }

        [Fact]
        public void FullMigrationProducesTheExpectedRows()
        {
            using var f = new V1Fixture();
            var log = new List<string>();
            var summary = f.Engine(f.Options(), log).Run();
            Assert.False(summary.Stopped, summary.StopReason);
            Assert.Equal(MigrationUnits.All().Count, summary.UnitsFinished);

            // ids preserved, the split
            Assert.Equal(9, f.HotCount("Item"));
            Assert.Equal(9, f.HotCount("ItemState"));
            Assert.Equal(2, f.HotCount("BookDetail"));
            Assert.Equal(7, f.HotCount("ComicEmbedded"));
            Assert.Equal(7, f.HotCount("ItemSignature"));
            Assert.Equal(1, f.HotCount("Item", "IsExcluded = 1"));
            Assert.Equal(1, f.HotCount("Item", "Id = 101 AND CalibreBookId = 844"));
            // folders: tree + two-pass parents + top folder + icon
            Assert.Equal(7, f.HotCount("Folder"));
            Assert.Equal(5, f.HotCount("Folder", "ParentId IS NOT NULL"));
            using (var w = f.Hot())
            {
                Assert.Equal(2, w.Scalar<long>("SELECT TopFolderId FROM Folder WHERE Id = 4"));
                Assert.Equal(1, w.Scalar<long>("SELECT HasIcon FROM Folder WHERE Id = 2"));
                Assert.Equal(3, w.Scalar<long>("SELECT Depth FROM Folder WHERE Id = 4"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM Folder WHERE Id = 11 AND RootId = 2"));
                // credits: ComicInfo writers split, Calibre authors split on '&', LOCG credits for the matched item
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM ItemCredit WHERE ItemId = 1 AND Source = 0 AND Role = 'Writer'"));
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM ItemCredit WHERE ItemId = 102 AND Source = 2 AND Role = 'Author'"));
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM ItemCredit WHERE ItemId = 1 AND Source = 3"));
                // tags: CVDB-resolved name → Cv, plain genre → ComicInfo, Calibre tags, GCD fold on the matched item
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM ItemTag WHERE ItemId = 2 AND Value = 'Harumi Kiyama' AND Source = 1"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM ItemTag WHERE ItemId = 2 AND Value = 'Science Fiction' AND Source = 0"));
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM ItemTag WHERE ItemId = 101 AND Source = 2"));
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM ItemTag WHERE ItemId = 1 AND Source = 4 AND Category = 'tag'"));
                // enum transforms
                Assert.Equal((long)ParseSource.Manual, w.Scalar<long>("SELECT SeriesSource FROM ComicDetail WHERE ItemId = 3"));
                Assert.Equal((long)ComicFormat.LimitedSeries, w.Scalar<long>("SELECT Format FROM ComicDetail WHERE ItemId = 6"));
                Assert.Equal("Limed Series", w.Scalar<string>("SELECT FormatRaw FROM ComicDetail WHERE ItemId = 6"));
                Assert.Equal(1, w.Scalar<long>("SELECT SeriesId FROM Item WHERE Id = 2"));
                // links: CV ints, LOCG span-corroborated → High + method, cleared reason kept, GCD, MU ambiguous → Multiple, Series.MuSeriesId
                Assert.Equal((long)LinkStatus.Multiple, w.Scalar<long>("SELECT Status FROM ItemProviderLink WHERE ItemId = 4 AND Provider = 0"));
                Assert.Equal(60, w.Scalar<long>("SELECT StoredTopScore FROM ItemProviderLink WHERE ItemId = 4 AND Provider = 0"));
                Assert.Equal((long)LinkQuality.High, w.Scalar<long>("SELECT Quality FROM ItemProviderLink WHERE ItemId = 1 AND Provider = 2"));
                Assert.Equal("series-issue;span-corroborated", w.Scalar<string>("SELECT Method FROM ItemProviderLink WHERE ItemId = 1 AND Provider = 2"));
                Assert.Equal("cleared-pageaudit", w.Scalar<string>("SELECT Error FROM ItemProviderLink WHERE ItemId = 4 AND Provider = 2"));
                Assert.Equal((long)LinkStatus.Manual, w.Scalar<long>("SELECT Status FROM SeriesKeyLink WHERE ParsedKey = 'Batman' AND Provider = 0"));
                Assert.Equal(95, w.Scalar<long>("SELECT StoredTopScore FROM SeriesKeyLink WHERE ParsedKey = '2000AD' AND Provider = 0"));
                Assert.Equal((long)LinkStatus.Multiple, w.Scalar<long>("SELECT Status FROM MuSeriesLink WHERE SeriesId = 2"));
                Assert.Equal(77, w.Scalar<long>("SELECT MuSeriesId FROM Series WHERE Id = 3"));
                Assert.Equal(5, w.Scalar<long>("SELECT count(*) FROM SeriesTag WHERE SeriesId = 3 AND Source = 6")); // MU fold
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM SeriesTag WHERE SeriesId = 4 AND Source = 5")); // External fold
                // reading order group keys
                Assert.Equal(1, w.Scalar<long>("SELECT SeriesId FROM ReadingOrderEntry WHERE ItemId = 1"));
                Assert.Equal(2, w.Scalar<long>("SELECT SeriesId FROM ReadingOrderEntry WHERE ItemId = 4"));
                // spans: 4 sources → one table
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM CollectedEditionSpan WHERE ItemId = 7"));
                // insights: 4 carried (orphan exported), ids preserved, book ids offset, tags re-keyed, currency picked
                Assert.Equal(4, w.Scalar<long>("SELECT count(*) FROM Insight WHERE SubjectKind = 1"));
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM Insight WHERE SubjectKind = 0"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM Insight WHERE Id = " + InsightIds.ForBook(101) + " AND Maturity = 2"));
                Assert.Equal(3, w.Scalar<long>("SELECT count(*) FROM InsightTag WHERE InsightId = " + InsightIds.ForBook(101) + " OR InsightId = " + InsightIds.ForBook(102)));
                Assert.Equal(0, w.Scalar<long>("SELECT count(*) FROM InsightTag WHERE InsightId = 5"));
                Assert.Equal(2, w.Scalar<long>("SELECT Id FROM Insight WHERE SubjectKind = 1 AND SubjectId = 1 AND IsCurrent = 1")); // opus (rank 3) beats sonnet High
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM Insight WHERE SubjectKind = 1 AND SubjectId = 2 AND IsCurrent = 1"));
                // ratings
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM Rating WHERE TargetKind = 1 AND TargetId = 2 AND Source = 5 AND IsOverride = 1"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM Rating WHERE TargetKind = 0 AND TargetId = 4 AND Source = 0 AND Value = 30"));
                // user activity: owner only; merged rows; name-keyed series mark resolved; unresolved name dropped with a count
                Assert.Equal(5, w.Scalar<long>("SELECT count(*) FROM UserItemState")); // bookmarks 1,2,101 + list 4 + comic mark 5 (999999 unmapped)
                Assert.Equal(0, w.Scalar<long>("SELECT count(*) FROM UserItemState WHERE UserId <> 1"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM UserItemState WHERE ItemId = 2 AND WantToRead = 1 AND Status = 1 AND LastPage = 12"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM UserItemState WHERE ItemId = 4 AND WantToRead = 1 AND Favorite = 1"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM UserItemState WHERE ItemId = 1 AND LastPage = -1 AND Status = 2"));
                Assert.Equal(2, w.Scalar<long>("SELECT count(*) FROM GroupMark WHERE UserId = 1 AND GroupType = 0"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM GroupMark WHERE GroupKey = '2' AND IsRead = 1"));
                // system state → registry
                Assert.StartsWith("v1:series_", w.Scalar<string>("SELECT InputFingerprint FROM DerivedTable WHERE Name = 'Series'"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM SystemState WHERE Key = 'cvcache_seed_fingerprint'"));
                // resolved scalars + fts
                Assert.Equal("Doppelganger", w.Scalar<string>("SELECT ResolvedTitle FROM Item WHERE Id = 6"));
                Assert.Equal("Batman Vol 2 #405", w.Scalar<string>("SELECT ResolvedTitle FROM Item WHERE Id = 5"));
                Assert.Equal("2000 AD #1", w.Scalar<string>("SELECT ResolvedTitle FROM Item WHERE Id = 1"));
                Assert.Equal("Rebellion", w.Scalar<string>("SELECT ResolvedPublisher FROM Item WHERE Id = 1"));
                Assert.Equal((long)SynopsisSource.Cv, w.Scalar<long>("SELECT ResolvedSynopsisSource FROM Item WHERE Id = 1"));
                Assert.Equal((long)SynopsisSource.Cv, w.Scalar<long>("SELECT ResolvedSynopsisSource FROM Item WHERE Id = 2")); // its own summary is boilerplate; the series' CV description wins
                Assert.Equal(1977, w.Scalar<long>("SELECT ResolvedYear FROM Item WHERE Id = 1"));
                Assert.Equal(2, w.Scalar<long>("SELECT ResolvedMonth FROM Item WHERE Id = 1"));
                Assert.Equal(84, w.Scalar<long>("SELECT ResolvedRating FROM Item WHERE Id = 1"));
                Assert.Equal(95, w.Scalar<long>("SELECT ResolvedRating FROM Series WHERE Id = 2"));
                Assert.Equal(95, w.Scalar<long>("SELECT ResolvedRating FROM Item WHERE Id = 4")); // series override flows to issues without own rating
                Assert.Equal("Aldous Huxley", w.Scalar<string>("SELECT ResolvedCreatorsCsv FROM Item WHERE Id = 101"));
                Assert.Equal(0.667, w.Scalar<double>("SELECT CoverAspect FROM Item WHERE Id = 1"), 3);
                Assert.Equal(9, w.Scalar<long>("SELECT count(*) FROM ItemFts"));
                Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM ItemFts WHERE ItemFts MATCH 'Dredd' AND rowid = 1"));
            }
            // legs
            Assert.Equal(2, f.LegsCount("LocgComicRaw"));
            Assert.Equal(1, f.HotCount("LocgComic"));
            Assert.Equal(2, f.LegsCount("LocgCreatorRaw"));
            Assert.Equal(2, f.LegsCount("CvVolumeRaw"));
            Assert.Equal(5, f.LegsCount("LinkCandidates")); // 2 series links + 2 CV item matches + 1 MU
            Assert.Equal(1, f.LegsCount("ProviderResponseCache"));
            Assert.Equal(1, f.LegsCount("MuSeriesRaw"));
            // orphan export
            var orphans = File.ReadAllText(Path.Combine(f.WorkDir, "orphan-insights.json"));
            Assert.Contains("No Such Series Anywhere", orphans);
            // the verifier is green on the fixture
            using (var src = new V1Source(f.V1Path))
            using (var hot = f.Hot())
            using (var legs = f.Legs())
            {
                var checks = new V1Verifier(src, hot, legs, f.Options()).Run();
                var failed = checks.Where(c => !c.Passed).ToList();
                Assert.True(failed.Count == 0, string.Join("\n", failed.Select(c => c.Name + ": " + c.Detail)));
            }
        }

        [Fact]
        public void KilledMidRunResumesToTheSameResult()
        {
            using var f = new V1Fixture();
            // tiny batches, three batches at a time, until done — every stop is a "kill"
            var runs = 0;
            while (true)
            {
                var s = f.Engine(f.Options(batchSize: 3, maxBatches: 3)).Run();
                runs++;
                Assert.False(s.Stopped && s.StopReason != "max batches reached", s.StopReason);
                if (!s.Stopped) break;
                Assert.True(runs < 500, "did not converge");
            }
            Assert.True(runs > 5);
            Assert.Equal(9, f.HotCount("Item"));
            Assert.Equal(5, f.HotCount("UserItemState"));
            Assert.Equal(2, f.HotCount("ItemCredit", "ItemId = 1 AND Source = 3"));
            Assert.Equal(MigrationUnits.All().Count, f.HotCount("MigrationProgress", "FinishedAt IS NOT NULL"));
            // a second full run on top is a no-op (idempotent upserts)
            var again = f.Engine(f.Options()).Run();
            Assert.Equal(0, again.Batches);
            // and a reset + rerun of one stage converges to the same counts
            var e = f.Engine(f.Options(stage: "user-activity"));
            e.ResetProgress("user-activity");
            e.Run();
            Assert.Equal(5, f.HotCount("UserItemState"));
            Assert.Equal(1, f.HotCount("UserItemState", "ItemId = 2 AND WantToRead = 1 AND LastPage = 12"));
        }

        [Fact]
        public void SeriesResolutionRecomputeMatchesTheCopiedDerivation()
        {
            using var f = new V1Fixture();
            f.Engine(f.Options()).Run();
            using var hot = f.Hot();
            var diff = SeriesResolver.Diff(hot);
            Assert.True(diff.Total == 0, string.Join("\n", diff.Samples));
        }

        [Fact]
        public void ReplayRunsEveryQueryWithoutFlagsOnTheFixture()
        {
            using var f = new V1Fixture();
            f.Engine(f.Options()).Run();
            var rows = new HotSetReplay(f.HotPath, largeTableRows: 1).Run();
            Assert.DoesNotContain(rows, r => r.Flags.Contains("ERROR"));
            Assert.Equal(new HotSetReplay(f.HotPath).Queries().Count(), rows.Count);
        }

        [Fact]
        public void EfReadsWhatTheWriterWrote()
        {
            using var f = new V1Fixture();
            f.Engine(f.Options()).Run();
            using var db = f.HotDb();
            var item = db.Items.Include(i => i.Series).Include(i => i.Comic).Include(i => i.Embedded).Include(i => i.State).Single(i => i.Id == 1);
            Assert.Equal("2000 AD", item.Series!.Name);
            Assert.Equal(ItemKind.Comic, item.Kind);
            Assert.Equal(ContainerFormat.Cbz, item.ContainerFormat);
            Assert.Equal(new DateTime(2025, 2, 3, 21, 39, 47), item.FileModifiedAt);
            Assert.Equal(ComicFormat.SingleIssue, item.Comic!.Format);
            Assert.Equal("Pat Mills, John Wagner", item.Embedded!.Writers);
            Assert.Equal(1500, item.State!.CoverHeight);
            var book = db.Items.Include(i => i.Book).Single(i => i.Id == 101);
            Assert.Equal("9780060850524", book.Book!.Isbn);
            Assert.Equal(844, book.CalibreBookId);
            Assert.Equal(5, db.UserItemStates.Count(u => u.UserId == 1));
            Assert.Equal(1, db.Insights.Count(n => n.SubjectKind == SubjectKind.Series && n.SubjectId == 1 && n.IsCurrent));
        }
    }
}
