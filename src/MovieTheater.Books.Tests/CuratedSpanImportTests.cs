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
        public void EvenAtEqualConfidenceGoldIsNotDisplaced()
        {
            var gold = new CuratedSpanImport.Existing(31, 59, 0.9, "issue: p001 back cover");
            var line = Span(1, 44, 44, 0.9);
            Assert.Equal(CuratedSpanImport.Verdict.Kept, CuratedSpanImport.Decide(line, gold, out var flag, out _));
            Assert.Equal("provider-disagrees", flag);
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
    }
}
