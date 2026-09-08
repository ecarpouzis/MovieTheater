using System.Globalization;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The containment-driven duplicate producer and the flags sheet it reads.
    ///
    /// <para>The interesting assertions are the REFUSALS. A group here says "you own these issues twice", and
    /// the whole reason the containment pass exists is that a provider leg saying so is often wrong. So the job
    /// must decline a judged span it does not trust, and must decline outright on a shelf a person has flagged —
    /// those are the series where several runs each number from #1 and "issue 5" names three different comics.</para>
    ///
    /// <para>Every test drains the job once BEFORE building its shelf: the migrated fixture already carries a
    /// span-corroborated container of its own, and absorbing it first makes the numbers that follow this shelf's
    /// and nothing else's.</para>
    /// </summary>
    public class ContainedDuplicateTests
    {
        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static TargetWriter Writer(V1Fixture f) => new(f.HotPath, MappingContract.Load(), dryRun: false);

        private const int SeriesId = 1, Container = 9001, Issue1 = 9002, Issue2 = 9003;

        private static (int Groups, int Members, int Skipped) Run(TargetWriter hot) =>
            ContainedDuplicateJob.RunAll(hot, 50, ContainedDuplicateJob.MinConfidence, _ => { });

        /// <summary>A shelf: one collected edition over two single issues, with the span that decided it.</summary>
        private static void Shelf(TargetWriter hot, double confidence, string? providerRef = "model:b000")
        {
            var (rootId, folderId) = hot.Pairs("SELECT RootId, CAST(FolderId AS TEXT) FROM Item LIMIT 1")
                .Select(p => (p.Item1, long.Parse(p.Item2!, CultureInfo.InvariantCulture))).First();

            foreach (var (id, name) in new[] { (Container, "Vol. 01.cbz"), (Issue1, "001.cbz"), (Issue2, "002.cbz") })
                hot.Exec(@"INSERT INTO Item (Id, RootId, FolderId, TopFolderId, Kind, Path, FileName, Extension,
                                             ContainerFormat, FileSize, PageCount, Title, NormalizedTitle, SeriesId,
                                             IsExcluded, KeepInDirectory)
                           VALUES ($id, $root, $folder, $folder, 0, $path, $name, '.cbz', 0, 1000, 120, $name, $name, $series, 0, 0)",
                         ("$id", id), ("$root", rootId), ("$folder", folderId),
                         ("$path", $"/t/{id}-{name}"), ("$name", name), ("$series", SeriesId));

            hot.Exec(@"INSERT INTO CollectionNode (ItemId, SeriesId, Level, TrackRole, SpanStart, SpanEnd, ContainsCount, ParentItemId, SpanSource, SpanLabel)
                       VALUES ($id, $s, 1, $role, 1, 2, 2, NULL, $src, '#1-2')",
                     ("$id", Container), ("$s", SeriesId), ("$role", (int)TrackRole.Container), ("$src", (int)SpanSource.Curated));
            foreach (var issue in new[] { Issue1, Issue2 })
                hot.Exec(@"INSERT INTO CollectionNode (ItemId, SeriesId, Level, TrackRole, SpanStart, SpanEnd, ContainsCount, ParentItemId, SpanSource, SpanLabel)
                           VALUES ($id, $s, 0, $role, 0, 0, 1, $parent, 0, NULL)",
                         ("$id", issue), ("$s", SeriesId), ("$role", (int)TrackRole.Primary), ("$parent", Container));

            hot.Exec(@"INSERT INTO CollectedEditionSpan (ItemId, Source, SeriesId, IssueStart, IssueEnd, EditionTitle, ProviderRef, Contiguous, Confidence)
                       VALUES ($id, $src, $s, 1, 2, 'Vol. 01', $ref, 1, $conf)",
                     ("$id", Container), ("$src", (int)EditionSource.Curated), ("$s", SeriesId),
                     ("$ref", providerRef), ("$conf", confidence));
        }

        private static void Flag(TargetWriter hot, int itemId, int? seriesId, string flag, string state = "Pending") =>
            hot.Exec(@"INSERT INTO ContainmentFlag (ItemId, SeriesId, Flag, Detail, Source, ReviewState)
                       VALUES ($i, $s, $f, 'because', 'test', $st)",
                     ("$i", itemId), ("$s", seriesId), ("$f", flag), ("$st", state));

        /// <summary>How many containment groups name THIS shelf's collected edition as the collection.</summary>
        private static long ShelfGroups(V1Fixture f)
        {
            using var w = f.Hot();
            return w.Scalar<long>(
                $@"SELECT count(*) FROM DuplicateGroup g JOIN DuplicateMember m ON m.DuplicateGroupId = g.Id
                   WHERE g.Relationship = {DuplicateRelationship.ContainedIn}
                     AND m.ItemId = {Container} AND m.Role = '{ContainedDuplicateJob.RoleCollection}'");
        }

        [Fact]
        public void ATrustedSpanGroupsTheIssuesItHolds()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                Shelf(hot, 0.9);
                var r = Run(hot);
                Assert.Equal(1, r.Groups);
                Assert.Equal(3, r.Members);          // the collection plus the two issues it holds
            }
            Assert.Equal(1, ShelfGroups(f));

            using var w = f.Hot();
            var group = w.Scalar<long>($@"SELECT DuplicateGroupId FROM DuplicateMember
                                          WHERE ItemId = {Container} AND Role = '{ContainedDuplicateJob.RoleCollection}'");
            Assert.Equal("High", w.Scalar<string>($"SELECT Confidence FROM DuplicateGroup WHERE Id = {group}"));
            Assert.Contains("covers #1-2", w.Scalar<string>($"SELECT Evidence FROM DuplicateGroup WHERE Id = {group}"));
            // No keeper is suggested: owning both editions is legitimate, so there is nothing to hide.
            Assert.Equal(0, w.Scalar<long>(
                $"SELECT count(*) FROM DuplicateGroup WHERE Id = {group} AND SuggestedKeeperItemId IS NOT NULL"));
            Assert.Equal(2, w.Scalar<long>(
                $"SELECT count(*) FROM DuplicateMember WHERE DuplicateGroupId = {group} AND Role = '{ContainedDuplicateJob.RoleContained}'"));
        }

        [Fact]
        public void ALowConfidenceJudgementIsEvidenceNotAGroup()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                Shelf(hot, 0.65);
                var r = Run(hot);
                Assert.Equal(0, r.Groups);
                Assert.Equal(1, r.Skipped);
            }
            Assert.Equal(0, ShelfGroups(f));
        }

        [Fact]
        public void GoldIsTrustedWhateverItsConfidence()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                // v1 quoting an edition's own indicia, or a person typing it in the review screen.
                Shelf(hot, 0.1, providerRef: "admin:eric");
                Assert.Equal(1, Run(hot).Groups);
            }
            Assert.Equal(1, ShelfGroups(f));
        }

        [Theory]
        [InlineData("overlap-in-series")]
        [InlineData("conflated-series")]
        public void AFlaggedShelfIsNeverGrouped(string flag)
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                Shelf(hot, 0.95);
                Flag(hot, Container, SeriesId, flag);
                Assert.Equal(0, Run(hot).Groups);
            }
            Assert.Equal(0, ShelfGroups(f));
        }

        [Fact]
        public void AnItemWithAnyUndecidedFlagIsNeverGrouped()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                Shelf(hot, 0.95);
                Flag(hot, Container, SeriesId, "arithmetic-odd");
                Assert.Equal(0, Run(hot).Groups);
            }
            Assert.Equal(0, ShelfGroups(f));
        }

        [Fact]
        public void DismissingTheFlagLetsTheShelfBack()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                Shelf(hot, 0.95);
                Flag(hot, Container, SeriesId, "arithmetic-odd", state: ContainmentFlagImport.Dismissed);
                Assert.Equal(1, Run(hot).Groups);
            }
            Assert.Equal(1, ShelfGroups(f));
        }

        [Fact]
        public void RunningTwiceDoesNotDuplicateTheGroup()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                Shelf(hot, 0.9);
                Assert.Equal(1, Run(hot).Groups);
                Assert.Equal(0, Run(hot).Groups);
            }
            Assert.Equal(1, ShelfGroups(f));
        }

        [Fact]
        public void ResetClearsOnlyTheContainmentGroups()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                Shelf(hot, 0.9);
                Run(hot);
                Assert.Equal(1, ShelfGroups(f));

                hot.Exec(@"INSERT INTO DuplicateGroup (Relationship, Confidence, Evidence, ReviewState)
                           VALUES (0, 'High', 'identical file', 'Pending')");
                hot.Begin();
                var wiped = ContainedDuplicateJob.Reset(hot);
                hot.Commit();
                Assert.True(wiped >= 1, $"expected this shelf's group among the wiped, got {wiped}");
            }
            Assert.Equal(0, ShelfGroups(f));

            using var w = f.Hot();
            Assert.Equal(0, w.Scalar<long>(
                $"SELECT count(*) FROM DuplicateGroup WHERE Relationship = {DuplicateRelationship.ContainedIn}"));
            // The signature pass's own group is untouched: this job only ever owns relationship 3.
            Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM DuplicateGroup WHERE Relationship = 0"));
        }
        [Fact]
        public void AFlagWhoseOwnSeriesIdIsStaleStillCondemnsTheShelf()
        {
            // A series fold moves the item and leaves the flag's denormalized SeriesId behind. The gate has to
            // read the ITEM's series, or a flagged shelf quietly becomes groupable again.
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                Run(hot);
                Shelf(hot, 0.95);
                Flag(hot, Container, seriesId: null, flag: "overlap-in-series");
                Assert.Equal(0, Run(hot).Groups);
            }
            Assert.Equal(0, ShelfGroups(f));
        }
    }
}
