using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MovieTheater.Music
{
    /// <summary>
    /// Turning what a file SAYS its genre is into something a facet can list (R9 S10).
    /// </summary>
    /// <remarks>
    /// <para>Genre tags are the least disciplined field in the whole library. The same record can
    /// arrive as <c>Rock</c>, <c>rock</c>, <c>ROCK </c>, <c>Rock/Pop</c>, <c>Rock;Alternative</c>,
    /// <c>(17)</c>, <c>(17)Rock</c> or <c>Rock (17)</c> depending on which ripper wrote it in which
    /// decade. A rail that lists those as eight different genres is worse than no rail, so every
    /// value goes through <see cref="Split"/> before it reaches the database.</para>
    /// <para><b>The ID3v1 numeric form is a real encoding, not junk.</b> ID3v2's TCON was defined to
    /// allow a parenthesised ID3v1 index — <c>(17)</c> IS "Rock" — and plenty of files in this
    /// library still carry it, sometimes with the spelled-out name after it. Dropping the digits
    /// would silently throw away the genre of every one of them, so the table below is the standard
    /// Winamp list (0–191) and the parser resolves the index.</para>
    /// <para><b>What is deliberately NOT done:</b> no synonym folding ("Hip-Hop" → "Rap"), no
    /// hierarchy, no vocabulary. The library's own tags are the vocabulary; imposing a taxonomy on
    /// them would be a judgement about somebody else's record collection, and the long-tail facet is
    /// designed to carry the mess.</para>
    /// </remarks>
    public static class MusicGenres
    {
        /// <summary>Longest a stored genre may be (the column is nvarchar(100)).</summary>
        public const int MaxLength = 100;

        /// <summary>How many genres one track's tag may contribute. A tag that lists fifteen things is
        /// describing nothing; the cap keeps one pathological file from flooding the facet.</summary>
        private const int MaxPerTag = 6;

        /// <summary>The ID3v1 genre list (Winamp's extension included) — index IS the value a
        /// parenthesised TCON carries.</summary>
        private static readonly string[] Id3v1 =
        {
            "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop",
            "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B", "Rap",
            "Reggae", "Rock", "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks",
            "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk", "Fusion", "Trance",
            "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise",
            "Alt. Rock", "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop", "Instrumental Rock",
            "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk", "Eurodance", "Dream",
            "Southern Rock", "Comedy", "Cult", "Gangsta Rap", "Top 40", "Christian Rap", "Pop/Funk", "Jungle",
            "Native American", "Cabaret", "New Wave", "Psychedelic", "Rave", "Showtunes", "Trailer", "Lo-Fi",
            "Tribal", "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
            "Folk", "Folk-Rock", "National Folk", "Swing", "Fast-Fusion", "Bebop", "Latin", "Revival",
            "Celtic", "Bluegrass", "Avantgarde", "Gothic Rock", "Progressive Rock", "Psychedelic Rock", "Symphonic Rock", "Slow Rock",
            "Big Band", "Chorus", "Easy Listening", "Acoustic", "Humour", "Speech", "Chanson", "Opera",
            "Chamber Music", "Sonata", "Symphony", "Booty Bass", "Primus", "Porn Groove", "Satire", "Slow Jam",
            "Club", "Tango", "Samba", "Folklore", "Ballad", "Power Ballad", "Rhythmic Soul", "Freestyle",
            "Duet", "Punk Rock", "Drum Solo", "A Cappella", "Euro-House", "Dance Hall", "Goa", "Drum & Bass",
            "Club-House", "Hardcore", "Terror", "Indie", "BritPop", "Afro-Punk", "Polsk Punk", "Beat",
            "Christian Gangsta Rap", "Heavy Metal", "Black Metal", "Crossover", "Contemporary Christian", "Christian Rock", "Merengue", "Salsa",
            "Thrash Metal", "Anime", "JPop", "Synthpop", "Abstract", "Art Rock", "Baroque", "Bhangra",
            "Big Beat", "Breakbeat", "Chillout", "Downtempo", "Dub", "EBM", "Eclectic", "Electro",
            "Electroclash", "Emo", "Experimental", "Garage", "Global", "IDM", "Illbient", "Industro-Goth",
            "Jam Band", "Krautrock", "Leftfield", "Lounge", "Math Rock", "New Romantic", "Nu-Breakz", "Post-Punk",
            "Post-Rock", "Psytrance", "Shoegaze", "Space Rock", "Trop Rock", "World Music", "Neoclassical", "Audiobook",
            "Audio Theatre", "Neue Deutsche Welle", "Podcast", "Indie Rock", "G-Funk", "Dubstep", "Garage Rock", "Psybient",
        };

        /// <summary>
        /// Every genre a raw tag names, normalised and de-duplicated, in the order the tag gave them.
        /// An unusable tag yields an empty list — never a null, never a "" row.
        /// </summary>
        public static IReadOnlyList<string> Split(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

            var pieces = new List<string>();
            var buffer = new StringBuilder();
            // Parenthesised ID3v1 indexes are their own tokens wherever they appear: "(17)Rock" is
            // ONE genre said twice, and "(17)(21)" is two. Handled here rather than by a regex over
            // the whole string so "Rock (17)" and "(17) Rock" collapse the same way.
            for (int i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (c == '(')
                {
                    var close = raw.IndexOf(')', i + 1);
                    if (close > i + 1 && int.TryParse(raw.AsSpan(i + 1, close - i - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                    {
                        Flush(pieces, buffer);
                        if (idx >= 0 && idx < Id3v1.Length) pieces.Add(Id3v1[idx]);
                        i = close;
                        continue;
                    }
                }
                // The four separators actually seen in the wild. A slash is included because
                // "Rock/Pop" is overwhelmingly two genres — the handful of real names that contain
                // one ("Jazz+Funk" uses a plus, "R&B" an ampersand) do not.
                if (c == ';' || c == '/' || c == ',' || c == '|' || c == '\0' || c == '\n' || c == '\r')
                {
                    Flush(pieces, buffer);
                    continue;
                }
                buffer.Append(c);
            }
            Flush(pieces, buffer);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outList = new List<string>();
            foreach (var p in pieces)
            {
                var n = Normalize(p);
                if (n == null || !seen.Add(n)) continue;
                outList.Add(n);
                if (outList.Count >= MaxPerTag) break;
            }
            return outList;
        }

        private static void Flush(List<string> into, StringBuilder buffer)
        {
            if (buffer.Length > 0) { into.Add(buffer.ToString()); buffer.Clear(); }
        }

        /// <summary>
        /// One genre's canonical spelling, or null when the token is not a genre at all. Trims,
        /// collapses inner whitespace, drops the tags that mean "unset", and title-cases a value that
        /// arrived all-lower or all-upper (leaving a hand-cased "BritPop" alone).
        /// </summary>
        public static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-', '.', '_');
            if (s.Length == 0) return null;
            // The spellings that mean "this field was never filled in" — plus the two that are true
            // but say nothing HERE. Keeping them would put a 4,000-strong "Unknown" pill at the top
            // of the facet, and a "Music" pill in the Music section, which is the opposite of useful.
            // (Measured on the first 200 tracks of the library: "Music" was the single commonest
            // value at 67 hits, ahead of Rock at 22 — it is what a couple of rippers write when the
            // user never chose.) This is the ONLY vocabulary judgement the normaliser makes: no
            // synonym folding, no hierarchy, nothing else is renamed or merged.
            if (Meaningless.Contains(s)) return null;
            // A pure number that was NOT in parentheses is a bare ID3v1 index — some taggers write
            // "17" — but it is also how a stray year or track number lands here, so only resolve it
            // when it is in range and let anything else go.
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                if (idx < 0 || idx >= Id3v1.Length) return null;
                var name = Id3v1[idx];
                return Meaningless.Contains(name) ? null : name; // index 12 IS "Other"
            }

            if (s.Length > MaxLength) s = s.Substring(0, MaxLength).TrimEnd();

            var hasLower = s.Any(char.IsLower);
            var hasUpper = s.Any(char.IsUpper);
            if (hasLower == hasUpper) return s; // mixed case was somebody's choice — keep it
            return TitleCase(s);
        }

        /// <summary>Title-case that respects hyphens and slashes and leaves short connectives alone
        /// ("rock and roll" → "Rock and Roll", "hip-hop" → "Hip-Hop").</summary>
        private static string TitleCase(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool atWordStart = true;
            int wordStart = 0;
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (atWordStart)
                {
                    wordStart = i;
                    var word = WordAt(s, i);
                    // Lower-case connectives, but never the first word.
                    if (wordStart > 0 && Connectives.Contains(word)) { sb.Append(char.ToLowerInvariant(c)); }
                    else sb.Append(char.ToUpperInvariant(c));
                    atWordStart = false;
                    continue;
                }
                if (c == ' ' || c == '-' || c == '/' || c == '&' || c == '+') { atWordStart = true; sb.Append(c); continue; }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string WordAt(string s, int start)
        {
            int end = start;
            while (end < s.Length && s[end] != ' ' && s[end] != '-' && s[end] != '/' && s[end] != '&' && s[end] != '+') end++;
            return s.Substring(start, end - start).ToLowerInvariant();
        }

        private static readonly HashSet<string> Connectives = new(StringComparer.OrdinalIgnoreCase)
        { "and", "of", "the", "a", "an", "or", "in", "n" };

        /// <summary>Tag values that carry no information — see <see cref="Normalize"/>.</summary>
        private static readonly HashSet<string> Meaningless = new(StringComparer.OrdinalIgnoreCase)
        { "unknown", "other", "none", "n/a", "na", "genre", "misc", "miscellaneous", "music", "audio", "general", "untagged" };

        /// <summary>
        /// The album's genres from its tracks' tags: every genre at least <paramref name="minShare"/>
        /// of the TAGGED tracks agree on, strongest first.
        /// </summary>
        /// <remarks>
        /// "Majority" is a share of the tracks that said ANYTHING, not of the tracks. Half a record's
        /// files being untagged is the normal case here and does not make the other half's verdict
        /// less true; measuring against the whole would leave those albums with no genre at all.
        /// Several genres are allowed out (the plan's word), so the threshold is a THIRD rather than
        /// half — an album whose tracks split 40/35/25 across three labels really is all three — but
        /// a genre one track in twenty mentions is that track's, not the album's.
        /// </remarks>
        public static IReadOnlyList<(string Genre, int Count)> RollUpAlbum(IEnumerable<string?> trackGenres, double minShare = 1.0 / 3.0, int max = 4)
        {
            var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
            int tagged = 0;
            foreach (var raw in trackGenres)
            {
                var list = Split(raw);
                if (list.Count == 0) continue;
                tagged++;
                foreach (var g in list)
                {
                    if (counts.TryGetValue(g, out var row)) counts[g] = (row.Display, row.Count + 1);
                    else counts[g] = (g, 1);
                }
            }
            if (tagged == 0) return Array.Empty<(string, int)>();

            var threshold = Math.Max(1, (int)Math.Ceiling(tagged * minShare));
            var kept = counts.Values
                .Where(r => r.Count >= threshold)
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(r => (r.Display, r.Count))
                .ToList();

            // A record whose tracks all disagree still HAS a genre — the most-said one. Falling back
            // rather than filing it under nothing is the difference between "this album is a bit of
            // everything" and "we know nothing about this album".
            if (kept.Count == 0)
            {
                var top = counts.Values.OrderByDescending(r => r.Count).ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase).First();
                kept.Add((top.Display, top.Count));
            }
            return kept;
        }

        /// <summary>
        /// An artist's headline genres: the ones most of their albums are filed under, strongest
        /// first, capped at <paramref name="max"/> (three — see <c>MusicArtistGenre</c>).
        /// </summary>
        public static IReadOnlyList<(string Genre, int Count)> RollUpArtist(IEnumerable<IEnumerable<string>> albumGenres, int max = 3)
        {
            var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var album in albumGenres)
            {
                // One vote per ALBUM, not per row: a record filed under four genres must not outvote
                // four records that agree.
                foreach (var g in album.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (counts.TryGetValue(g, out var row)) counts[g] = (row.Display, row.Count + 1);
                    else counts[g] = (g, 1);
                }
            }
            return counts.Values
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(r => (r.Display, r.Count))
                .ToList();
        }
    }
}
