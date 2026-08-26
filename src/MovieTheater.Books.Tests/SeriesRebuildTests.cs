using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// <c>books-resolve --series</c> — the series identity rebuild applied to a real (migrated, throwaway) file.
    /// The migration copied v1's own derivation; these tests change an INPUT and assert the job re-derives the
    /// answer, then assert a second run is a no-op.
    /// </summary>
    public class SeriesRebuildTests
    {
        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static UnitCounts Rebuild(V1Fixture f, int batchSize = 3)
        {
            using var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false);
            return SeriesRebuildJob.RunAll(hot, batchSize, _ => { });
        }

        [Fact]
        public void RebuildLeavesTheMigratedIdentityAloneAndRecomputesTheCounts()
        {
            using var f = Migrated();
            using (var w = f.Hot()) Assert.Equal(0, SeriesResolver.Diff(w).Total);

            var before = Identity(f);
            Rebuild(f);

            // The IDENTITY the migration copied is reproduced exactly — that is the port's fidelity proof.
            Assert.Equal(before, Identity(f));
            using (var w = f.Hot()) Assert.Equal(0, SeriesResolver.Diff(w).Total);

            using var hot = f.Hot();
            // The COUNTS and SPANS are derived, not copied: series 1 holds three files but one is an excluded
            // shadow duplicate, and its issues are dated 1977 — so the job corrects both.
            Assert.Equal(2, hot.Scalar<long>("SELECT IssueCount FROM Series WHERE Id = 1"));
            Assert.Equal(1977, hot.Scalar<long>("SELECT YearStart FROM Series WHERE Id = 1"));
            Assert.Equal(1977, hot.Scalar<long>("SELECT YearEnd FROM Series WHERE Id = 1"));
            Assert.Equal(0, hot.Scalar<long>("SELECT IsOngoing FROM Series WHERE Id = 1"));
        }

        [Fact]
        public void RunningItTwiceChangesNothingTheSecondTime()
        {
            using var f = Migrated();
            Rebuild(f);
            var once = Snapshot(f);
            Rebuild(f);
            Assert.Equal(once, Snapshot(f));
        }

        [Fact]
        public void ACvLinkOnAnAliasSpellingMergesTheSeriesAndRePointsItsItems()
        {
            using var f = Migrated();

            // INPUT edit: the "Doppelganger" spelling gains ComicVine volume 796 — the volume "Batman" already
            // owns. The two parsed keys now share a canonical identity, so the rebuild must fold one into the
            // other and carry every series-keyed row across.
            using (var w = f.Hot(dryRun: false))
            {
                w.Begin();
                w.Upsert("SeriesKeyLink", new
                {
                    ParsedKey = "Doppelganger",
                    Provider = Provider.Cv,
                    ProviderKey = 796,
                    Status = LinkStatus.Matched,
                    Score = 100,
                    AttemptCount = 1,
                });
                // and a mark on the series that is about to be merged away, to prove marks survive
                w.Upsert("GroupMark", new
                {
                    UserId = 1,
                    GroupType = GroupType.Series,
                    GroupKey = "3",
                    IsRead = true,
                    WantToRead = false,
                    IsFavorite = false,
                    Rating = 90,
                    Notes = "loved it",
                });
                w.Commit();
            }

            Rebuild(f);

            using var hot = f.Hot();
            // Exactly one series survives for cv:796, and it is the Batman row (2 items beats 1).
            Assert.Equal(1, hot.Scalar<long>("SELECT count(*) FROM Series WHERE CanonicalKey = 'cv:796'"));
            var survivor = hot.Scalar<long>("SELECT Id FROM Series WHERE CanonicalKey = 'cv:796'");
            Assert.Equal(2, survivor);
            Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM Series WHERE Id = 3"));

            // the aliases point at the survivor and the items were re-pointed with them
            Assert.Equal(survivor, hot.Scalar<long>("SELECT SeriesId FROM SeriesAlias WHERE ParsedKey = 'Doppelganger'"));
            Assert.Equal(survivor, hot.Scalar<long>("SELECT SeriesId FROM Item WHERE Id = 6"));
            Assert.Equal(3, hot.Scalar<long>("SELECT IssueCount FROM Series WHERE Id = 2"));

            // the redirect row exists, and the merged-away mark landed on the survivor with the higher rating
            Assert.Equal(survivor, hot.Scalar<long>("SELECT NewSeriesId FROM SeriesMerge WHERE OldSeriesId = 3"));
            Assert.Equal(1, hot.Scalar<long>("SELECT IsRead FROM GroupMark WHERE UserId = 1 AND GroupType = 0 AND GroupKey = '2'"));
            Assert.Equal(90, hot.Scalar<long>("SELECT Rating FROM GroupMark WHERE UserId = 1 AND GroupType = 0 AND GroupKey = '2'"));
            Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM GroupMark WHERE GroupType = 0 AND GroupKey = '3'"));

            // nothing still names the deleted id
            Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM MuSeriesLink WHERE SeriesId = 3"));
            Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM SeriesTag WHERE SeriesId = 3"));
            Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry WHERE SeriesId = 3"));
            Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM Insight WHERE SubjectKind = 1 AND SubjectId = 3"));

            // and it is stable: a second pass writes the same thing
            var once = Snapshot(f);
            Rebuild(f);
            Assert.Equal(once, Snapshot(f));
        }

        [Fact]
        public void ADisplayNameOverrideWinsTheNameAndSurvivesTheRebuild()
        {
            using var f = Migrated();
            using (var w = f.Hot(dryRun: false))
            {
                w.Begin();
                w.Update("Series", "Id", 1, new { DisplayNameOverride = "2000 AD (Prog)" });
                w.Commit();
            }

            Rebuild(f);

            using var hot = f.Hot();
            Assert.Equal("2000 AD (Prog)", hot.Scalar<string>("SELECT Name FROM Series WHERE Id = 1"));
            Assert.Equal(0, SeriesResolver.Diff(hot).Total);
        }

        [Fact]
        public void TheRebuildIsResumableAndTheCursorMatchesTheBatchOrdering()
        {
            using var f = Migrated();

            // Drive it phase by phase and stop halfway through the re-point, the way a kill would.
            long cursor = SeriesRebuildJob.IdentityCursor;
            var counts = new UnitCounts();
            using (var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false))
            {
                for (var i = 0; i < 2; i++)
                {
                    hot.Begin();
                    SeriesRebuildJob.RunStep(hot, cursor, 2, _ => { }, counts, out cursor);
                    hot.Commit();
                }
                Assert.True(cursor >= SeriesRebuildJob.RepointBase, "expected to be mid re-point");
            }

            // Resuming from the SAME cursor in a new process finishes the job.
            using (var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false))
            {
                var guard = 0;
                while (guard++ < 200)
                {
                    hot.Begin();
                    var done = SeriesRebuildJob.RunStep(hot, cursor, 2, _ => { }, counts, out cursor);
                    hot.Commit();
                    if (done) break;
                }
            }

            using var w = f.Hot();
            Assert.Equal(0, SeriesResolver.Diff(w).Total);
            Assert.Equal(2, w.Scalar<long>("SELECT IssueCount FROM Series WHERE Id = 1"));
            Assert.Equal(Snapshot(f), SnapshotAfterFullRun(f));
        }

        /// <summary>A full drain from scratch must land on the same rows the resumed run did.</summary>
        private static string SnapshotAfterFullRun(V1Fixture f)
        {
            Rebuild(f);
            return Snapshot(f);
        }

        [Fact]
        public void TheRebuildStampsItsRegistryRows()
        {
            using var f = Migrated();
            Rebuild(f);
            using var hot = f.Hot();
            foreach (var name in new[] { "Series", "SeriesAlias", "Item.SeriesId" })
            {
                Assert.Equal("books-resolve --series", hot.Scalar<string>("SELECT RebuildJob FROM DerivedTable WHERE Name = $n", ("$n", name)));
                Assert.True(hot.Scalar<long>("SELECT length(coalesce(InputFingerprint,'')) FROM DerivedTable WHERE Name = $n", ("$n", name)) > 0);
                Assert.True(hot.Scalar<long>("SELECT RowCount FROM DerivedTable WHERE Name = $n", ("$n", name)) > 0);
            }
        }

        /// <summary>Everything the job derives, as one comparable string — the "second run changed nothing" proof.</summary>
        private static string Snapshot(V1Fixture f)
        {
            using var w = f.Hot();
            var parts = new List<string>();
            foreach (var (id, row) in w.Pairs(
                "SELECT Id, CanonicalKey || '|' || coalesce(Name,'') || '|' || IssueCount || '|' || coalesce(YearStart,-1) || '|' || coalesce(YearEnd,-1) || '|' || IsOngoing FROM Series ORDER BY Id"))
                parts.Add($"S{id}={row}");
            parts.Add(Identity(f));
            return string.Join(";", parts);
        }

        /// <summary>Just the IDENTITY half: survivors' canonical keys and names, the alias map, the item ids, the redirects.</summary>
        private static string Identity(V1Fixture f)
        {
            using var w = f.Hot();
            var parts = new List<string>();
            foreach (var (id, row) in w.Pairs("SELECT Id, CanonicalKey || '|' || coalesce(Name,'') FROM Series ORDER BY Id"))
                parts.Add($"S{id}={row}");
            foreach (var (sid, key) in w.Pairs("SELECT SeriesId, ParsedKey FROM SeriesAlias ORDER BY ParsedKey"))
                parts.Add($"A{key}={sid}");
            foreach (var (id, sid) in w.Pairs("SELECT Id, coalesce(CAST(SeriesId AS TEXT),'') FROM Item ORDER BY Id"))
                parts.Add($"I{id}={sid}");
            foreach (var (oldId, newId) in w.Pairs("SELECT OldSeriesId, CAST(NewSeriesId AS TEXT) FROM SeriesMerge ORDER BY OldSeriesId"))
                parts.Add($"M{oldId}={newId}");
            return string.Join(";", parts);
        }
    }
}
