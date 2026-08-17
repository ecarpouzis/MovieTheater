using MovieTheater.Music;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The cue-fit test that decides whether a stored LRC can belong to the file it is attached to
    /// (<see cref="MusicLyricsFit.CuesFit"/>).
    /// </summary>
    /// <remarks>
    /// The bug it exists to stop, reported 2026-08-17: CHVRCHES' <i>Recover</i> (225.85 s) had come
    /// back from LRCLIB with cues running to 4:10.80. The lookup passed <c>duration=226</c> and
    /// LRCLIB matched — on the duration its UPLOADER recorded, which says nothing about the
    /// timestamps. Every line then landed ~25 s late, and because the scrub bar and the highlight are
    /// both driven off the same (correct) playhead, the two agreed with each other and the fault read
    /// as a player bug.
    ///
    /// <para>Fixtures are the real ones: the bad record's span, and the span every well-formed
    /// LRCLIB entry for that track carries (<c>[00:01.71]</c> → <c>[03:31.15]</c>).</para>
    /// </remarks>
    public class MusicLyricsFitTests
    {
        private const double RecoverDurationSec = 225.853333333333;

        private const string BadRecover =
            "[00:45.02] Blow by blow\n[00:56.20] You appear\n[04:10.80] When you know you don't need me?";

        private const string GoodRecover =
            "[00:01.71] Blow by blow\n[00:12.20] You appear\n[03:31.15] When you know you don't need me?";

        [Fact]
        public void An_lrc_whose_last_cue_is_past_the_end_cannot_be_this_track()
        {
            // 4:10.80 against a 3:45.9 file: the line can never fire, so the timings are another
            // version's — and a version mismatch shifts every line, not just the ones off the end.
            Assert.False(MusicLyricsFit.CuesFit(BadRecover, RecoverDurationSec));
            Assert.Equal(250.8, MusicLyricsFit.LastCueSec(BadRecover)!.Value, 2);
        }

        [Fact]
        public void The_lrc_that_actually_belongs_to_it_passes()
        {
            Assert.True(MusicLyricsFit.CuesFit(GoodRecover, RecoverDurationSec));
        }

        [Fact]
        public void A_final_cue_sitting_on_the_end_is_honest()
        {
            // Rounding and a cue written at the fade — inside the tolerance, and real LRCs do this.
            var atTheEnd = $"[00:01.00] first\n[03:46.00] last";
            Assert.True(MusicLyricsFit.CuesFit(atTheEnd, RecoverDurationSec));
            // …but a couple of seconds is the whole allowance.
            var wellPast = $"[00:01.00] first\n[03:52.00] last";
            Assert.False(MusicLyricsFit.CuesFit(wellPast, RecoverDurationSec));
        }

        [Fact]
        public void A_few_seconds_of_tail_is_NOT_licence_to_throw_the_timings_away()
        {
            // The regression that cost 72 good LRCs on the first repair run (restored from backup):
            // Bill Withers' "I Don't Want You On My Mind" — a 4:27 track whose last cue sits at 4:29.
            // It smells, and it is still plainly the right lyrics for the song.
            const double withers = 267.0;                       // 4:27
            var shortTail = "[00:12.00] first\n[04:30.00] last";               // three seconds over
            Assert.False(MusicLyricsFit.CuesFit(shortTail, withers));          // …flagged
            Assert.False(MusicLyricsFit.IsVersionMismatch(shortTail, withers)); // …but never written to
            Assert.Equal(3.0, MusicLyricsFit.OverhangSec(shortTail, withers)!.Value, 2);
        }

        [Fact]
        public void An_overhang_that_can_only_be_another_recording_is_proof()
        {
            // Recover: ~25 s past a 3:45.9 track, against a threshold of max(10, 5%) = 11.3 s.
            Assert.True(MusicLyricsFit.IsVersionMismatch(BadRecover, RecoverDurationSec));
            Assert.Equal(11.29, MusicLyricsFit.MismatchThresholdSec(RecoverDurationSec), 2);
            Assert.Equal(24.95, MusicLyricsFit.OverhangSec(BadRecover, RecoverDurationSec)!.Value, 2);
        }

        [Fact]
        public void The_floor_protects_short_tracks_from_the_proportional_term()
        {
            // Black Flag's "Wasted" is 44 s: 5% of it is 2.2 s, which would make every rounded tail
            // "proof". The 10 s floor is what stops a punk record being judged by a percentage.
            const double wasted = 44.0;
            Assert.Equal(10.0, MusicLyricsFit.MismatchThresholdSec(wasted), 2);
            Assert.False(MusicLyricsFit.IsVersionMismatch("[00:02.00] a\n[00:47.00] b", wasted));
            Assert.True(MusicLyricsFit.IsVersionMismatch("[00:02.00] a\n[01:20.00] b", wasted));
        }

        [Fact]
        public void A_long_track_gets_proportionally_more_room()
        {
            // Dylan's "Desolation Row" is 8:17. A 20 s tail on a track that long is far likelier to be
            // an outro transcription than a different take; 5% = 24.9 s is the line.
            const double desolationRow = 497.0;
            Assert.Equal(24.85, MusicLyricsFit.MismatchThresholdSec(desolationRow), 2);
            Assert.False(MusicLyricsFit.IsVersionMismatch("[00:10.00] a\n[08:37.00] b", desolationRow));
            // …and the real row: cues to 10:24 against 8:17 is 127 s over. That is another recording.
            Assert.True(MusicLyricsFit.IsVersionMismatch("[00:10.00] a\n[10:24.00] b", desolationRow));
        }

        [Fact]
        public void Nothing_is_a_mismatch_without_a_duration_to_measure_against()
        {
            Assert.False(MusicLyricsFit.IsVersionMismatch(BadRecover, null));
            Assert.False(MusicLyricsFit.IsVersionMismatch(BadRecover, 0));
            Assert.False(MusicLyricsFit.IsVersionMismatch("no cues here", RecoverDurationSec));
            Assert.Null(MusicLyricsFit.OverhangSec("no cues here", RecoverDurationSec));
        }

        [Fact]
        public void Without_a_duration_there_is_nothing_to_judge_against()
        {
            // ~24 tracks in the live catalog have no DurationSec. Refusing their lyrics would throw
            // away good data to enforce a test that cannot run.
            Assert.True(MusicLyricsFit.CuesFit(BadRecover, null));
            Assert.True(MusicLyricsFit.CuesFit(BadRecover, 0));
        }

        [Fact]
        public void Plain_text_with_no_timestamps_is_not_a_misfit()
        {
            Assert.True(MusicLyricsFit.CuesFit("Blow by blow\nYou appear", RecoverDurationSec));
            Assert.Null(MusicLyricsFit.LastCueSec("Blow by blow"));
            Assert.Null(MusicLyricsFit.LastCueSec(null));
        }

        [Fact]
        public void Metadata_tags_are_not_cues()
        {
            // [ar:…]/[ti:…]/[offset:…] carry no time; a file of nothing but tags has no last cue.
            Assert.Null(MusicLyricsFit.LastCueSec("[ar:CHVRCHES]\n[ti:Recover]\n[offset:+0]"));
        }

        [Fact]
        public void The_biggest_cue_wins_even_when_the_file_is_out_of_order()
        {
            // Nothing guarantees an LRC is sorted, and one stray late cue is the whole signal. The
            // metadata line is there because the grammar here must stay identical to the pane's
            // parser (Music/lrc.js): a cue the validator ignored but the pane renders would be a line
            // this test can never see.
            var jumbled = "[03:31.15] last\n[00:01.71] first\n[ar:CHVRCHES]\n[04:10.80] stray";
            Assert.Equal(250.8, MusicLyricsFit.LastCueSec(jumbled)!.Value, 2);
            Assert.False(MusicLyricsFit.CuesFit(jumbled, RecoverDurationSec));
        }
    }
}
