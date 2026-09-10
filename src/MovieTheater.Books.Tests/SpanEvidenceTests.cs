using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// Trust is granted per row by evidence, never by provenance. These pin the one test that grants it:
    /// the note quotes the book, and the quote names exactly the issues the range claims.
    /// </summary>
    public class SpanEvidenceTests
    {
        [Fact]
        public void AnIndiciaQuoteNamingExactlyTheRangeIsSelfProving()
        {
            const string note = "issue: indicia p2: 'Originally published in single magazine form as FABLES 1-5'";
            Assert.True(SpanEvidence.SelfProving(note, 1, 5));
            Assert.False(SpanEvidence.SelfProving(note, 1, 6));
            Assert.False(SpanEvidence.SelfProving(note, 2, 5));
        }

        [Fact]
        public void ANonContiguousQuoteDoesNotProveTheHullOverIt()
        {
            // The shape that deletes files: the book skips #20-25, the span claims them anyway.
            const string note = "issue: indicia p003: 'Originally published in single magazine form in CHECKMATE 13-19, 26-31'";
            Assert.False(SpanEvidence.SelfProving(note, 13, 31));
            var q = SpanEvidence.QuotedIssues(note)!;
            Assert.Contains(19d, q);
            Assert.Contains(26d, q);
            Assert.DoesNotContain(20d, q);
        }

        [Theory]
        [InlineData("issue: indicia p004: 'This volume collects issues #0\u20137 of the Dark Horse Conan'", 0, 7)]
        [InlineData("back cover p146: 'Collects issues #700-#704 of the ongoing ARCHIE series'", 700, 704)]
        [InlineData("indicia: 'collects issues #8 through #13'", 8, 13)]
        public void TheOddDashesAndHashesStillParse(string note, double a, double b)
        {
            Assert.True(SpanEvidence.SelfProving(note, a, b));
        }

        [Fact]
        public void AYearIsNotAnIssueNumber()
        {
            const string note = "issue: indicia p2: 'Originally published in single magazine form as SAGA 1-6 (2012)'";
            Assert.True(SpanEvidence.SelfProving(note, 1, 6));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("volume: no quotation here at all, just prose about the book")]
        [InlineData("issue: cover states 'the complete saga' with no numbers")]
        public void ANoteWithoutAQuotedIssueListProvesNothing(string? note)
        {
            Assert.Null(SpanEvidence.QuotedIssues(note));
            Assert.False(SpanEvidence.SelfProving(note, 1, 5));
            Assert.Null(SpanEvidence.ExcludedIssues(note, 1, 5));
        }

        [Theory]
        // The five live hulls of docs/books/containment/PLAN.md §6.1, verbatim from their stored notes.
        [InlineData("issue: indicia p003: 'Originally published in single magazine form in CHECKMATE 13-19, 26-31'",
                    13, 31, new double[] { 20, 21, 22, 23, 24, 25 })]
        [InlineData("issue: indicia p003: 'Originally published in single magazine form in SUPERMAN 33-36, 39-40'",
                    33, 40, new double[] { 37, 38 })]
        [InlineData("issue: back cover p678: 'Collecting: X-Men (2019) #1-12 & #16-21, Giant-Size X-Men one-shots'",
                    1, 21, new double[] { 13, 14, 15 })]
        [InlineData("issue: indicia p3: 'Originally published in single magazine form as FABLES: THE LAST CASTLE and FABLES 19-21, 23-27'",
                    19, 27, new double[] { 22 })]
        [InlineData("issue: p325 back-matter: 'Collecting Peter Parker, the Spectacular Spider-Man 27-28, and Daredevil #158-161 and #163-172 written by Frank Miller'",
                    158, 172, new double[] { 162 })]
        public void AQuotedGapInsideTheRangeIsADenial(string note, double a, double b, double[] gap)
        {
            var excluded = SpanEvidence.ExcludedIssues(note, a, b);
            Assert.NotNull(excluded);
            Assert.Equal(gap.OrderBy(x => x), excluded!.OrderBy(x => x));
        }

        [Fact]
        public void AGapWiderThanWhatIsNamedIsStillAGap()
        {
            // "Collecting Amazing Spider-Man #88-92 and #121-122" — 28 of the 35 numbers are denied.
            // Believing it under-claims; disbelieving it claims 28 issues the book does not hold.
            const string note = "issue: p156 back cover: 'Collecting Amazing Spider-Man #88-92 and #121-122'";
            var excluded = SpanEvidence.ExcludedIssues(note, 88, 122)!;
            Assert.Equal(28, excluded.Count);
            Assert.Contains(100d, excluded);
            Assert.DoesNotContain(92d, excluded);
            Assert.DoesNotContain(121d, excluded);
        }

        [Theory]
        // Every one of these quotes a DIFFERENT numbering than its range, which is why the anchor rule
        // exists: reading their gaps as denials would empty editions whose ranges are correct.
        // Baltimore Vol. 08 — the arc's own #1-5 under a continuous 36-40.
        [InlineData("issue: indicia p005: 'This volume collects Baltimore: The Red Kingdom #1-#5'; sequential count 36-40", 36, 40)]
        // The Essential Groo Vol. 08 — the source series' numbers under this shelf's volume ordinal.
        [InlineData("volume: intro page p001: 'This volume is a compilation of Groo v2 #76 to #88'", 8, 8)]
        // A sibling volume's range, quoted as the reason this one starts where it does.
        [InlineData("issue: gcd title match 15-19; cv's '#20-25' is vol 5's range", 15, 19)]
        public void AQuoteThatNamesNeitherEndpointIsNotAboutThisRange(string note, double a, double b)
        {
            Assert.Null(SpanEvidence.ExcludedIssues(note, a, b));
        }

        [Fact]
        public void AQuoteNamingExactlyTheRangeDeniesNothing()
        {
            const string note = "issue: indicia p2: 'Originally published in single magazine form as FABLES 1-5'";
            Assert.True(SpanEvidence.SelfProving(note, 1, 5));
            Assert.Null(SpanEvidence.ExcludedIssues(note, 1, 5));
        }
    }
}
