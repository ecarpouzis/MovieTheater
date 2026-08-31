using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
    /// Track-level popularity from Last.fm (2026-08-31) — how widely heard each SONG is, which is the
    /// one question <c>music-enrich</c>'s album number cannot answer.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is its own command.</b> <c>music-enrich</c>'s queue is album-shaped: it walks
    /// <c>MusicAlbum</c> rows and stamps them. This queue is TRACK-shaped and an order of magnitude
    /// longer (60,797 rows against 4,166), so folding it in would have meant one command with two
    /// cursors that terminate at wildly different times — the failure the album command already
    /// documents from the day the rating leg was bolted onto the popularity leg's stamp.</para>
    ///
    /// <para><b>One request per ARTIST, not per track.</b> Last.fm will answer
    /// <c>track.getinfo</c> exactly, but that is one request per row and this library has 60,797 of
    /// them. <c>artist.gettoptracks&amp;limit=1000</c> returns an artist's whole ranked catalogue in
    /// one answer, so the same library costs ~7,900 requests, and it is the SAME answer for every
    /// track by that artist — which the disk cache then makes free for the rest of the run and every
    /// run after it. The price is a name join, measured at 97–100% before this was written (see
    /// <see cref="MusicLastFm.ParseTopTracks"/>).</para>
    ///
    /// <para><b>The queue is still tracks, and that is what makes it bounded.</b> <c>--take</c> caps
    /// TRACKS, so a run's request count is capped by it too (usually far below it, because tracks
    /// ordered by Id arrive grouped by artist). The stop condition is
    /// <c>PopularityCheckedUtc IS NULL</c> ordered by Id — the cursor matches the ordering — and the
    /// stamp goes on a MISS as well as a hit, so the queue shrinks monotonically and terminates. A
    /// miss here is ordinary, not a failure: a deep cut outside an artist's top 1,000 simply is not
    /// in the answer.</para>
    ///
    /// <para><b>What it never does.</b> It writes nothing without <c>--apply</c>, touches nothing
    /// under the music root, and never lowers a score to zero on a miss — "we did not find it this
    /// time" is not "nobody has heard it".</para>
    /// </remarks>
    [Command("music-track-popularity", Description = "Per-TRACK popularity from Last.fm artist top-tracks, cached raw on disk (dry-run unless --apply).")]
    public class MusicTrackPopularityCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("dry-run", Description = "Force a dry run. Redundant (dry is the default) but accepted, so the safe spelling is never a typo.")]
        public bool DryRun { get; set; }

        [CommandOption("take", Description = "Max TRACKS to resolve this run (default 500).")]
        public int Take { get; set; } = 500;

        [CommandOption("after", Description = "Resume cursor: skip tracks whose Id is ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("artist", Description = "Only tracks under this MusicArtist id — for filling one shelf without disturbing the cursor.")]
        public int? ArtistId { get; set; }

        [CommandOption("recheck", Description = "Also re-ask for tracks already stamped (ignores the negative cache; respects the response cache's TTL).")]
        public bool Recheck { get; set; }

        [CommandOption("cache-dir", Description = "Raw response cache root. Default: data/music-cache (gitignored).")]
        public string? CacheDir { get; set; }

        [CommandOption("verbose", Description = "Print a line per track, not just the summary.")]
        public bool Verbose { get; set; }

        /// <summary>Popularity drifts, so a cached ranking older than this is re-asked — the same
        /// window <c>music-enrich</c> holds album listener counts for.</summary>
        private static readonly TimeSpan PopularityTtl = TimeSpan.FromDays(120);

        /// <summary>How many of an artist's tracks to ask for. Last.fm's ceiling for one page, and
        /// deliberately at the ceiling: the deep cuts are exactly the rows a smaller page would leave
        /// unmatched, and they cost nothing extra — it is one request either way.</summary>
        private const int TopTrackLimit = 1000;

        /// <summary>
        /// Tag values that name a COMPILATION rather than a performer. Asking Last.fm about these
        /// returns somebody's novelty act, not the artist on the record, so a track wearing one falls
        /// back to its folder artist instead — and if that is a compilation too, it simply misses.
        /// </summary>
        private static readonly HashSet<string> NotAnArtist = new(StringComparer.OrdinalIgnoreCase)
        {
            "various artists", "various", "va", "unknown artist", "unknown", "soundtrack",
            "original soundtrack", "ost", "compilation", "no artist",
        };

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public MusicTrackPopularityCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (DryRun) Apply = false;

            var apiKey = config.LastFmApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // The whole command is one source. There is no half of it that works without a key,
                // so unlike music-enrich this refuses rather than degrading — a run that silently did
                // nothing would stamp the library "asked" and hand the keyed run an empty queue.
                w.WriteLine("Last.fm: not configured (set LastFmApiKey in appsettings) — nothing to do.");
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            var cache = new MusicResponseCache(CacheDir);

            var queue = db.MusicTracks.AsNoTracking()
                .Where(t => t.MissingSinceUtc == null && t.Id > After);
            if (!Recheck) queue = queue.Where(t => t.PopularityCheckedUtc == null);
            if (ArtistId != null) queue = queue.Where(t => t.ArtistId == ArtistId.Value);

            var pendingTotal = await queue.CountAsync();
            var batch = await queue
                .OrderBy(t => t.Id)
                .Take(Math.Max(1, Take))
                .Select(t => new TrackRow(t.Id, t.Title, t.TagArtist, t.Artist.Name))
                .ToListAsync();

            int hits = 0, misses = 0, requests = 0, cacheHits = 0, errors = 0, fallbacks = 0;
            // One artist is asked about ONCE per run however many of their tracks are in the batch.
            // The disk cache would make the repeat cheap; this makes it free, and keeps the run's
            // request count legible in the summary.
            var rankings = new Dictionary<string, ArtistAnswer>(StringComparer.OrdinalIgnoreCase);
            var updates = new Dictionary<int, (int? Score, long? Listeners)>();
            using var http = MusicRemoteArt.CreateHttp();

            foreach (var track in batch)
            {
                var tagArtist = Usable(track.TagArtist) ? track.TagArtist!.Trim() : null;
                var folderArtist = track.ArtistName ?? "";
                var key = MusicTrackTitles.Normalize(track.Title);

                long? listeners = null;
                var asked = false;

                // The tag artist first: on a compilation it is the only field that names who is
                // actually playing, and the folder is called "Disney".
                var candidates = Candidates(tagArtist, folderArtist).ToList();
                for (var attempt = 0; attempt < candidates.Count; attempt++)
                {
                    var candidate = candidates[attempt];
                    try
                    {
                        var answer = await RankingAsync(http, cache, apiKey!, candidate, rankings);
                        if (answer.WasRequest) requests++;
                        if (answer.FromCache) cacheHits++;
                        // ANSWERED, not "has entries". Last.fm saying "I have never heard of this
                        // artist" is knowledge, and it must close the queue for their tracks — the
                        // stop condition is "we asked", and conflating an empty answer with an
                        // unreachable server left those rows in the work set forever, re-read by
                        // every chunk from then on. Only a body we never got leaves them pending.
                        if (!answer.Answered) continue;
                        asked = true;
                        var ranking = answer.Ranking;
                        if (ranking == null) continue;
                        // Exact, then the guarded completion for a truncated tag — 11% of this
                        // library's title frames are cut short (MusicTrackTitles.TryMatch).
                        if (MusicTrackTitles.TryMatch(ranking, key, out var found))
                        {
                            listeners = found;
                            // Anything but the first candidate means the file's own tag failed and the
                            // folder answered — worth counting, because a large number here says the
                            // library's artist tags are drifting from its folders.
                            if (attempt > 0) fallbacks++;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        if (Verbose) w.WriteLine($"  ! {track.Id} {candidate}: {ex.Message}");
                    }
                }

                var popularity = MusicPopularity.FromAudience(listeners);
                if (popularity != null) hits++; else misses++;

                // "We asked and something answered" — not "we intended to ask". A track whose every
                // candidate threw has learned nothing and must stay in the queue for a later pass,
                // exactly as a key-less run would leave the whole library.
                if (asked) updates[track.Id] = (popularity, listeners);

                if (Verbose)
                    w.WriteLine($"  {(popularity != null ? "+" : "·")} {track.Id} {tagArtist ?? folderArtist} — {track.Title}: " +
                                $"{(listeners?.ToString("N0") ?? "—")} listeners → {(popularity?.ToString() ?? "—")}");
            }

            if (Apply && updates.Count > 0)
            {
                // Re-read TRACKED rows to write. The batch itself is AsNoTracking and projected, so
                // the read stays cheap for the (common) dry run that writes nothing at all.
                var ids = updates.Keys.ToList();
                var rows = await db.MusicTracks.Where(t => ids.Contains(t.Id)).ToListAsync();
                var now = DateTime.UtcNow;
                foreach (var row in rows)
                {
                    var (score, listeners) = updates[row.Id];
                    // A miss never erases a score an earlier run established (the album leg's rule):
                    // "we don't know this time" is not "nobody has heard of it".
                    if (score != null)
                    {
                        row.Popularity = score;
                        // Banked alongside the score so a re-tune of the scale is an UPDATE rather
                        // than a re-parse, and so the UI can show a DROP the log scale flattens.
                        row.PopularityListeners = listeners;
                        row.PopularitySource = MusicGenreSources.LastFm;
                    }
                    row.PopularityCheckedUtc = now;
                }
                await db.SaveChangesAsync();
            }

            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var remaining = Apply ? Math.Max(0, pendingTotal - updates.Count) : pendingTotal;

            w.WriteLine();
            w.WriteLine($"resolved {batch.Count} track(s) across {rankings.Count} artist(s): " +
                        $"{hits} hit / {misses} miss, {requests} request(s), {cacheHits} served from the disk cache, " +
                        $"{fallbacks} matched on the folder artist, {errors} error(s)" +
                        (Apply ? "." : " — DRY RUN, nothing written."));
            w.WriteLine($"cache: {cache.Root}");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: {nextCursor}, " +
                        $"counts: {{ hits: {hits}, misses: {misses}, requests: {requests}, cacheHits: {cacheHits}, " +
                        $"fallbacks: {fallbacks}, errors: {errors} }} }}");
            // A dry run still FILLS THE CACHE on purpose — the answers it collected are the answers
            // the --apply run parses, with no second trip to Last.fm.
            if (!Apply) w.WriteLine("DRY RUN — nothing written to the database (raw responses ARE cached above). Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
        }

        /// <summary>One track's identity for the lookup, projected so the batch read stays narrow.</summary>
        private sealed record TrackRow(int Id, string Title, string? TagArtist, string ArtistName);

        /// <summary>
        /// Who to ask about this track, best guess first: the file's own artist tag, then the folder
        /// it lives in. Both only when they differ — and a compilation label is never asked about.
        /// </summary>
        private static IEnumerable<string> Candidates(string? tagArtist, string folderArtist)
        {
            if (tagArtist != null) yield return tagArtist;
            if (Usable(folderArtist) && !string.Equals(tagArtist, folderArtist, StringComparison.OrdinalIgnoreCase))
                yield return folderArtist.Trim();
        }

        /// <summary>An artist name worth spending a request on.</summary>
        private static bool Usable(string? name) =>
            !string.IsNullOrWhiteSpace(name) && !NotAnArtist.Contains(name.Trim());

        /// <summary>
        /// What one lookup of an artist produced. <c>Answered</c> and <c>Ranking</c> are DIFFERENT
        /// facts, and the queue's termination depends on the difference: answered-but-empty is
        /// Last.fm saying it has never heard of them — a legitimate negative that must retire their
        /// tracks — while not-answered is a request that never landed, which must not.
        /// </summary>
        private sealed record ArtistAnswer(Dictionary<string, long>? Ranking, bool Answered, bool FromCache, bool WasRequest);

        /// <summary>
        /// One artist's ranked catalogue as normalised-title → listeners, memoised for the run, so an
        /// artist is asked about once however many of their tracks are in the batch.
        /// </summary>
        private async Task<ArtistAnswer> RankingAsync(
            HttpClient http, MusicResponseCache cache, string apiKey, string artist,
            Dictionary<string, ArtistAnswer> memo)
        {
            // Replayed WITHOUT its cost flags: the request was counted the first time, and counting
            // it again would report more traffic than actually happened.
            if (memo.TryGetValue(artist, out var known))
                return known with { FromCache = false, WasRequest = false };

            var key = $"artist.gettoptracks|{artist}";
            var url = "https://ws.audioscrobbler.com/2.0/?method=artist.gettoptracks" +
                      $"&artist={Uri.EscapeDataString(artist)}&limit={TopTrackLimit}" +
                      $"&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";
            var (json, fromCache) = await GetCachedAsync(http, cache, "lastfm", key, url, PopularityTtl,
                // The key is in the URL and the cache is on disk in a repo working tree — store the
                // request with the secret blanked (music-enrich's rule, for music-enrich's reason).
                urlForMeta: url.Replace(Uri.EscapeDataString(apiKey), "«key»"));

            Dictionary<string, long>? ranking = null;
            if (json != null)
            {
                ranking = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, listeners) in MusicLastFm.ParseTopTracks(json))
                {
                    var normalized = MusicTrackTitles.Normalize(name);
                    if (normalized.Length == 0) continue;
                    // Last.fm lists a song several times (the single, the album cut, a live take that
                    // folded onto the same key). The BIGGEST audience wins: they are the same song,
                    // and the question is how many people have heard it.
                    if (!ranking.TryGetValue(normalized, out var existing) || listeners > existing)
                        ranking[normalized] = listeners;
                }
                if (ranking.Count == 0) ranking = null;
            }

            var answer = new ArtistAnswer(ranking, Answered: json != null, fromCache, WasRequest: !fromCache);
            memo[artist] = answer;
            return answer;
        }

        /// <summary>
        /// The cached body, fetching it once if it is not on disk (or is past <paramref name="maxAge"/>).
        /// A network failure is a MISS, never a throw — the caller stamps the negative cache either way.
        /// </summary>
        /// <remarks>
        /// Takes the process-wide <see cref="MusicRemoteArt.Gate"/> so this pass can never run
        /// concurrently with the art warm and double somebody's request rate. It spaces its OWN calls
        /// rather than borrowing <c>SpaceCallAsync</c>: that spacer holds consecutive calls a second
        /// apart because MusicBrainz asks for it, and nothing on this path touches MusicBrainz —
        /// paying MusicBrainz's toll on a Last.fm-only queue of 60,797 rows would turn a four-hour
        /// job into a seventeen-hour one for no one's benefit.
        /// </remarks>
        private async Task<(string? Body, bool FromCache)> GetCachedAsync(
            HttpClient http, MusicResponseCache cache, string source, string key, string url,
            TimeSpan? maxAge, string? urlForMeta = null)
        {
            var cached = await cache.TryReadAsync(source, key, maxAge);
            if (cached != null) return (cached, true);

            await MusicRemoteArt.Gate.WaitAsync();
            try
            {
                await SpaceLastFmCallAsync();
                using var response = await http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) return (null, false);
                await cache.SaveAsync(source, key, body, urlForMeta ?? url, (int)response.StatusCode);
                return (body, false);
            }
            catch (HttpRequestException) { return (null, false); }
            catch (TaskCanceledException) { return (null, false); }
            finally { MusicRemoteArt.Gate.Release(); }
        }

        /// <summary>Last.fm asks for about five requests a second per key; this holds four, under
        /// their limit with room for the clock to be imprecise.</summary>
        private const int LastFmThrottleMs = 250;

        private static DateTime lastLastFmCallUtc = DateTime.MinValue;

        /// <summary>Awaited immediately before a Last.fm call, holding
        /// <see cref="MusicRemoteArt.Gate"/> — so the spacing is real and not a race.</summary>
        private static async Task SpaceLastFmCallAsync()
        {
            var wait = LastFmThrottleMs - (int)(DateTime.UtcNow - lastLastFmCallUtc).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait);
            lastLastFmCallUtc = DateTime.UtcNow;
        }
    }
}
