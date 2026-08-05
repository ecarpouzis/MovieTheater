using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

        [CommandOption("verbose", Description = "Print a line per track, not just the hits.")]
        public bool Verbose { get; set; }

        /// <summary>~2 requests/second — LRCLIB publishes no hard limit but asks callers to be gentle.</summary>
        private const int ThrottleMs = 500;
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

            // Work set: playable tracks with no lyrics row and no prior LRCLIB attempt. The "no lyrics
            // row" test is a left join so a track whose row exists (embedded/sidecar) is never touched.
            var pending = db.MusicTracks
                .Where(t => t.MissingSinceUtc == null
                            && t.LyricsCheckedUtc == null
                            && !db.MusicTrackLyrics.Any(l => l.TrackId == t.Id));

            var totalPending = await pending.Where(t => t.Id > After).CountAsync();
            var batch = await pending
                .Where(t => t.Id > After)
                .OrderBy(t => t.Id)
                .Include(t => t.Artist)
                .Include(t => t.Album)
                .Take(Math.Max(1, Limit))
                .ToListAsync();

            int synced = 0, plainOnly = 0, misses = 0;
            using var http = CreateHttp();

            foreach (var track in batch)
            {
                await Task.Delay(ThrottleMs);
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
                        PlainText = result.Value.plain,
                        SyncedLrc = result.Value.synced,
                        Source = "lrclib",
                        FetchedUtc = DateTime.UtcNow,
                    });
                }

                if (result.Value.synced != null) synced++; else plainOnly++;
                if (Verbose)
                    w.WriteLine($"  + {track.Id} {track.Artist.Name} — {track.Title} ({(result.Value.synced != null ? "synced" : "plain")})");
            }

            if (Apply) await db.SaveChangesAsync();

            var remaining = Math.Max(0, totalPending - batch.Count);
            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var withLyrics = await db.MusicTrackLyrics.CountAsync();
            var totalTracks = await db.MusicTracks.CountAsync(t => t.MissingSinceUtc == null);

            w.WriteLine();
            w.WriteLine($"this run: {synced} synced, {plainOnly} plain-only, {misses} no match.");
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

        /// <summary>One LRCLIB /api/get lookup. Returns null on a miss, a network error, or a hit that
        /// carries no actual text (LRCLIB returns instrumental entries with both fields empty).</summary>
        private static async Task<(string? plain, string? synced)?> FetchAsync(HttpClient http, MusicTrack track)
        {
            var query = new List<string>
            {
                "artist_name=" + Uri.EscapeDataString(track.Artist.Name),
                "track_name=" + Uri.EscapeDataString(track.Title),
            };
            if (track.Album != null)
                query.Add("album_name=" + Uri.EscapeDataString(track.Album.Title));
            if (track.DurationSec is double d && d > 0)
                query.Add("duration=" + ((int)Math.Round(d)).ToString());

            string json;
            try
            {
                var response = await http.GetAsync("https://lrclib.net/api/get?" + string.Join("&", query));
                if (!response.IsSuccessStatusCode) return null; // 404 = LRCLIB has nothing for this track
                json = await response.Content.ReadAsStringAsync();
            }
            catch
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var plain = Text(root, "plainLyrics");
                var syncedLrc = Text(root, "syncedLyrics");
                if (plain == null && syncedLrc == null) return null;
                return (plain, syncedLrc);
            }
            catch
            {
                return null;
            }
        }

        private static string? Text(JsonElement root, string property) =>
            root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(el.GetString())
                ? el.GetString()
                : null;
    }
}
