using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MovieTheater.Music
{
    /// <summary>
    /// Folding a song title down to the form two catalogues can be compared on (2026-08-31) — the
    /// join key behind <c>music-track-popularity</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why any of this is needed.</b> The popularity lookup asks Last.fm for an artist's
    /// tracks and matches them against ours by NAME, and the two catalogues never spell a song the
    /// same way. Measured against the real library before this existed: our
    /// <c>"Something (Remastered 2009)"</c>, <c>"Hurt - 2011 Remaster"</c> and
    /// <c>"Don't Look Back In Anger"</c> are Last.fm's <c>"Something"</c>, <c>"Hurt"</c> and
    /// <c>"Don't Look Back in Anger"</c>. Case, punctuation and the edition suffix are all noise;
    /// what is left is the song.</para>
    ///
    /// <para><b>The dangerous half is what it must NOT strip.</b> A leading parenthetical is very
    /// often the title itself — <c>"(Don't Fear) The Reaper"</c>, <c>"(I Can't Get No)
    /// Satisfaction"</c>, <c>"(What's the Story) Morning Glory?"</c> — and a rule that dropped every
    /// bracket would map all three onto some other song. So a bracketed group is removed only when
    /// it NAMES AN EDITION (<see cref="EditionWords"/>): remaster, mix, live, mono, demo and their
    /// kin. <c>"(Reprise)"</c> is deliberately not on that list — a reprise is a different track with
    /// a different length, and folding it into the parent would hand two rows one number.</para>
    ///
    /// <para>Pure and total: no allocations the caller has to dispose, no I/O, every rule pinned by
    /// <c>MusicTrackTitlesTests</c>. It exists as its own type because a matching rule that lives
    /// inside the command that uses it is a rule nobody can test against real examples.</para>
    /// </remarks>
    public static class MusicTrackTitles
    {
        /// <summary>
        /// The words that mark a bracketed group as an EDITION rather than part of the song's name.
        /// Kept deliberately short: every addition is a chance to swallow a real title, and the cost
        /// of missing one is a single unmatched track, while the cost of over-matching is a wrong
        /// number on the right song.
        /// </summary>
        private const string EditionWords =
            "remaster|remastered|remix|mix|version|edit|mono|stereo|live|demo|bonus|instrumental|" +
            "acoustic|reissue|deluxe|explicit|clean|radio|single|album|original|take|alternate|" +
            "outtake|rehearsal|session|feat|featuring|with";

        /// <summary>A bracketed or parenthesised group whose contents name an edition.</summary>
        private static readonly Regex BracketedEdition = new(
            @"\s*[\(\[][^)\]]*\b(?:" + EditionWords + @")\b[^)\]]*[\)\]]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// A trailing <c>" - 2011 Remaster"</c> suffix: the same edition marker written with a dash
        /// instead of brackets, which is the form streaming catalogues favour.
        /// </summary>
        /// <remarks>
        /// Anchored to the END and requiring the dash to be surrounded by spaces, so it can never eat
        /// a hyphenated title (<c>"Sun-Dried"</c>) or a song whose name simply contains a dash
        /// (<c>"Ashes to Ashes - Live at Wembley"</c> loses only the venue clause, which is what it
        /// should lose).
        /// </remarks>
        private static readonly Regex TrailingEdition = new(
            @"\s+[-–—]\s+[^-–—]*\b(?:" + EditionWords + @")\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NonAlphanumeric = new(
            @"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The comparison form of a song title: edition markers dropped, accents folded, punctuation
        /// reduced to single spaces, lower-cased. Empty when there is nothing left to compare, which
        /// the caller must treat as "cannot be matched" rather than as a key.
        /// </summary>
        /// <remarks>
        /// Order matters. Brackets go first (they can contain the dash the next rule looks for), the
        /// dash suffix second, and only then is punctuation flattened — run the other way round, the
        /// brackets and dashes the rules key on would already be gone.
        /// </remarks>
        public static string Normalize(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";

            var t = title.Trim();
            // Repeat: a title can carry two of them ("Hurt (Live) [Remastered]"), and one pass over a
            // non-overlapping match set leaves the second in place. Bounded by the shrink — each pass
            // must remove characters or the loop stops.
            for (var i = 0; i < 3; i++)
            {
                var next = BracketedEdition.Replace(t, "");
                if (next.Length == t.Length) break;
                t = next;
            }
            t = TrailingEdition.Replace(t, "");

            t = FoldAccents(t);
            t = t.ToLowerInvariant();
            // Ampersand before punctuation is flattened, or "Simon & Garfunkel" and "Simon and
            // Garfunkel" fold to "simon garfunkel" and "simon and garfunkel" — two different keys for
            // one name. Spelling it out makes both land on the longer form.
            t = t.Replace("&", " and ");
            // An apostrophe CLOSES rather than separating: "don't" must become "dont", not "don t",
            // or it stops matching a catalogue that spells it "dont".
            t = t.Replace("'", "").Replace("’", "");
            t = NonAlphanumeric.Replace(t, " ");
            return t.Trim();
        }

        /// <summary>
        /// The shortest normalised title worth trying to complete. Below this a prefix is not
        /// evidence of anything — "tension" opens a dozen different songs — and the uniqueness guard
        /// alone would not save a library that happens to hold only one of them.
        /// </summary>
        private const int MinPrefixLength = 12;

        /// <summary>
        /// Looks one of our titles up in an external catalogue keyed by <see cref="Normalize"/>:
        /// exactly first, and failing that as the beginning of exactly ONE entry.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a prefix match exists at all.</b> 566 tracks in this library carry a
        /// TRUNCATED title tag: the file is
        /// <c>07_10,000 Maniacs - What's The Matter Here.mp3</c> and its ID3 title frame says
        /// <c>What's The Matte</c>. The tag is what ingest stores, so those rows could never match a
        /// catalogue exactly, and would have been scoreless for a reason that has nothing to do with
        /// how well known the songs are. The completion also covers the wider case it generalises to:
        /// any title of ours that is the opening of a longer one in the outside catalogue.
        /// </para>
        /// <para><b>Why "exactly one" was the wrong guard.</b> The obvious rule — complete a prefix
        /// only when ONE entry can finish it — was tried first and it almost never fired. A real
        /// Last.fm artist page is mostly a long tail of scrobbler typos: 10,000 Maniacs' catalogue
        /// carries <c>Planned Obsolescence</c> (14,803 listeners) AND <c>Planned Obsolescene</c> (63),
        /// which are the same song spelled two ways, and requiring uniqueness threw the real answer
        /// away because a misspelling of it also existed.
        /// </para>
        /// <para><b>So the guard is DOMINANCE instead.</b> Take the best-known completion, and accept
        /// it only when it is at least <see cref="DominanceRatio"/>× bigger than the next DIFFERENT
        /// one. A typo is always orders of magnitude smaller than the song it misspells (235× here,
        /// 822× for <c>Candy Everybody Wants</c>), while two genuinely different songs sharing a
        /// twelve-character opening have comparable audiences and are refused — which is the case the
        /// uniqueness rule was really trying to catch, caught properly.
        /// </para>
        /// <para>Truncation cuts mid-WORD, so no word-boundary rule applies here: the whole point is
        /// that <c>obsolesc</c> is half a word.</para>
        /// </remarks>
        public static bool TryMatch(IReadOnlyDictionary<string, long> catalogue, string key, out long value)
        {
            value = 0;
            if (catalogue == null || string.IsNullOrEmpty(key)) return false;
            if (catalogue.TryGetValue(key, out value)) return true;
            if (key.Length < MinPrefixLength) return false;

            long best = -1, runnerUp = -1;
            foreach (var entry in catalogue)
            {
                if (!entry.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Value > best) { runnerUp = best; best = entry.Value; }
                else if (entry.Value > runnerUp) { runnerUp = entry.Value; }
            }

            if (best < 0) { value = 0; return false; }
            // A lone completion has nothing to be ambiguous with. Otherwise the winner has to be
            // decisive, or this is a guess dressed as a match.
            if (runnerUp >= 0 && best < runnerUp * DominanceRatio) { value = 0; return false; }
            value = best;
            return true;
        }

        /// <summary>
        /// How far ahead the best completion must be before it counts as the answer rather than a
        /// coin flip. Ten is well above the noise (typo variants trail the real song by two to three
        /// orders of magnitude) and well below anything two real songs would show.
        /// </summary>
        private const int DominanceRatio = 10;

        /// <summary>
        /// Accented letters reduced to their base form, so <c>Björk</c> and <c>Bjork</c> — the same
        /// artist typed by two taggers — produce one key.
        /// </summary>
        private static string FoldAccents(string s)
        {
            var decomposed = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                    != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
