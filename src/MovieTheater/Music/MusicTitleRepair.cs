using System;
using System.IO;

namespace MovieTheater.Music
{
    /// <summary>
    /// Recovering a TRUNCATED track title from the filename that still carries it whole
    /// (2026-08-31) — the proposing half of <c>music-fix-titles</c>.
    /// </summary>
    /// <remarks>
    /// <para>Its own type, and not a private method on the command, for the reason every other music
    /// pass splits the same way: the CliFx wrapper is deliberately kept out of the test project, so a
    /// rule that lives inside a command is a rule nobody can pin. This one PROPOSES a rewrite of
    /// library metadata, which makes it exactly the kind of rule that has to be pinned.</para>
    ///
    /// <para>It only ever proposes. The command will not write a proposal that the artist's cached
    /// Last.fm catalogue does not independently confirm, which is what lets the refusals here be
    /// generous: a row this returns null for is simply left alone.</para>
    ///
    /// <para><b>Reads no files.</b> It is handed a filename as a STRING — the standing project rule
    /// is that nothing automated touches the media library, and this repair is rows only.</para>
    /// </remarks>
    public static class MusicTitleRepair
    {
        /// <summary>
        /// Below this the stored title is too short for "the filename contains it" to mean anything —
        /// a four-letter title occurs inside half the filenames in a folder by accident.
        /// </summary>
        private const int MinTitleLength = 10;

        /// <summary>The database column's own limit; a pathological name must not throw at save time.</summary>
        private const int MaxTitleLength = 400;

        /// <summary>
        /// The full title this file's NAME implies, or null when the row shows no sign of truncation
        /// (or shows one that cannot be resolved).
        /// </summary>
        /// <remarks>
        /// Truncation cuts the END, so the stored title is a PREFIX of the real one and the filename
        /// still holds the whole thing: the recovered title runs from where the stored title starts
        /// to the end of the stem. Nothing here parses the filename's grammar — track numbers and
        /// artist prefixes vary wildly across the library, and guessing at them is how a repair pass
        /// invents titles rather than recovering them.
        /// </remarks>
        /// <param name="ambiguous">Set when the stored title appears more than once in the name, so
        /// which occurrence begins the song is a guess. Reported, never picked.</param>
        public static string? Recover(string? title, string? fileName, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(fileName)) return null;
            var stored = title.Trim();
            if (stored.Length < MinTitleLength) return null;

            string? stem;
            try { stem = Path.GetFileNameWithoutExtension(fileName); }
            catch (ArgumentException) { return null; }
            if (string.IsNullOrWhiteSpace(stem)) return null;

            var at = stem.IndexOf(stored, StringComparison.Ordinal);
            if (at < 0) return null;
            if (stem.IndexOf(stored, at + 1, StringComparison.Ordinal) >= 0) { ambiguous = true; return null; }
            // Already whole: the name ends where the title ends, so nothing was cut off.
            if (stem.EndsWith(stored, StringComparison.Ordinal)) return null;

            var candidate = stem.Substring(at).Trim();
            if (candidate.Length <= stored.Length) return null;
            if (candidate.Length > MaxTitleLength) return null;
            // The two gates that separate a TRUNCATION from a filename that simply says more.
            if (!CutAtAKnownWidth(stored, candidate)) return null;
            if (StartsAnAnnotation(candidate.Substring(stored.Length))) return null;
            return candidate;
        }

        /// <summary>
        /// Whether the recovered tail is a fresh bracketed or dashed clause rather than the rest of
        /// an interrupted phrase.
        /// </summary>
        /// <remarks>
        /// <para>The second thing reading the pass by eye caught. A cut at a fixed width lands
        /// wherever it lands — mid-word (<c>"Poison In The We"</c> + <c>"ll"</c>) or mid-phrase
        /// (<c>"Sit down. Stand up. (Snakes &amp;"</c> + <c>" Ladders.)"</c>). Landing EXACTLY on the
        /// boundary before an opening bracket is a coincidence, and when it happens the far likelier
        /// explanation is that the tag was always right and the FILENAME carries an annotation: a
        /// year (<c>"Rapper's Delight"</c> + <c>" (1979)"</c>), a composer
        /// (<c>"Shape Da Future"</c> + <c>" - Hideki Naganuma"</c>), a note
        /// (<c>"Cross Eyed Mary"</c> + <c>" (Jethro Tull Cover)"</c>).</para>
        /// <para>It costs a few genuine subtitles — <c>"Let's See Action (Nothing Is Everything)"</c>
        /// is refused — and that trade is deliberate and in the same direction as every other rule
        /// here: leaving a right title unimproved is the cheap error.</para>
        /// </remarks>
        private static bool StartsAnAnnotation(string tail)
        {
            var i = 0;
            while (i < tail.Length && char.IsWhiteSpace(tail[i])) i++;
            // No separator at all means the cut fell inside a word — the clearest truncation there is.
            if (i == 0 || i >= tail.Length) return i >= tail.Length;
            return tail[i] is '(' or '[' or '{' or '-' or '–' or '—';
        }

        /// <summary>
        /// The widths a tag was cut to. <b>30 is ID3v1's title field</b> and it shows in this library
        /// as an unmistakable spike — 54 rows against 12 and 8 at the lengths either side. 16 is some
        /// ripper's own limit and accounts for the rest of the 10,000 Maniacs discography.
        /// </summary>
        /// <remarks>
        /// This set must stay FIXED and short. Allowing an arbitrary width would make the check
        /// vacuous: the stored title is a prefix of the candidate by construction, so truncating the
        /// candidate to the stored title's own length always reproduces it, and every row would pass.
        /// The strictness comes entirely from the width being decided in advance.
        /// </remarks>
        private static readonly int[] KnownCutWidths = { 16, 30 };

        /// <summary>
        /// Whether cutting <paramref name="candidate"/> at one of the <see cref="KnownCutWidths"/>
        /// reproduces <paramref name="stored"/> exactly — the evidence that the stored value is the
        /// full title with its tail chopped off, rather than a complete title the filename decorates.
        /// </summary>
        /// <remarks>
        /// <para>This is the rule that had to be added after reading the first pass by eye. Without
        /// it the repair proposed <c>"Fly Like a Butterfly"</c> → <c>"Fly Like a Butterfly - Hideki
        /// Naganuma"</c> and <c>"Der Schrei"</c> → <c>"Der Schrei [Laboratory X]"</c>: complete
        /// titles whose FILES carry the composer or the performing act after a separator. Both were
        /// confirmed by the outside catalogue too, because the same badly-named files are what people
        /// scrobbled — so the "two independent sources" were not independent, and only the mechanism
        /// of truncation itself could tell the two cases apart.</para>
        /// <para>The trailing trim is part of it: a cut landing on a space leaves one behind, which
        /// ingest trimmed, so <c>"Candy Everybody Wants"</c> cut at 16 is stored as the 15-character
        /// <c>"Candy Everybody"</c>.</para>
        /// <para>The cost is real and accepted: a genuine full title that is not a fixed-width cut —
        /// <c>"Carnival of Sorts"</c> → <c>"Carnival of Sorts (Box Cars)"</c> — is refused and left
        /// alone. Leaving a right title unimproved is the cheap error; writing a wrong one is not.</para>
        /// </remarks>
        private static bool CutAtAKnownWidth(string stored, string candidate)
        {
            foreach (var width in KnownCutWidths)
            {
                if (candidate.Length <= width) continue;
                if (candidate.Substring(0, width).TrimEnd() == stored) return true;
            }
            return false;
        }
    }
}
