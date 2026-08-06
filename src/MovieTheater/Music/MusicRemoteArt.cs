using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Music
{
    /// <summary>
    /// The remote album-art lookup (music-plan.md §2.5): MusicBrainz release search → Cover Art
    /// Archive front image, falling back to the iTunes Search API.
    ///
    /// <para>Lifted out of <see cref="MusicArtCommand"/> so the admin backfill endpoint can share one
    /// implementation. That split is load-bearing rather than tidiness: album art has to be written by
    /// the process that owns the live images mount, and a dev-box CLI run does not — exactly the
    /// reason <c>/API/Admin/IngestReview/BackfillPosters</c> exists for movie posters. Two copies of
    /// this logic would drift on the throttle or the User-Agent, both of which are conditions of use
    /// rather than preferences.</para>
    /// </summary>
    public static class MusicRemoteArt
    {
        /// <summary>MusicBrainz asks for ≤1 request/second and a contact-bearing User-Agent; both are
        /// conditions of use, not suggestions. Callers must space their calls by this much.</summary>
        public const int MusicBrainzThrottleMs = 1100;

        private const string RemoteUserAgent = "MovieTheater-music-art/1.0 (private home media library)";

        /// <summary>Process-wide gate for remote lookups. Both in-process callers (the lazy image route
        /// and the admin bulk warm) take THIS one, so the rate limit holds no matter how they interleave
        /// — two independent gates would silently double the request rate.
        ///
        /// <para>The lazy route takes it with a zero timeout (busy ⇒ show a placeholder, never queue a
        /// web request behind someone else's lookup); the bulk warm waits its turn.</para></summary>
        public static readonly SemaphoreSlim Gate = new(1, 1);

        private static DateTime lastCallUtc = DateTime.MinValue;

        /// <summary>Await immediately before a lookup, holding <see cref="Gate"/>, to keep consecutive
        /// MusicBrainz calls ≥1s apart.</summary>
        public static async Task SpaceCallAsync()
        {
            var wait = MusicBrainzThrottleMs - (int)(DateTime.UtcNow - lastCallUtc).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait);
            lastCallUtc = DateTime.UtcNow;
        }

        /// <summary>A client carrying the User-Agent MusicBrainz requires — they 403 anonymous callers.</summary>
        public static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(RemoteUserAgent);
            return http;
        }

        /// <summary>Best available front cover, or null when neither source has one. Any network hiccup
        /// is a miss, never a throw — the caller stamps the negative cache either way, and a genuinely
        /// transient failure costs one album until someone clears its ArtCheckedUtc.
        ///
        /// <para><paramref name="spaceCall"/> is awaited before every MusicBrainz search AFTER the
        /// first — the caller already spaced that one. Omitting it means this method will only ever
        /// make the one search, which keeps a caller that throttles externally correct by default.</para>
        /// </summary>
        public static async Task<byte[]?> FetchAsync(HttpClient http, string artist, string album, Func<Task>? spaceCall = null)
        {
            // "Title-only" means the artist column holds a WORK or a studio rather than a performer —
            // a root-level soundtrack folder, or a bucket like "Disney". The credit then proves
            // nothing, so the title must carry the match on its own.
            var redundant = ArtistAddsNothing(artist, album);
            var titleOnly = redundant || IsBucketArtist(artist);

            var bytes = await CoverArtForQueryAsync(
                http, $"artist:\"{Sanitize(artist)}\" AND release:\"{Sanitize(album)}\"", album, artist, titleOnly);
            if (bytes != null) return bytes;

            // An album folder sitting at the library ROOT ("Avenue Q (2003) [Soundtrack]", "Django
            // Unchained (2012) [Soundtrack]") makes the record its own artist. As a Lucene constraint
            // that name matches nothing and takes the perfectly findable release down with it. When the
            // artist adds nothing to the title, ask again without it — and only then: for a real artist
            // the constraint is exactly what keeps a same-named release by someone else out.
            if (titleOnly && spaceCall != null)
            {
                await spaceCall();
                bytes = await SoundtrackCoverAsync(http, album);
                if (bytes != null) return bytes;

                await spaceCall();
                bytes = await CoverArtForQueryAsync(
                    http, $"release:\"{Sanitize(album)}\"", album, artist, titleOnly: true);
                if (bytes != null) return bytes;
            }

            // iTunes is asked LAST and only for real artists. It has no artist field in the query, so
            // it answers any term with its best guess; for a work-title album that guess is unverifiable
            // and was a reliable source of confident nonsense.
            if (titleOnly) return null;
            return await ItunesArtworkAsync(http, $"{artist} {album}", album, artist);
        }

        /// <summary>True when the artist name is the album's own name dressed up — a self-titled flat
        /// folder, or a root-level album folder still wearing its "(year) [tag]" decoration.</summary>
        private static bool ArtistAddsNothing(string artist, string album)
        {
            static string Fold(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var a = Fold(artist);
            var b = Fold(album);
            return a.Length > 0 && b.Length > 0 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
        }

        /// <summary>Runs one MusicBrainz release search and returns the first cover any of its hits can
        /// produce — trying each release, then each release GROUP. The group is the one that usually
        /// pays: Cover Art Archive art is frequently filed against the group rather than the specific
        /// release the search happened to rank first, so release-only lookups miss covers that are
        /// sitting right there.</summary>
        private static async Task<byte[]?> CoverArtForQueryAsync(
            HttpClient http, string query, string album, string artist, bool titleOnly)
        {
            var candidates = await MusicBrainzCandidatesAsync(http, query);

            // Releases first, then the GROUPS of the releases that passed. The group is the one that
            // usually pays: Cover Art Archive art is frequently filed against the group rather than the
            // specific release a search ranked first.
            var groups = new List<string>();
            foreach (var c in candidates)
            {
                if (!Accepts(c.Title, c.Credit, album, artist, titleOnly)) continue;
                if (c.GroupId != null && !groups.Contains(c.GroupId)) groups.Add(c.GroupId);
                var bytes = await TryGetAsync(http, $"https://coverartarchive.org/release/{c.Id}/front-500");
                if (MusicArtStore.LooksLikeCover(bytes)) return bytes;
            }
            foreach (var rgid in groups)
            {
                var bytes = await TryGetAsync(http, $"https://coverartarchive.org/release-group/{rgid}/front-500");
                if (MusicArtStore.LooksLikeCover(bytes)) return bytes;
            }
            return null;
        }

        private sealed record MbCandidate(string Id, string? GroupId, string Title, string Credit);

        private static async Task<List<MbCandidate>> MusicBrainzCandidatesAsync(HttpClient http, string query)
        {
            var found = new List<MbCandidate>();
            var json = await TryGetStringAsync(http, $"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(query)}&fmt=json&limit=8");
            if (json == null) return found;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("releases", out var releases)) return found;
                foreach (var release in releases.EnumerateArray())
                {
                    if (!release.TryGetProperty("id", out var id) || id.GetString() is not string s) continue;
                    string? gid = null;
                    if (release.TryGetProperty("release-group", out var group)
                        && group.TryGetProperty("id", out var g))
                        gid = g.GetString();
                    var title = release.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    found.Add(new MbCandidate(s, gid, title, CreditOf(release)));
                }
            }
            catch { /* malformed response = miss */ }
            return found;
        }

        private static string CreditOf(JsonElement element)
        {
            if (!element.TryGetProperty("artist-credit", out var credits)
                || credits.ValueKind != JsonValueKind.Array) return "";
            return string.Join(" ", credits.EnumerateArray()
                .Select(c => c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                .Where(n => n.Length > 0));
        }

        /// <summary>
        /// Soundtracks, cast recordings and game scores, matched by WORK + TYPE rather than by credit.
        ///
        /// <para>These have no artist to match on — the folder bucket IS the work — and MusicBrainz
        /// credits them to a composer, a cast or Various Artists. Matching on title alone is not safe
        /// either: it returns a Swedish stage production for "A Clockwork Orange" and a chiptune parody
        /// for "Dr. Horrible's Sing-Along Blog". So require TWO independent things — MusicBrainz must
        /// have typed the release group as a Soundtrack, AND its title must begin with ours.</para>
        /// </summary>
        private static async Task<byte[]?> SoundtrackCoverAsync(HttpClient http, string album)
        {
            var query = $"releasegroup:\"{Sanitize(album)}\" AND secondarytype:soundtrack";
            var json = await TryGetStringAsync(http,
                $"https://musicbrainz.org/ws/2/release-group/?query={Uri.EscapeDataString(query)}&fmt=json&limit=8");
            if (json == null) return null;
            var ours = Fold(album);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("release-groups", out var groups)) return null;
                foreach (var rg in groups.EnumerateArray())
                {
                    if (!rg.TryGetProperty("id", out var id) || id.GetString() is not string gid) continue;
                    var title = rg.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    if (LooksLikeImpostor(title)) continue;

                    var typed = rg.TryGetProperty("secondary-types", out var st)
                                && st.ValueKind == JsonValueKind.Array
                                && st.EnumerateArray().Any(x => string.Equals(x.GetString(), "Soundtrack",
                                                                              StringComparison.OrdinalIgnoreCase));
                    if (!typed) continue;

                    // Prefix, not containment: "Avenue Q: The Musical" begins with ours; "Avenue Q
                    // Swings" does not.
                    var got = Fold(title);
                    if (got != ours && !got.StartsWith(ours + " ", StringComparison.Ordinal)) continue;

                    var bytes = await TryGetAsync(http, $"https://coverartarchive.org/release-group/{gid}/front-500");
                    if (MusicArtStore.LooksLikeCover(bytes)) return bytes;
                }
            }
            catch { /* malformed response = miss */ }
            return null;
        }

        private static async Task<byte[]?> ItunesArtworkAsync(HttpClient http, string searchTerm, string album, string artist)
        {
            var term = Uri.EscapeDataString(searchTerm);
            var json = await TryGetStringAsync(http, $"https://itunes.apple.com/search?term={term}&entity=album&limit=5");
            if (json == null) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
                foreach (var result in results.EnumerateArray())
                {
                    var gotTitle = result.TryGetProperty("collectionName", out var cn) ? cn.GetString() : null;
                    var gotArtist = result.TryGetProperty("artistName", out var an) ? an.GetString() : null;
                    // Both names must agree. This is a fuzzy store answering a free-text term: it once
                    // paired Clutch's "B-Sides and Rarities" with Cake's, and it is where an unrelated
                    // "Beauty and the Beast" on another label came from.
                    if (Similar(gotArtist, artist) < 0.7 || Similar(gotTitle, album) < 0.75) continue;
                    if (LooksLikeImpostor(gotTitle)) continue;

                    if (!result.TryGetProperty("artworkUrl100", out var art)) continue;
                    var url = art.GetString();
                    if (url == null) continue;
                    // iTunes serves any size by rewriting the dimension segment of the URL.
                    var bytes = await TryGetAsync(http, url.Replace("100x100", "600x600"));
                    if (MusicArtStore.LooksLikeCover(bytes)) return bytes;
                }
            }
            catch { /* malformed response = miss */ }
            return null;
        }

        /// <summary>Lucene special characters in an album/artist name would otherwise break the
        /// MusicBrainz query (or silently match nothing). Each is replaced by a SPACE, never deleted:
        /// the hyphen is in this set, so deleting collapsed "Sing-Along" to "SingAlong" and took every
        /// hyphenated title in the library down with it.</summary>
        public static string Sanitize(string s) =>
            CollapseSpaces(new string(s.Select(c => "\\+-!(){}[]^\"~*?:/".Contains(c) ? ' ' : c).ToArray()));

        private static string CollapseSpaces(string s) =>
            string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // ── verifying what came back ────────────────────────────────────────────────────────────────
        //
        // Everything below exists because this lookup used to accept the FIRST result of a search
        // without checking it against the album it was asked about. That is how "Disney's Greatest
        // Hits" ended up wearing Queen's Greatest Hits, "Disney's Hero Songs" got High School Musical
        // 3, three Disney compilations got ZOMBIES 1/2/3, Led Zeppelin I got Coda's sleeve and
        // Gorillaz' Singles & B-Sides got a Bruno Mars single. A confident wrong cover is worse than
        // no cover, so a candidate now has to look like the record we asked for.

        /// <summary>Comparison form: accents stripped, punctuation removed, lowercased, single-spaced.</summary>
        private static string Fold(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var norm = s.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(norm.Length);
            foreach (var c in norm)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                    == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else sb.Append(' ');
            }
            return CollapseSpaces(sb.ToString());
        }

        /// <summary>The volume/part numbers in a name. "Vol. 4" and "Vol. 2" are different records even
        /// though the letters agree, so disagreeing numbers veto a match outright.</summary>
        private static HashSet<string> Numbers(string? s)
        {
            var set = new HashSet<string>();
            foreach (var tok in Fold(s).Split(' '))
                if (tok.Length > 0 && tok.All(char.IsDigit) && int.TryParse(tok, out var n))
                    set.Add(n.ToString());
            return set;
        }

        public static double Similar(string? a, string? b)
        {
            var fa = Fold(a);
            var fb = Fold(b);
            if (fa.Length == 0 || fb.Length == 0) return 0;

            var na = Numbers(a);
            var nb = Numbers(b);
            if (na.Count > 0 && nb.Count > 0 && !na.Overlaps(nb)) return 0;

            if (fa == fb) return 1;

            // Containment is the usual shape here ("Beware" inside "Beware: Complete Singles"), but it
            // only counts when the shorter name carries real weight — "Jet Set" sits inside "Jet Set
            // Radio Future Original Sound Tracks" and is a different record entirely.
            var (shortest, longest) = fa.Length <= fb.Length ? (fa, fb) : (fb, fa);
            // 0.6 of the longer name, floor of 10 characters. This threshold is load-bearing and was
            // measured, not guessed: at 0.5, "Greatest Hits" (13) counts as contained in "Disney's
            // Greatest Hits" (22) and Queen's cover is accepted for the Disney compilation again —
            // the exact bug this gate exists to stop. The cost of holding the line here is that a
            // heavily subtitled edition can fail to match on the release title; those are recovered
            // by the release-GROUP fallback instead.
            if (longest.Contains(shortest, StringComparison.Ordinal)
                && shortest.Length >= Math.Max(10, 0.6 * longest.Length))
                return 0.93;

            return DiceCoefficient(fa, fb);
        }

        /// <summary>Bigram overlap — a cheap stand-in for a full edit-distance ratio, and enough to
        /// separate "Hybrid Theory" from "Hybrid Theory" spelled slightly differently.</summary>
        private static double DiceCoefficient(string a, string b)
        {
            if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;
            var pairs = new Dictionary<string, int>();
            for (int i = 0; i < a.Length - 1; i++)
            {
                var k = a.Substring(i, 2);
                pairs[k] = pairs.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            int hits = 0, total = b.Length - 1;
            for (int i = 0; i < b.Length - 1; i++)
            {
                var k = b.Substring(i, 2);
                if (pairs.TryGetValue(k, out var c) && c > 0) { pairs[k] = c - 1; hits++; }
            }
            return 2.0 * hits / (a.Length - 1 + total);
        }

        /// <summary>Re-recordings that are not the album, however well their name matches.</summary>
        public static bool LooksLikeImpostor(string? title) =>
            Fold(title) is var t && (t.Contains("karaoke") || t.Contains("tribute")
                || t.Contains("made famous by") || t.Contains("in the style of")
                || t.Contains("as made popular") || t.Contains("cover version"));

        /// <summary>Studio/label names that sit in the artist column but are not performers. Their
        /// credit matches almost everything the studio released, so albums filed under one are matched
        /// on the WORK (title-led) and never on the credit.</summary>
        private static readonly HashSet<string> BucketArtists = new(StringComparer.OrdinalIgnoreCase)
        {
            "disney", "walt disney", "walt disney records", "pixar", "various artists", "various",
        };

        public static bool IsBucketArtist(string artist) => BucketArtists.Contains(Fold(artist));

        /// <summary>Does this candidate look like the record we asked about?
        ///
        /// <para>When <paramref name="titleOnly"/>, the credit is not evidence — a soundtrack's
        /// "artist" is a work title and MusicBrainz files it under a composer, a cast or Various
        /// Artists — so the title has to carry the identity alone and is held to a much higher bar.</para>
        /// </summary>
        public static bool Accepts(string? gotTitle, string? gotCredit, string album, string artist, bool titleOnly)
        {
            if (LooksLikeImpostor(gotTitle) && !LooksLikeImpostor(album)) return false;
            if (titleOnly) return Similar(gotTitle, album) >= 0.85;
            return Similar(gotTitle, album) >= 0.72 && Similar(gotCredit, artist) >= 0.6;
        }

        private static async Task<byte[]?> TryGetAsync(HttpClient http, string url)
        {
            try
            {
                var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch { return null; }
        }

        private static async Task<string?> TryGetStringAsync(HttpClient http, string url)
        {
            try
            {
                var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }
    }
}
