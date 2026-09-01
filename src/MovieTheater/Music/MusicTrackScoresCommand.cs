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
    /// Fills <see cref="MusicTrackScore"/> from every source that will talk to us, and recomputes the
    /// library-wide consensus ranking (2026-08-31).
    /// </summary>
    /// <remarks>
    /// <para><b>Three jobs, one command, because they are one pipeline.</b> <c>--source lastfm</c>
    /// seeds the table from data already in the database (no network at all — the listener counts
    /// were banked by <c>music-track-popularity</c>). <c>--source deezer</c> fetches the one service
    /// that needs no credentials. <c>--rank-only</c> recomputes percentiles and the consensus from
    /// whatever rows exist. A run with no <c>--source</c> does the ranking, so the last step is never
    /// forgotten.</para>
    ///
    /// <para><b>Album-first for Deezer</b>, exactly as the Last.fm pass is artist-first and for the
    /// same measured reason: per-track search cost one request each and matched 65% of a sample,
    /// while two requests per ALBUM matched 77% — ~8,300 requests for this library instead of
    /// ~60,000. The album match is GATED (<see cref="MusicDeezer.AcceptsAlbum"/>) because the
    /// measured failure mode is a confident answer about the WRONG record, not a missing one.</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry run unless <c>--apply</c>; bounded by <c>--take</c> ALBUMS
    /// (Deezer) or TRACKS (lastfm seed); resumable by <c>--after</c>; idempotent because a source's
    /// row for a track is REPLACED, never appended. Polite: Deezer calls are spaced and go through
    /// the process-wide gate so this can never run alongside the art warm at double rate.</para>
    /// </remarks>
    [Command("music-track-scores", Description = "Fill per-source track popularity (lastfm seed, deezer fetch) and recompute the library ranking. Dry-run unless --apply.")]
    public class MusicTrackScoresCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("dry-run", Description = "Force a dry run. Redundant (dry is the default) but accepted, so the safe spelling is never a typo.")]
        public bool DryRun { get; set; }

        [CommandOption("source", Description = "Which source to fill: lastfm (offline seed) | deezer. Omit to only recompute the ranking.")]
        public string? Source { get; set; }

        [CommandOption("rank-only", Description = "Skip fetching; just recompute percentiles and the consensus from the rows already stored.")]
        public bool RankOnly { get; set; }

        [CommandOption("take", Description = "Max ALBUMS (deezer) or TRACKS (lastfm) to handle this run (default 300 / 20000).")]
        public int? Take { get; set; }

        [CommandOption("after", Description = "Resume cursor: skip ids ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("cache-dir", Description = "Raw response cache root. Default: data/music-cache (gitignored).")]
        public string? CacheDir { get; set; }

        [CommandOption("verbose", Description = "Print a line per album/track, not just the summary.")]
        public bool Verbose { get; set; }

        /// <summary>Deezer publishes no documented rate limit beyond "be reasonable"; four a second is
        /// well inside what their own clients do and finishes the library in about half an hour.</summary>
        private const int DeezerThrottleMs = 250;

        /// <summary>A ranking drifts as slowly as popularity does — the same window the album and
        /// track popularity passes hold their answers for.</summary>
        private static readonly TimeSpan ScoreTtl = TimeSpan.FromDays(120);

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public MusicTrackScoresCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (DryRun) Apply = false;

            await using var db = await dbFactory.CreateDbContextAsync();

            var source = (Source ?? "").Trim().ToLowerInvariant();
            if (!RankOnly && source.Length > 0)
            {
                switch (source)
                {
                    case MusicScoreSources.LastFm: await SeedLastFmAsync(db, w); break;
                    case MusicScoreSources.Deezer: await FetchDeezerAsync(db, w); break;
                    case MusicScoreSources.Spotify:
                        // Named explicitly rather than "unknown source": Spotify is the best available
                        // track-popularity signal and the ONLY thing missing is a free app
                        // registration, so the message has to say that and not look like a typo.
                        w.WriteLine("Spotify needs SpotifyClientId + SpotifyClientSecret in config (a free app registration) — not configured, nothing done.");
                        return;
                    default:
                        w.WriteLine($"Unknown --source '{Source}': use lastfm, deezer, or omit it to rank.");
                        return;
                }
            }

            // The ranking always runs last, because a fetch that did not end in a recomputed
            // consensus has changed the evidence without changing the answer anybody reads.
            await RankAsync(db, w);
        }

        // ── lastfm: an offline seed from data already banked ────────────────────────────────────

        /// <summary>
        /// Copies the Last.fm listener counts already on <see cref="MusicTrack"/> into the per-source
        /// table. No network: those counts were fetched and stored by <c>music-track-popularity</c>,
        /// and re-asking for them would be paying twice for the same answer.
        /// </summary>
        private async Task SeedLastFmAsync(MovieDb db, ConsoleWriter w)
        {
            var take = Take ?? 20000;
            var pending = await db.MusicTracks.AsNoTracking()
                .Where(t => t.PopularityListeners != null && t.Id > After)
                .CountAsync();
            var batch = await db.MusicTracks.AsNoTracking()
                .Where(t => t.PopularityListeners != null && t.Id > After)
                .OrderBy(t => t.Id)
                .Take(Math.Max(1, take))
                .Select(t => new { t.Id, Listeners = t.PopularityListeners!.Value })
                .ToListAsync();

            var written = 0;
            if (Apply && batch.Count > 0)
            {
                var ids = batch.Select(b => b.Id).ToList();
                var existing = await db.MusicTrackScores
                    .Where(s => s.Source == MusicScoreSources.LastFm && ids.Contains(s.MusicTrackId))
                    .ToDictionaryAsync(s => s.MusicTrackId);
                var now = DateTime.UtcNow;
                foreach (var t in batch)
                {
                    // The percentile is filled by the ranking pass; what this seed owns is the RAW
                    // value, which is the thing that came from outside.
                    if (existing.TryGetValue(t.Id, out var row)) { row.RawValue = t.Listeners; row.CheckedUtc = now; }
                    else db.MusicTrackScores.Add(new MusicTrackScore
                    {
                        MusicTrackId = t.Id, Source = MusicScoreSources.LastFm,
                        Score = 0, RawValue = t.Listeners, CheckedUtc = now,
                    });
                    written++;
                }
                await db.SaveChangesAsync();
            }
            else written = batch.Count;

            var next = batch.Count > 0 ? batch[^1].Id : After;
            w.WriteLine($"lastfm seed: {batch.Count} track(s) {(Apply ? "written" : "would be written")}, " +
                        $"{Math.Max(0, pending - batch.Count)} remaining.");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {Math.Max(0, pending - batch.Count)}, nextCursor: {next}, counts: {{ rows: {written} }} }}");
        }

        // ── deezer: two requests per album, gated ───────────────────────────────────────────────

        private async Task FetchDeezerAsync(MovieDb db, ConsoleWriter w)
        {
            var take = Take ?? 300;
            var cache = new MusicResponseCache(CacheDir);
            using var http = MusicRemoteArt.CreateHttp();

            // The work set is albums we have not scored yet: an album none of whose tracks carry a
            // deezer row. Cheaper and more honest than a per-album stamp column, and it means a
            // partially-matched album is not re-fetched forever.
            var scored = db.MusicTrackScores.Where(s => s.Source == MusicScoreSources.Deezer)
                .Select(s => s.Track.AlbumId);
            var query = db.MusicAlbums.AsNoTracking()
                .Where(a => a.Id > After && !scored.Contains(a.Id));

            var pending = await query.CountAsync();
            var albums = await query.OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.Title, Artist = a.Artist.Name })
                .Take(Math.Max(1, take))
                .ToListAsync();

            int found = 0, rejected = 0, missing = 0, rows = 0, requests = 0, cacheHits = 0, errors = 0;

            foreach (var album in albums)
            {
                var ours = await db.MusicTracks.AsNoTracking()
                    .Where(t => t.AlbumId == album.Id && t.MissingSinceUtc == null)
                    .Select(t => new { t.Id, t.Title })
                    .ToListAsync();
                if (ours.Count == 0) continue;

                try
                {
                    var searchUrl = "https://api.deezer.com/search/album?limit=1&q=" +
                                    Uri.EscapeDataString($"{album.Artist} {album.Title}");
                    var (searchBody, fromCache) = await GetCachedAsync(http, cache, $"album.search|{album.Artist}|{album.Title}", searchUrl);
                    if (fromCache) cacheHits++; else requests++;

                    var hit = MusicDeezer.ParseAlbumSearch(searchBody);
                    if (hit == null) { missing++; if (Verbose) w.WriteLine($"  - {album.Id} no album: {album.Artist} — {album.Title}"); continue; }

                    // The gate. A confident answer about the WRONG record is the measured failure.
                    if (!MusicDeezer.AcceptsAlbum(hit.Value.Title, hit.Value.Artist, album.Title, album.Artist))
                    {
                        rejected++;
                        if (Verbose) w.WriteLine($"  ! {album.Id} REJECTED '{hit.Value.Artist} — {hit.Value.Title}' for '{album.Artist} — {album.Title}'");
                        continue;
                    }
                    found++;

                    var tracksUrl = $"https://api.deezer.com/album/{hit.Value.Id}/tracks?limit=100";
                    var (tracksBody, tracksCached) = await GetCachedAsync(http, cache, $"album.tracks|{hit.Value.Id}", tracksUrl);
                    if (tracksCached) cacheHits++; else requests++;

                    var theirs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (title, rank) in MusicDeezer.ParseTracks(tracksBody))
                    {
                        var key = MusicTrackTitles.Normalize(title);
                        if (key.Length == 0) continue;
                        // Same rule as the Last.fm pass: one song listed twice keeps the bigger number.
                        if (!theirs.TryGetValue(key, out var existing) || rank > existing) theirs[key] = rank;
                    }

                    var matched = 0;
                    var now = DateTime.UtcNow;
                    foreach (var track in ours)
                    {
                        if (!MusicTrackTitles.TryMatch(theirs, MusicTrackTitles.Normalize(track.Title), out var rank)) continue;
                        matched++;
                        if (!Apply) continue;
                        var row = await db.MusicTrackScores
                            .FirstOrDefaultAsync(s => s.MusicTrackId == track.Id && s.Source == MusicScoreSources.Deezer);
                        if (row == null)
                            db.MusicTrackScores.Add(new MusicTrackScore
                            {
                                MusicTrackId = track.Id, Source = MusicScoreSources.Deezer,
                                Score = 0, RawValue = rank, CheckedUtc = now,
                            });
                        else { row.RawValue = rank; row.CheckedUtc = now; }
                    }
                    rows += matched;
                    if (Apply) await db.SaveChangesAsync();
                    if (Verbose) w.WriteLine($"  + {album.Id} {album.Artist} — {album.Title}: {matched}/{ours.Count}");
                }
                catch (Exception ex) { errors++; if (Verbose) w.WriteLine($"  ! {album.Id} {ex.Message}"); }
            }

            var next = albums.Count > 0 ? albums[^1].Id : After;
            w.WriteLine($"deezer: {albums.Count} album(s) — {found} accepted, {rejected} rejected by the match gate, " +
                        $"{missing} not on Deezer; {rows} track score(s) {(Apply ? "written" : "matched")}, " +
                        $"{requests} request(s), {cacheHits} from cache, {errors} error(s).");
            w.WriteLine($"{{ processed: {albums.Count}, remaining: {Math.Max(0, pending - albums.Count)}, nextCursor: {next}, " +
                        $"counts: {{ accepted: {found}, rejected: {rejected}, missing: {missing}, rows: {rows}, requests: {requests}, cacheHits: {cacheHits}, errors: {errors} }} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written to the database (raw responses ARE cached).");
        }

        // ── the ranking ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Recomputes every source's percentiles and each track's consensus. Whole-library and
        /// unbounded ON PURPOSE: a percentile is a statement about a population, so a partial
        /// recompute would mix old positions with new ones and produce a ranking that never existed.
        /// It is pure arithmetic over ~120k small rows and costs no network.
        /// </summary>
        private async Task RankAsync(MovieDb db, ConsoleWriter w)
        {
            var all = await db.MusicTrackScores
                .Select(s => new { s.Id, s.MusicTrackId, s.Source, s.RawValue, s.Score })
                .ToListAsync();
            if (all.Count == 0) { w.WriteLine("ranking: no scores stored yet — nothing to rank."); return; }

            var percentileByRow = new Dictionary<int, int>();
            foreach (var group in all.Where(s => s.RawValue != null).GroupBy(s => s.Source))
            {
                var values = group.Select(g => (g.Id, g.RawValue!.Value)).ToList();
                var ranked = MusicScoreRanking.Percentiles(values);
                foreach (var (rowId, percentile) in ranked) percentileByRow[rowId] = percentile;
                w.WriteLine($"  {group.Key}: {values.Count} scored track(s)");
            }

            var consensus = all
                .Where(s => percentileByRow.ContainsKey(s.Id))
                .GroupBy(s => s.MusicTrackId)
                .ToDictionary(g => g.Key, g => MusicScoreRanking.Consensus(g.Select(x => percentileByRow[x.Id])));

            var multi = consensus.Count(c => c.Value.Sources > 1);
            w.WriteLine($"ranking: {consensus.Count} track(s) ranked, {multi} with more than one source agreeing.");

            if (!Apply) { w.WriteLine("DRY RUN — the ranking was computed but not written."); return; }

            // Written in one pass per table rather than row-by-row: 60k tracked entities would be a
            // minutes-long SaveChanges, and this is arithmetic the database can do in place.
            var scoreRows = await db.MusicTrackScores.ToListAsync();
            foreach (var row in scoreRows)
                if (percentileByRow.TryGetValue(row.Id, out var p)) row.Score = p;

            var trackRows = await db.MusicTracks
                .Where(t => t.PopularityRank != null || t.PopularityListeners != null)
                .ToListAsync();
            var touched = new HashSet<int>(trackRows.Select(t => t.Id));
            foreach (var id in consensus.Keys.Where(k => !touched.Contains(k)))
                trackRows.Add(await db.MusicTracks.FirstAsync(t => t.Id == id));

            foreach (var track in trackRows)
            {
                if (consensus.TryGetValue(track.Id, out var c))
                {
                    track.PopularityRank = c.Rank;
                    track.PopularityRankSources = c.Sources;
                }
                else
                {
                    // A track every source has now forgotten loses its rank rather than keeping a
                    // stale one — the count is what says whether to believe it.
                    track.PopularityRank = null;
                    track.PopularityRankSources = 0;
                }
            }
            await db.SaveChangesAsync();
            w.WriteLine($"ranking written: {scoreRows.Count} score row(s), {trackRows.Count} track row(s).");
        }

        // ── the wire ────────────────────────────────────────────────────────────────────────────

        private static DateTime lastDeezerCallUtc = DateTime.MinValue;

        /// <summary>
        /// The cached body, fetching it once if it is not on disk. A network failure is a MISS, never
        /// a throw. Takes the process-wide gate so this can never run beside the art warm at double
        /// rate, and spaces its own calls — Deezer's tolerance is not MusicBrainz's one-per-second.
        /// </summary>
        private async Task<(string? Body, bool FromCache)> GetCachedAsync(
            HttpClient http, MusicResponseCache cache, string key, string url)
        {
            var cached = await cache.TryReadAsync("deezer", key, ScoreTtl);
            if (cached != null) return (cached, true);

            await MusicRemoteArt.Gate.WaitAsync();
            try
            {
                var wait = DeezerThrottleMs - (int)(DateTime.UtcNow - lastDeezerCallUtc).TotalMilliseconds;
                if (wait > 0) await Task.Delay(wait);
                lastDeezerCallUtc = DateTime.UtcNow;

                using var response = await http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) return (null, false);
                await cache.SaveAsync("deezer", key, body, url, (int)response.StatusCode);
                return (body, false);
            }
            catch (HttpRequestException) { return (null, false); }
            catch (TaskCanceledException) { return (null, false); }
            finally { MusicRemoteArt.Gate.Release(); }
        }
    }
}
