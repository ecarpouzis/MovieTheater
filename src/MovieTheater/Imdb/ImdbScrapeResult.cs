using System;
using System.Collections.Generic;

namespace MovieTheater.Imdb
{
    /// <summary>One credited person extracted from an IMDB title page.</summary>
    public class ScrapedPerson
    {
        public string ImdbNameId { get; set; }
        public string DisplayName { get; set; }
        public string Character { get; set; }
    }

    /// <summary>One contributed plot summary from the /plotsummary page.</summary>
    public class ScrapedSummary
    {
        public string Author { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Everything we pull from a single IMDB title page. A null <see cref="Found"/>
    /// (false) means the id did not resolve and the row should be flagged for review.
    /// </summary>
    public class ImdbScrapeResult
    {
        public string ImdbId { get; set; }
        public bool Found { get; set; }
        public string FailureReason { get; set; }

        public string Title { get; set; }
        public int? Year { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string MpaaRating { get; set; }
        public decimal? ImdbRating { get; set; }
        public string Plot { get; set; }

        /// <summary>The long single IMDB synopsis (spoilers), from /plotsummary.</summary>
        public string Synopsis { get; set; }

        /// <summary>All contributed plot summaries, from /plotsummary.</summary>
        public List<ScrapedSummary> Summaries { get; } = new List<ScrapedSummary>();

        public List<string> Genres { get; } = new List<string>();
        public List<ScrapedPerson> Actors { get; } = new List<ScrapedPerson>();
        public List<ScrapedPerson> Directors { get; } = new List<ScrapedPerson>();
        public List<ScrapedPerson> Writers { get; } = new List<ScrapedPerson>();
    }
}
