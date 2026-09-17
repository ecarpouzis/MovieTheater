using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The gate the model pass's judged ranges pass through (<see cref="CuratedSpanImport"/>). Eric is about to
    /// de-duplicate comic FILES on this containment, so the interesting cases here are all refusals: a book the
    /// model declined to place, a range it could not justify, and a v1 indicia-quoted row it is not allowed to
    /// overwrite.
    /// </summary>
    public class CuratedSpanImportTests
    {
        private static CuratedSpanImport.Line Span(int id, double a, double b, double conf) =>
            CuratedSpanImport.Parse(
                $$"""{"itemId": {{id}}, "start": {{a}}, "end": {{b}}, "editionTitle": "Book One", "confidence": {{conf}}, "rationale": "filename range"}""", 1);

        [Fact]
        public void AJudgedRangeWithNoPriorRowIsWritten()
        {
            // Saga Book 1: the file name carries "(Saga 01-018)" and 505 pages over 18 issues is 28 a piece.
            var line = Span(34061, 1, 18, 0.95);
            Assert.Equal(CuratedSpanImport.Verdict.Write, CuratedSpanImport.Decide(line, null, out var flag, out _));
            Assert.Null(flag);
            Assert.Equal(1, line.Start);
            Assert.Equal(18, line.End);
            Assert.Equal("Book One", line.EditionTitle);
        }

        [Fact]
        public void UnknownIsCountedAndNeverWritten()
        {
            // '68 Vol 2 "Scars" is its own mini with its own numbering — the parent series' #1-4 would be a lie.
            var line = CuratedSpanImport.Parse("""{"itemId": 32824, "unknown": true, "why": "Scars is a separate mini"}""", 1);
            Assert.Equal(CuratedSpanImport.Verdict.Unknown, CuratedSpanImport.Decide(line, null, out var flag, out _));
            Assert.Null(flag);
            Assert.True(line.Unknown);
        }

        [Fact]
        public void AGoldIndiciaRowSurvivesADisagreeingModelAnswerAndIsFlagged()
        {
            // Saga Vol. 10 carries a v1 Curated #55-60 at 0.98. A model answer never displaces it.
            var gold = new CuratedSpanImport.Existing(55, 60, 0.98, null);
            var line = Span(34083, 49, 54, 0.85);
            Assert.Equal(CuratedSpanImport.Verdict.Kept, CuratedSpanImport.Decide(line, gold, out var flag, out var detail));
            Assert.Equal("provider-disagrees", flag);
            Assert.Contains("55-60", detail);
            Assert.Contains("49-54", detail);
        }

        [Fact]
        public void EvenAtEqualConfidenceAProvenRowIsNotDisplaced()
        {
            // What holds the line is the QUOTATION. This row points at the book naming exactly #31-59, so a
            // judgement at the same confidence does not get to overwrite it.
            var proven = new CuratedSpanImport.Existing(31, 59, 0.9,
                null, "issue: indicia p001: 'Originally published in single magazine form as EXAMPLE 31-59'");
            var line = Span(1, 44, 44, 0.9);
            Assert.Equal(CuratedSpanImport.Verdict.Kept, CuratedSpanImport.Decide(line, proven, out var flag, out _));
            Assert.Equal("provider-disagrees", flag);
        }

        [Fact]
        public void ARowThatQuotesNoNumbersIsNotProtectedByItsProvenance()
        {
            // PLAN.md §6.6, and the four Hellboy omnibus rows that named the wrong volumes. "p001 back cover"
            // is a citation, not a quotation: it names no issues, so it proves nothing, and a shelf judgement
            // at equal-or-better confidence replaces it. Gold confirms at 47.8%; the label buys nothing.
            var unproven = new CuratedSpanImport.Existing(31, 59, 0.9, null, "issue: p001 back cover");
            var line = Span(1, 44, 44, 0.9);
            Assert.Equal(CuratedSpanImport.Verdict.Write, CuratedSpanImport.Decide(line, unproven, out var flag, out _));
            Assert.Null(flag);
        }

        [Fact]
        public void AnAgreeingGoldRowIsRewrittenWithoutAFlag()
        {
            var gold = new CuratedSpanImport.Existing(1, 18, 0.98, null);
            var line = Span(34061, 1, 18, 0.95);
            // Same range: nothing to ask Eric about, and the row is left as it is by the upsert's own values.
            Assert.Equal(CuratedSpanImport.Verdict.Kept, CuratedSpanImport.Decide(line, gold, out var flag, out _));
            Assert.Null(flag);
        }

        [Fact]
        public void ThePassRewritesItsOwnRowsSoRerunningIsIdempotent()
        {
            var mine = new CuratedSpanImport.Existing(1, 6, 0.7, "model:pass1");
            var line = Span(34061, 1, 18, 0.95);
            Assert.Equal(CuratedSpanImport.Verdict.Write, CuratedSpanImport.Decide(line, mine, out var flag, out _));
            Assert.Null(flag);

            var same = new CuratedSpanImport.Existing(1, 18, 0.95, "model:pass1");
            Assert.Equal(CuratedSpanImport.Verdict.Write, CuratedSpanImport.Decide(line, same, out _, out _));
        }

        [Theory]
        [InlineData("""{"itemId": 1, "start": 18, "end": 1, "confidence": 0.9}""")]
        [InlineData("""{"itemId": 1, "start": 1, "confidence": 0.9}""")]
        [InlineData("""{"itemId": 1, "start": 1, "end": 6, "confidence": 0.4}""")]
        [InlineData("""{"itemId": 1, "start": 1, "end": 6, "confidence": 1.5}""")]
        [InlineData("""{"start": 1, "end": 6, "confidence": 0.9}""")]
        [InlineData("not json at all")]
        public void AnUnusableLineIsRefusedAndFlagged(string json)
        {
            var line = CuratedSpanImport.Parse(json, 7);
            Assert.Equal(CuratedSpanImport.Verdict.Invalid, CuratedSpanImport.Decide(line, null, out var flag, out var detail));
            Assert.Equal("invalid-span", flag);
            Assert.False(string.IsNullOrEmpty(detail));
        }

        [Fact]
        public void ASingleIssueRangeIsAllowedWhenTheModelMeansIt()
        {
            // #0 exists, and a one-issue "collection" is a real (if odd) shape — the DEGENERATE-span guard lives
            // in SpanSelection, which sees the page count. This gate only refuses what it can prove wrong.
            var line = Span(1, 0, 0, 0.9);
            Assert.Equal(CuratedSpanImport.Verdict.Write, CuratedSpanImport.Decide(line, null, out var flag, out _));
            Assert.Null(flag);
        }

        [Fact]
        public void ALineCarriesItsOwnBatchLabelWhenItHasOne()
        {
            var line = CuratedSpanImport.Parse(
                """{"itemId": 5, "start": 1, "end": 6, "confidence": 0.7, "batch": "pass1-b042"}""", 1);
            Assert.Equal("pass1-b042", line.Batch);
            Assert.Equal(CuratedSpanImport.Verdict.Write, CuratedSpanImport.Decide(line, null, out _, out _));
        }
        // ── retraction: an audit that corrects the pass must be able to take a span back ──

        [Fact]
        public void AWithdrawnAnswerDeletesTheRowThePassItselfWrote()
        {
            var line = CuratedSpanImport.Parse("""{"itemId": 5, "unknown": true, "why": "116pp cannot hold ten issues"}""", 1);
            var mine = new CuratedSpanImport.Existing(9, 18, 0.85, "model:pass1");
            Assert.Equal(CuratedSpanImport.Verdict.Retract, CuratedSpanImport.Decide(line, mine, out var flag, out var detail));
            Assert.Equal("span-retracted", flag);
            Assert.Contains("116pp cannot hold ten issues", detail);
        }

        [Fact]
        public void AWithdrawnAnswerNeverTouchesAProvenRowOrAPerson()
        {
            // Declining is not evidence against an edition's own words, or against a person who typed one.
            var line = CuratedSpanImport.Parse("""{"itemId": 5, "unknown": true, "why": "not evidenced"}""", 1);
            foreach (var kept in new[]
            {
                new CuratedSpanImport.Existing(1, 18, 0.98, null,
                    "issue: indicia p2: 'Originally published in single magazine form as EXAMPLE 1-18'"),
                new CuratedSpanImport.Existing(1, 18, 1.0, "admin:eric"),
            })
            {
                Assert.Equal(CuratedSpanImport.Verdict.Unknown, CuratedSpanImport.Decide(line, kept, out var flag, out _));
                Assert.Null(flag);
            }
        }

        [Fact]
        public void AWithdrawnAnswerDoesRetractAnUnprovenRow()
        {
            // A shelf that has been read and found to hold no answer must be able to withdraw one that was
            // never evidenced — otherwise a wrong range outlives the judgement that disproved it. Hellboy
            // Omnibus Vol. 04 claimed #11-12 while collecting two books from another Series entirely.
            var line = CuratedSpanImport.Parse("""{"itemId": 5, "unknown": true, "why": "collects another Series"}""", 1);
            var unproven = new CuratedSpanImport.Existing(11, 12, 0.9, null,
                "volume: indicia p2: 'This book collects the graphic novels Hellboy in Hell Volumes 1 & 2'");
            Assert.Equal(CuratedSpanImport.Verdict.Retract, CuratedSpanImport.Decide(line, unproven, out var flag, out var detail));
            Assert.Equal("span-retracted", flag);
            Assert.Contains("11-12", detail);
        }

        [Fact]
        public void AWithdrawnAnswerWithNothingStandingIsJustUnknown()
        {
            var line = CuratedSpanImport.Parse("""{"itemId": 5, "unknown": true, "why": "manga volume"}""", 1);
            Assert.Equal(CuratedSpanImport.Verdict.Unknown, CuratedSpanImport.Decide(line, null, out var flag, out _));
            Assert.Null(flag);
        }
        [Fact]
        public void AHandTypedSpanOutranksTheModelAndIsFlaggedWhenTheyDisagree()
        {
            // What the containment review screen writes: Curated, confidence 1.0, ProviderRef "admin:<user>".
            // A person who can see the shelf beats an inference about it, so the model answer is KEPT OUT.
            var typed = new CuratedSpanImport.Existing(1, 6, 1.0, "admin:eric");
            var line = Span(5, 1, 12, 0.85);
            Assert.Equal(CuratedSpanImport.Verdict.Kept, CuratedSpanImport.Decide(line, typed, out var flag, out var detail));
            Assert.Equal("provider-disagrees", flag);
            Assert.Contains("kept curated 1-6", detail);
            Assert.Contains("model said 1-12", detail);

            // …and an AGREEING line at the same confidence leaves the row alone rather than rewriting its
            // ProviderRef to `model:` — what a person typed stays attributed to the person.
            Assert.Equal(CuratedSpanImport.Verdict.Kept,
                CuratedSpanImport.Decide(Span(5, 1, 6, 1.0), typed, out var flag2, out _));
            Assert.Null(flag2);
        }

        [Fact]
        public void AnIdentityPassRangeOutranksTheModelAndIsFlaggedWhenTheyDisagree()
        {
            // Eric's ruling: an `identity:` row is a reader's answer off the whole provider packet, and every
            // landing re-runs pass2 into this verb — so a model line at equal or greater confidence must not
            // overwrite it, and `unknown` must not retract it. Same class as admin / self-proving.
            var read = new CuratedSpanImport.Existing(1, 5, 0.95, "identity:B-061");
            var line = Span(5, 1, 4, 0.95);
            Assert.Equal(CuratedSpanImport.Verdict.Kept, CuratedSpanImport.Decide(line, read, out var flag, out var detail));
            Assert.Equal("provider-disagrees", flag);
            Assert.Contains("kept curated 1-5", detail);
            Assert.Contains("model said 1-4", detail);

            var withdrawn = CuratedSpanImport.Parse("""{"itemId": 5, "unknown": true, "why": "not evidenced"}""", 1);
            Assert.Equal(CuratedSpanImport.Verdict.Unknown, CuratedSpanImport.Decide(withdrawn, read, out var flag2, out _));
            Assert.Null(flag2);

            // An AGREEING model line does not rewrite it either: the range is already there, and the write
            // would swap `identity:B-061` for `model:…` and demote the row out of the proven class.
            Assert.Equal(CuratedSpanImport.Verdict.Kept,
                CuratedSpanImport.Decide(Span(5, 1, 5, 0.95), read, out var flag3, out _));
            Assert.Null(flag3);
        }

        // ── the run refs: which RUN the range counts in, across a re-import ──────────────────────────

        [Fact]
        public void AReImportKeepsTheRunRefsAReaderPutOnTheSpan()
        {
            // The wave pipeline's hazard, exactly as `books-curated-spans-import` meets it: the retraction
            // path DELETEs the Curated row, the foreign key cascades, and a reader's "#1-5 OF Wake the Devil"
            // goes with it. The verb reads the refs before it writes and puts them back; this does what the
            // verb does, against a real file, so the cascade in the assertion is the real cascade.
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Upsert("CollectedEditionSpan", new
                {
                    ItemId = 7, Source = EditionSource.Curated, SeriesId = 1, IssueStart = 1.0, IssueEnd = 5.0,
                    ProviderRef = "identity:B-061", Contiguous = true, Confidence = 0.95, Note = "read",
                    CreatedAt = DateTime.UtcNow,
                });
                hot.Upsert("CollectedEditionSpanRun", new
                {
                    ItemId = 7, Source = EditionSource.Curated, Provider = Provider.Cv, ProviderKey = "13579",
                    IssueStart = 1.0, IssueEnd = 5.0, Confidence = 0.95, CreatedAt = DateTime.UtcNow,
                });
                hot.Upsert("CollectedEditionSpanRun", new
                {
                    ItemId = 7, Source = EditionSource.Curated, Provider = Provider.Gcd, ProviderKey = "2468",
                    IssueStart = 103.0, IssueEnd = 107.0, Confidence = 0.95, CreatedAt = DateTime.UtcNow,
                });
                // the second mini this book collects, on the SAME leg — impossible before the PK widened
                hot.Upsert("CollectedEditionSpanRun", new
                {
                    ItemId = 7, Source = EditionSource.Curated, Provider = Provider.Cv, ProviderKey = "24680",
                    IssueStart = 1.0, IssueEnd = 4.0, Confidence = 0.9, CreatedAt = DateTime.UtcNow,
                });
                hot.Commit();
            }

            using (var hot = Writer(f))
            {
                var runs = CuratedSpanRuns.Read(hot, "7");
                Assert.Equal(3, runs[7].Count);
                var second = runs[7].Single(r => r.Key == "24680");
                Assert.Equal((Provider.Cv, 1.0, 4.0, 0.9), (second.Provider, second.Start, second.End, second.Confidence));

                hot.Begin();
                // the verb's retraction path: delete, then write the row again
                hot.Exec("DELETE FROM CollectedEditionSpan WHERE ItemId = 7 AND Source = $s",
                    ("$s", (int)EditionSource.Curated));
                Assert.Equal(0, hot.Scalar<long>("SELECT count(*) FROM CollectedEditionSpanRun WHERE ItemId = 7"));
                hot.Upsert("CollectedEditionSpan", new
                {
                    ItemId = 7, Source = EditionSource.Curated, SeriesId = 1, IssueStart = 1.0, IssueEnd = 6.0,
                    ProviderRef = "model:pass2", Contiguous = true, Confidence = 0.9, Note = "re-imported",
                    CreatedAt = DateTime.UtcNow,
                });
                Assert.Equal(3, CuratedSpanRuns.Reattach(hot, 7, runs, apply: true));
                hot.Commit();
            }

            using var w = f.Hot();
            Assert.Equal(3, w.Scalar<long>("SELECT count(*) FROM CollectedEditionSpanRun WHERE ItemId = 7"));
            Assert.Equal("13579,24680", w.Scalar<string>(
                $"SELECT group_concat(ProviderKey) FROM (SELECT ProviderKey FROM CollectedEditionSpanRun "
                + $"WHERE ItemId = 7 AND Provider = {(int)Provider.Cv} ORDER BY ProviderKey)"));
            Assert.Equal("2468", w.Scalar<string>(
                $"SELECT ProviderKey FROM CollectedEditionSpanRun WHERE ItemId = 7 AND Provider = {(int)Provider.Gcd}"));
            // and the RANGES came back with them: the GCD leg numbers these same issues #103-107
            Assert.Equal(103.0, w.Scalar<double>(
                $"SELECT IssueStart FROM CollectedEditionSpanRun WHERE ItemId = 7 AND Provider = {(int)Provider.Gcd}"));
            Assert.Equal(4.0, w.Scalar<double>(
                "SELECT IssueEnd FROM CollectedEditionSpanRun WHERE ItemId = 7 AND ProviderKey = '24680'"));
            // and the span itself was re-written, so this is a re-import and not a no-op
            Assert.Equal(6.0, w.Scalar<double>("SELECT IssueEnd FROM CollectedEditionSpan WHERE ItemId = 7 AND Source = 3"));
        }

        [Fact]
        public void TheWinningSpansRunRefsTravelWithIt()
        {
            // `ReadingOrderJob.LoadSpans` is where every consumer gets its answer, so the refs have to arrive
            // with the SELECTED span and be attributed to the source that won.
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Upsert("CollectedEditionSpan", new
                {
                    ItemId = 7, Source = EditionSource.Curated, SeriesId = 1, IssueStart = 1.0, IssueEnd = 5.0,
                    ProviderRef = "identity:B-061", Contiguous = true, Confidence = 0.95, Note = "read",
                    CreatedAt = DateTime.UtcNow,
                });
                hot.Upsert("CollectedEditionSpanRun", new
                {
                    ItemId = 7, Source = EditionSource.Curated, Provider = Provider.Cv, ProviderKey = "13579",
                    IssueStart = 1.0, IssueEnd = 5.0, Confidence = 0.95, CreatedAt = DateTime.UtcNow,
                });
                hot.Commit();
            }
            using var w = f.Hot();
            var span = ReadingOrderJob.LoadSpans(w)[7];
            Assert.Equal(EditionSource.Curated, span.Source);
            var run = Assert.Single(span.Runs!);
            Assert.Equal(Provider.Cv, run.Provider);
            Assert.Equal("13579", run.Key);
            Assert.Equal((1.0, 5.0), (run.Start, run.End));
        }

        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static TargetWriter Writer(V1Fixture f) => new(f.HotPath, MappingContract.Load(), dryRun: false);
    }
}
