namespace MovieTheater.RottenTomatoes
{
    /// <summary>
    /// Outcome of resolving a movie on rottentomatoes.com and reading its two scores.
    /// A false <see cref="Found"/> means RT search produced no confident match, so the
    /// row should be flagged for review rather than trusted. Either score may still be
    /// null when found (RT shows "- -" for unscored titles).
    /// </summary>
    public class RtScoreResult
    {
        public string SearchTitle { get; set; }

        public bool Found { get; set; }
        public string FailureReason { get; set; }

        /// <summary>
        /// True when the failure was transient (RT bot-challenge / block / network) rather than
        /// a genuine "not on RT" miss. Transient failures are NOT persisted, so a resume retries
        /// the row, and they trip the run's throttling circuit-breaker.
        /// </summary>
        public bool Transient { get; set; }

        /// <summary>Canonical RT movie page we resolved to (e.g. https://www.rottentomatoes.com/m/matrix).</summary>
        public string ResolvedUrl { get; set; }

        /// <summary>Title of the RT row we matched, for audit/mismatch context.</summary>
        public string MatchedTitle { get; set; }

        /// <summary>Release year of the RT row we matched.</summary>
        public int? MatchedYear { get; set; }

        /// <summary>Tomatometer (critics) percentage, 0–100.</summary>
        public int? Tomatometer { get; set; }

        /// <summary>Popcornmeter (audience) percentage, 0–100.</summary>
        public int? Popcornmeter { get; set; }
    }
}
