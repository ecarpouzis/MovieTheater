using MovieTheater.Books.Db;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>The run-status rule (<see cref="RunStatusJob.Decide"/>), pinned on the cases the first live run taught.</summary>
    public class RunStatusTests
    {
        private const int Year = 2026;
        private static RunStatusJob.Evidence E(bool? cur = null, string? fmt = null, int? gcdN = null, int? ended = null, string? notes = null,
            int? planned = null, int? cvN = null, string? cvDesc = null, int? yearEnd = null, int? heldMax = null) =>
            new(cur, fmt, gcdN, ended, notes, planned, cvN, cvDesc, yearEnd, null, heldMax);

        [Fact]
        public void GCD_current_is_ongoing()
        {
            var d = RunStatusJob.Decide(E(cur: true, fmt: "ongoing series", gcdN: 72, yearEnd: 2024), Year);
            Assert.Equal(RunStatus.Ongoing, d.Status);
            Assert.Equal("gcd:current", d.Basis);
        }

        [Fact]
        public void A_limited_run_that_reached_its_printed_length_is_completed()
        {
            var d = RunStatusJob.Decide(E(cur: false, fmt: "limited series", gcdN: 12, ended: 1987, planned: 12, yearEnd: 1987), Year);
            Assert.Equal(RunStatus.Completed, d.Status);
            Assert.Equal(12, d.Planned);
        }

        [Fact]
        public void A_finished_run_short_of_its_printed_length_is_cancelled()
        {
            var d = RunStatusJob.Decide(E(cur: false, fmt: "limited series", gcdN: 4, ended: 2013, planned: 6, yearEnd: 2013), Year);
            Assert.Equal(RunStatus.Cancelled, d.Status);
        }

        [Fact]
        public void Our_own_files_outrank_a_stale_provider_count()
        {
            // the June GCD dump knew Zorro (2026) #1; we hold #1-3 of 3 — complete, not cancelled at #1
            var d = RunStatusJob.Decide(E(cur: false, gcdN: 1, planned: 3, yearEnd: 2026, heldMax: 3), Year);
            Assert.Equal(RunStatus.Completed, d.Status);
            Assert.Equal(3, d.Published);
        }

        [Fact]
        public void A_year_misread_as_an_issue_number_does_not_inflate_the_run()
        {
            var d = RunStatusJob.Decide(E(cur: false, fmt: "was ongoing series", gcdN: 553, ended: 2015, yearEnd: 2015, heldMax: 1948), Year);
            Assert.Equal(553, d.Published);
            Assert.Equal(RunStatus.Ended, d.Status);
        }

        [Fact]
        public void Young_run_short_of_plan_is_ongoing_not_cancelled()
        {
            var d = RunStatusJob.Decide(E(planned: 5, cvN: 2, yearEnd: 2026), Year);
            Assert.Equal(RunStatus.Ongoing, d.Status);
        }

        [Fact]
        public void A_source_saying_cancelled_on_a_finished_run()
        {
            var d = RunStatusJob.Decide(E(cvN: 8, cvDesc: "The series was cancelled after eight issues.", yearEnd: 2012), Year);
            Assert.Equal(RunStatus.Cancelled, d.Status);
            Assert.Equal("cv:cancelled", d.Basis);
        }

        [Fact]
        public void Was_ongoing_is_ended_and_no_evidence_is_unknown()
        {
            Assert.Equal(RunStatus.Ended, RunStatusJob.Decide(E(cur: false, fmt: "was ongoing series", gcdN: 300, ended: 2004, yearEnd: 2004), Year).Status);
            Assert.Equal(RunStatus.Unknown, RunStatusJob.Decide(E(), Year).Status);
            Assert.Equal(RunStatus.Ongoing, RunStatusJob.Decide(E(yearEnd: 2026), Year).Status);   // recency
        }

        [Theory]
        [InlineData("Zorro 01 (of 03) (2026) (digital).cbr", 3)]
        [InlineData("Murder Inc. - Jagger Rose 02 (of 06) (2023).cbr", 6)]
        [InlineData("Saga 054 (2018).cbz", null)]
        public void The_planned_length_comes_from_the_filename(string name, int? expected) =>
            Assert.Equal(expected, RunStatusJob.PlannedFromFileName(name));
    }
}
