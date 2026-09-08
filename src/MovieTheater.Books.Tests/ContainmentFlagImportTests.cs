using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// Reading the containment pass's review sheet. The details are prose written by the pass — they carry
    /// commas, quotes and apostrophes — so the parsing has to survive a sentence, not just a number.
    /// </summary>
    public class ContainmentFlagImportTests
    {
        [Fact]
        public void AQuotedFieldKeepsItsCommas()
        {
            var f = ContainmentFlagImport.SplitCsv("12,34,Saga,label-ambiguous,\"one, two, three\",file.cbz");
            Assert.Equal(6, f.Count);
            Assert.Equal("one, two, three", f[4]);
            Assert.Equal("file.cbz", f[5]);
        }

        [Fact]
        public void ADoubledQuoteIsOneQuote()
        {
            var f = ContainmentFlagImport.SplitCsv("1,2,S,flag,\"the \"\"Deluxe\"\" edition\",x.cbz");
            Assert.Equal("the \"Deluxe\" edition", f[4]);
        }

        [Fact]
        public void AGoodRowParses()
        {
            var r = ContainmentFlagImport.Parse("32824,2,'68,conflated-series,\"identical gcd #1-4 on every volume\",68 v01.cbr", 7);
            Assert.Null(r.Error);
            Assert.Equal(32824, r.ItemId);
            Assert.Equal(2, r.SeriesId);
            Assert.Equal("conflated-series", r.Flag);
            Assert.Equal("identical gcd #1-4 on every volume", r.Detail);
            Assert.Equal(7, r.LineNo);
        }

        [Fact]
        public void AMissingSeriesIdIsAllowed()
        {
            var r = ContainmentFlagImport.Parse("5,,,label-ambiguous,detail,f.cbz", 1);
            Assert.Null(r.Error);
            Assert.Null(r.SeriesId);
        }

        [Theory]
        [InlineData("nope,2,S,flag,d,f", "itemId")]
        [InlineData("1,2,S", "at least 4 columns")]
        [InlineData("1,2,S,,d,f", "flag is empty")]
        public void ABadRowSaysWhy(string line, string expected)
        {
            var r = ContainmentFlagImport.Parse(line, 1);
            Assert.NotNull(r.Error);
            Assert.Contains(expected, r.Error);
        }

        [Fact]
        public void TheHeaderIsRecognisedWithOrWithoutABom()
        {
            Assert.True(ContainmentFlagImport.IsHeader("itemId,seriesId,series,flag,detail,file"));
            Assert.True(ContainmentFlagImport.IsHeader("﻿itemId,seriesId,series,flag,detail,file"));
            Assert.False(ContainmentFlagImport.IsHeader("32824,2,'68,conflated-series,d,f"));
        }

        [Fact]
        public void TheFlagVocabularyIsTheOneTheReviewScreenExplains()
        {
            // The tab's FLAG_HELP map is keyed on these; a new flag with no explanation is a silent gap.
            Assert.Contains("overlap-in-series", ContainmentFlagImport.KnownFlags);
            Assert.Contains("duplicate-edition", ContainmentFlagImport.KnownFlags);
            Assert.Contains("span-retracted", ContainmentFlagImport.KnownFlags);
        }
    }
}
