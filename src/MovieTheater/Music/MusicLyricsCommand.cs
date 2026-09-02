using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Music
{
    /// <summary>
    /// Fills in lyrics from LRCLIB (music-plan.md §2.7) for tracks that have none. Embedded tag and
    /// sidecar <c>.lrc</c> lyrics are captured at ingest and always win — this pass only ever ADDS a
    /// row, it never touches an existing one, so an <c>embedded</c>/<c>sidecar</c> row cannot be
    /// overwritten by a worse internet match.
    ///
    /// <para>LRCLIB is keyless but asks for a descriptive User-Agent; we stay at ~2 requests/second.
    /// The lookup is by (artist, title, album, duration) — duration is what stops a cover/remix from
    /// matching, so tracks whose duration we never read are queried without it and take whatever
    /// LRCLIB's fuzzy search returns.</para>
    ///
    /// <para><b>Three ladders, and the order is the finding.</b> The first pass asked LRCLIB's
    /// <c>/api/get</c> — an EXACT match — using the FOLDER's artist, and gave up on 25,426 tracks
    /// (2026-09-01). It was mostly not LRCLIB's fault:
    /// <list type="number">
    /// <item><b>The file's own tags go first.</b> Folder identity wins everywhere in this library
    /// (§2.3) and that is right for browsing — but it is the wrong key for an outside catalogue.
    /// <c>TV on the Radio</c>'s <i>Wolf Like Me</i> sat unfetched because the folder reads
    /// <c>T.V. on the Radio</c>, which LRCLIB has never heard of, while the track's own
    /// <see cref="MusicTrack.TagArtist"/> read <c>TV on the Radio</c> exactly. Worse, a compilation
    /// folder (<i>The Pitchfork 500</i>, <i>Rolling Stones Top 500</i>, a game soundtrack) files
    /// EVERY track under one made-up artist, so every one of its lookups asked for a song by the
    /// wrong performer. 11,454 of the 25,426 misses carry a tag artist that differs from the folder's;
    /// only 391 carry none at all.</item>
    /// <item><b>Then the folder's names</b>, unchanged — the tags are not always the better answer
    /// (a rip with an empty or wrong artist tag), so this is a ladder, not a replacement.</item>
    /// <item><b>Then <c>/api/search</c></b>, which is fuzzy where <c>/api/get</c> is exact. A
    /// candidate still has to BE this track: same artist and title after folding case, accents,
    /// punctuation and a "feat." tail, and a duration within a few seconds. Nothing that fails that
    /// test is written — a wrong lyric is worse than no lyric.</item>
    /// <item><b>Last, the TITLE is read as "performer - song".</b> Some compilations put the
    /// performer nowhere but the title, so both name fields lie in the same way:
    /// <c>Fat Wreck Chords</c> — <i>"Poison Idea - Humanity"</i>, <c>Tony Hawk Pro Skater
    /// [Soundtrack]</c> — <i>"The Explosion - No Revolution"</i>. A leading track number
    /// (<i>"11 - Therapy"</i>) is the other shape of the same split, and there the LEFT side is
    /// noise and the artist we already have is right. This rung only fires when every other one has
    /// missed, and its candidates face the same artist+title+duration test — which is what makes
    /// guessing at a hyphen safe.</item>
    /// </list></para>
    ///
    /// <para>Spoken word (comedy, audiobooks — <see cref="MusicArtistKinds"/>) is skipped by default:
    /// there are no lyrics to a George Carlin bit, and 1,976 of the misses are that. <c>--include-spoken</c>
    /// puts them back.</para>
    ///
    /// <para><b>Re-opening the negative cache.</b> <c>LyricsCheckedUtc</c> is what stops a re-run
    /// asking LRCLIB the same question twice — so a run that asked a BETTER question has to be able
    /// to lift it. <c>--recheck</c> switches the work set to exactly the tracks a previous pass gave
    /// up on (stamped, still no lyrics row) and re-asks with the ladder above; it never touches a
    /// track that already has lyrics.</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run by default; <c>--apply</c> writes. Bounded by
    /// <c>--limit</c> TRACKS per run, resumable via <c>--after &lt;trackId&gt;</c>, printing
    /// <c>{ processed, remaining, nextCursor }</c>. Idempotent: every attempt stamps
    /// <c>MusicTrack.LyricsCheckedUtc</c> — hit or miss — so a re-run skips tracks LRCLIB has already
    /// declined (the negative cache), and the work set shrinks monotonically.</para>
    /// </summary>
    [Command("music-lyrics", Description = "Fetch missing lyrics from LRCLIB (dry-run unless --apply).")]
    public class MusicLyricsCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max TRACKS to query this run (default 200).")]
        public int Limit { get; set; } = 200;

        [CommandOption("after", Description = "Resume cursor: skip tracks whose id is ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("recheck", Description = "Re-ask for tracks a PREVIOUS run gave up on (stamped, still no lyrics) instead of the never-tried ones.")]
        public bool Recheck { get; set; }

        [CommandOption("include-spoken", Description = "Also query comedy/audiobook artists, which have no lyrics to find (default: skipped).")]
        public bool IncludeSpoken { get; set; }

        [CommandOption("verbose", Description = "Print a line per track, not just the hits.")]
        public bool Verbose { get; set; }

        /// <summary>~2 requests/second — LRCLIB publishes no hard limit but asks callers to be gentle.
        /// Spent per REQUEST, not per track: a track can walk up to four of them (tags, folder, two
        /// searches), and a burst of four is exactly what "be gentle" rules out.</summary>
        private const int ThrottleMs = 500;

        /// <summary>How far a SEARCH candidate's duration may sit from ours. Tighter than the refit
        /// pass's 6 s: that one already knows the track is right and is only choosing between takes,
        /// while here the duration is the only thing standing between us and a cover.</summary>
        private const double SearchSlackSec = 4.0;

        /// <summary>Shortest FOLDED name allowed to match by containment rather than equality. A
        /// one-letter artist ("X" folds to "x") is contained in half the catalogue, and the ±4 s
        /// duration window cannot carry the match alone -- below this, only equality counts.</summary>
        private const int MinContainmentLength = 4;
        private const string UserAgent = "MovieTheater-music-lyrics/1.0 (private home media library)";

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public MusicLyricsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            // Work set: playable tracks with no lyrics row. The "no lyrics row" test is an anti-join
            // so a track whose row exists (embedded/sidecar, or an earlier hit) is never touched.
            // --recheck flips WHICH half of the un-lyric'd library this is: the never-tried tracks, or
            // the ones a previous, worse question already gave up on.
            var pending = db.MusicTracks
                .Where(t => t.MissingSinceUtc == null
                            && (Recheck ? t.LyricsCheckedUtc != null : t.LyricsCheckedUtc == null)
                            && !db.MusicTrackLyrics.Any(l => l.TrackId == t.Id));

            // A stand-up set has no lyrics and never will; asking for 1,976 of them is 16 minutes of
            // somebody else's bandwidth for nothing.
            if (!IncludeSpoken)
                pending = pending.Where(t => t.Artist.Kind != MusicArtistKinds.Comedy
                                             && t.Artist.Kind != MusicArtistKinds.Audiobook);

            var totalPending = await pending.Where(t => t.Id > After).CountAsync();
            var batch = await pending
                .Where(t => t.Id > After)
                .OrderBy(t => t.Id)
                .Include(t => t.Artist)
                .Include(t => t.Album)
                .Take(Math.Max(1, Limit))
                .ToListAsync();

            int synced = 0, plainOnly = 0, misses = 0;
            var viaRung = new Dictionary<string, int>();   // which rung of the ladder actually paid
            using var http = CreateHttp();

            foreach (var track in batch)
            {
                var result = await FetchAsync(http, track);

                if (Apply) track.LyricsCheckedUtc = DateTime.UtcNow;

                if (result == null)
                {
                    misses++;
                    if (Verbose) w.WriteLine($"  · {track.Id} {track.Artist.Name} — {track.Title}: no match");
                    continue;
                }

                if (Apply)
                {
                    db.MusicTrackLyrics.Add(new MusicTrackLyrics
                    {
                        TrackId = track.Id,
                        PlainText = result.Plain,
                        SyncedLrc = result.Synced,
                        Source = "lrclib",
                        FetchedUtc = DateTime.UtcNow,
                    });
                }

                if (result.Synced != null) synced++; else plainOnly++;
                viaRung[result.Via] = viaRung.GetValueOrDefault(result.Via) + 1;
                if (Verbose)
                    w.WriteLine($"  + {track.Id} {track.Artist.Name} — {track.Title} "
                                + $"({(result.Synced != null ? "synced" : "plain")}, via {result.Via})");
            }

            if (Apply) await db.SaveChangesAsync();

            var remaining = Math.Max(0, totalPending - batch.Count);
            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var withLyrics = await db.MusicTrackLyrics.CountAsync();
            var totalTracks = await db.MusicTracks.CountAsync(t => t.MissingSinceUtc == null);

            w.WriteLine();
            w.WriteLine($"this run: {synced} synced, {plainOnly} plain-only, {misses} no match.");
            if (viaRung.Count > 0)
                w.WriteLine("found by: " + string.Join(", ", viaRung.OrderByDescending(kv => kv.Value)
                                                               .Select(kv => $"{kv.Key} {kv.Value}")));
            w.WriteLine($"coverage: {withLyrics}/{totalTracks} tracks have lyrics " +
                        $"({(totalTracks == 0 ? 0 : 100.0 * withLyrics / totalTracks):F1}%).");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: {nextCursor} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return http;
        }

        /// <summary>A lyric LRCLIB gave us, and which rung of the ladder found it.</summary>
        private sealed record Hit(string? Plain, string? Synced, string Via);

        /// <summary>One (artist, album) pair to ask LRCLIB about — the file's own tags, or the folder's names.</summary>
        private readonly record struct Identity(string Artist, string? Album, string Via);

        /// <summary>
        /// The names to try, best-informed first. The file's TAGS lead: the folder is OUR filing
        /// system — and on a compilation it is not even the performer — while the tag is what the
        /// person who made the file wrote down, which is the same thing LRCLIB's uploaders had in
        /// front of them. The folder still gets its turn: a tag can be blank, or wrong.
        /// Deduplicated case-insensitively, so the common case (they agree) stays ONE request.
        /// </summary>
        private static IEnumerable<Identity> Identities(MusicTrack track)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new[]
            {
                new Identity(track.TagArtist ?? "", track.TagAlbum ?? track.Album?.Title, "tags"),
                new Identity(track.Artist.Name, track.Album?.Title, "folder"),
            };
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.Artist)) continue;
                if (!seen.Add(candidate.Artist.Trim() + "|" + (candidate.Album ?? "").Trim())) continue;
                yield return candidate;
            }
        }

        /// <summary>The artist name to search under: the file's own tag unless it is BLANK -- a
        /// blank tag is exactly the rip the folder fallback exists for, and <c>??</c> alone would
        /// hand an empty string to the fuzzy rungs and silence them.</summary>
        private static string BestArtist(MusicTrack track) =>
            string.IsNullOrWhiteSpace(track.TagArtist) ? track.Artist.Name.Trim() : track.TagArtist.Trim();

        /// <summary>Walk the ladder: exact by tags, exact by the folder's names, the fuzzy search,
        /// and finally the title read as "performer - song".</summary>
        private static async Task<Hit?> FetchAsync(HttpClient http, MusicTrack track)
        {
            foreach (var identity in Identities(track))
            {
                var hit = await GetAsync(http, track, identity);
                if (hit != null) return hit;
            }
            return await SearchAsync(http, track) ?? await SplitTitleAsync(http, track);
        }

        /// <summary>One LRCLIB /api/get lookup — an EXACT match on the names given. Returns null on a
        /// miss, a network error, or a hit that carries no actual text (LRCLIB returns instrumental
        /// entries with both fields empty).</summary>
        private static async Task<Hit?> GetAsync(HttpClient http, MusicTrack track, Identity identity)
        {
            var query = new List<string>
            {
                "artist_name=" + Uri.EscapeDataString(identity.Artist),
                "track_name=" + Uri.EscapeDataString(track.Title),
            };
            if (!string.IsNullOrWhiteSpace(identity.Album))
                query.Add("album_name=" + Uri.EscapeDataString(identity.Album));
            if (track.DurationSec is double d && d > 0)
                query.Add("duration=" + ((int)Math.Round(d)).ToString());

            var json = await GetStringAsync(http, "https://lrclib.net/api/get?" + string.Join("&", query));
            if (json == null) return null;                  // 404 = LRCLIB has nothing under these names

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var plain = Text(root, "plainLyrics");
                var syncedLrc = KeepOnlyFittingCues(Text(root, "syncedLyrics"), track.DurationSec);
                if (plain == null && syncedLrc == null) return null;
                return new Hit(plain, syncedLrc, identity.Via);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The fuzzy rung. <c>/api/search</c> answers with everything it holds under a name, so the
        /// judging happens HERE, and it is deliberately strict: same artist and same title once case,
        /// accents, punctuation and a "feat." tail are folded away, and a duration within
        /// <see cref="SearchSlackSec"/>. A wrong lyric is worse than no lyric — it is silently wrong,
        /// on a pane nobody double-checks — so a track whose duration we never read is refused
        /// outright here: there would be nothing left to disqualify a cover with.
        /// </summary>
        private static async Task<Hit?> SearchAsync(HttpClient http, MusicTrack track)
        {
            if (track.DurationSec is not double duration || duration <= 0) return null;

            var artist = BestArtist(track);
            var title = track.Title.Trim();
            return await BestMatchAsync(http, artist, title, duration, wide: true, via: "search");
        }

        /// <summary>
        /// The last rung: the performer is in the TITLE, not in either name field. A compilation that
        /// files everything under its own name often writes the track as "Poison Idea - Humanity",
        /// and then the artist we asked with was never going to work.
        ///
        /// <para>Two readings of the same hyphen, and the LEFT side decides which: a number is a
        /// track index ("11 - Therapy"), so the artist we already have stands and only the title is
        /// cleaned; anything else is read as the performer. Both readings are still judged by
        /// <see cref="BestMatchAsync"/>'s artist + title + duration test, which is the whole reason
        /// it is safe to guess at a hyphen at all — and this rung is reached only when everything
        /// else has already missed.</para>
        /// </summary>
        private static async Task<Hit?> SplitTitleAsync(HttpClient http, MusicTrack track)
        {
            if (track.DurationSec is not double duration || duration <= 0) return null;

            var match = TitleSplit.Match(track.Title.Trim());
            if (!match.Success) return null;

            var left = match.Groups["left"].Value.Trim();
            var right = match.Groups["right"].Value.Trim();
            if (right.Length == 0) return null;

            var leftIsTrackNumber = TrackNumber.IsMatch(left);
            var artist = leftIsTrackNumber ? BestArtist(track) : left;
            if (artist.Length == 0) return null;

            // The artist-scoped query only: the free-text one would be searching for a name this rung
            // is merely GUESSING at, and a wrong guess is exactly what the strict test then has to
            // throw away — at the cost of a request per track that will never pay.
            return await BestMatchAsync(http, artist, right, duration, wide: false,
                                        via: leftIsTrackNumber ? "title-trim" : "title-split");
        }

        /// <summary>
        /// Ask <c>/api/search</c> under one (artist, title) and return the best candidate that could
        /// actually BE this track. <paramref name="wide"/> adds the free-text query, which reaches
        /// entries filed under a spelling neither of our names uses — worth a request when the names
        /// came from the file, wasted when they came from a guess.
        /// </summary>
        private static async Task<Hit?> BestMatchAsync(HttpClient http, string artist, string title,
                                                       double duration, bool wide, string via)
        {
            if (artist.Length == 0 || title.Length == 0) return null;

            var queries = new List<string>
            {
                "artist_name=" + Uri.EscapeDataString(artist) + "&track_name=" + Uri.EscapeDataString(title),
            };
            if (wide) queries.Add("q=" + Uri.EscapeDataString(artist + " " + title));

            foreach (var query in queries)
            {
                var json = await GetStringAsync(http, "https://lrclib.net/api/search?" + query);
                if (json == null) continue;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                    var best = doc.RootElement.EnumerateArray()
                        .Select(e => new
                        {
                            Plain = Text(e, "plainLyrics"),
                            Synced = Text(e, "syncedLyrics"),
                            Artist = Text(e, "artistName") ?? "",
                            Title = Text(e, "trackName") ?? "",
                            Duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                                ? d.GetDouble() : 0,
                        })
                        .Where(c => (c.Plain != null || c.Synced != null)
                                    && SameName(c.Artist, artist)
                                    && SameName(c.Title, title)
                                    && Math.Abs(c.Duration - duration) <= SearchSlackSec)
                        .OrderByDescending(c => c.Synced != null)
                        .ThenBy(c => Math.Abs(c.Duration - duration))
                        .FirstOrDefault();

                    if (best == null) continue;
                    var syncedLrc = KeepOnlyFittingCues(best.Synced, duration);
                    if (best.Plain == null && syncedLrc == null) continue;
                    return new Hit(best.Plain, syncedLrc, via);
                }
                catch
                {
                    // A malformed body is this QUERY's problem — try the next one, not the next track.
                }
            }

            return null;
        }

        /// <summary>
        /// LRCLIB matched on the duration it was TOLD, which is the uploader's metadata — not a
        /// property of the cues. The two can disagree, and when they do the file is timed for a
        /// different version and every line lands late (2026-08-17: CHVRCHES' Recover, a 225.9 s
        /// track, held cues running to 4:14). Drop the timings and keep the words.
        ///
        /// <para>The MISMATCH test, not the mere cue-fit smell: a couple of seconds of overhang is a
        /// tail written at the fade, and refusing that would import plain text for a song whose
        /// timings were fine all along.</para>
        /// </summary>
        private static string? KeepOnlyFittingCues(string? syncedLrc, double? durationSec) =>
            syncedLrc != null && MusicLyricsFit.IsVersionMismatch(syncedLrc, durationSec) ? null : syncedLrc;

        /// <summary>Every request goes through here, so the throttle counts REQUESTS: a track that
        /// walks the whole ladder must not fire four of them back to back at somebody's free API.
        /// Null on anything but a 200.</summary>
        private static async Task<string?> GetStringAsync(HttpClient http, string url)
        {
            await Task.Delay(ThrottleMs);
            try
            {
                var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Are these two the same name, allowing for how differently two catalogues write it?
        /// Containment rather than equality, because one side routinely carries what the other omits
        /// ("Beyonce" vs "Beyonce feat. Jay-Z", "Therapy" vs "11 - Therapy") -- but only when the
        /// contained side is at least <see cref="MinContainmentLength"/> folded characters, so a
        /// one-letter name can only ever match its equal.</summary>
        private static bool SameName(string a, string b)
        {
            var x = Fold(a);
            var y = Fold(b);
            if (x.Length == 0 || y.Length == 0) return false;
            if (x == y) return true;
            if (Math.Min(x.Length, y.Length) < MinContainmentLength) return false;
            return x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
        }

        /// <summary>Case, accents, punctuation, "and"/"&amp;" and a trailing "feat …" folded away — what
        /// is left is the part two catalogues can be expected to agree on.</summary>
        private static string Fold(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(ch);
            }
            var folded = FeatSuffix.Replace(sb.ToString().ToLowerInvariant(), "");
            return NonAlphanumeric.Replace(folded.Replace(" and ", " & "), "");
        }

        private static readonly Regex FeatSuffix =
            new(@"\s*[\(\[]?\b(feat|ft|featuring)\b\.?.*$", RegexOptions.Compiled);

        private static readonly Regex NonAlphanumeric =
            new(@"[^a-z0-9&]+", RegexOptions.Compiled);

        /// <summary>"performer - song", on a SPACED separator only: an unspaced hyphen belongs to the
        /// words around it ("Jay-Z", "Wham-Bam"), and splitting there would invent a performer.
        /// Non-greedy, so the first separator wins and the rest stays with the song.</summary>
        private static readonly Regex TitleSplit =
            new(@"^(?<left>.+?)\s+[-–—]\s+(?<right>.+)$", RegexOptions.Compiled);

        /// <summary>A leading "01"/"1." is a track index, not a performer.</summary>
        private static readonly Regex TrackNumber =
            new(@"^\d{1,3}\.?$", RegexOptions.Compiled);

        private static string? Text(JsonElement root, string property) =>
            root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(el.GetString())
                ? el.GetString()
                : null;
    }
}
