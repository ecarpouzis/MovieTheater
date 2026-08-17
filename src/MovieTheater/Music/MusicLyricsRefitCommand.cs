using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Repairs stored LRCs whose cues cannot belong to the file they are attached to.
    ///
    /// <para><b>The fault.</b> <c>music-lyrics</c> asks LRCLIB's <c>/api/get</c> for
    /// (artist, title, album, duration), and LRCLIB matches on the duration the UPLOADER recorded —
    /// which is metadata, not a property of the timestamps. When the two disagree we store lyrics
    /// timed against a different, longer version, and every line then lands late by the difference.
    /// Found 2026-08-17 on CHVRCHES' <i>Recover</i>: a 225.9 s track holding cues out to 4:10, so the
    /// pane bolded the 0:56 line while the song was half a verse further on. The scrub bar was right
    /// the whole time, which is what made it read as a player bug.</para>
    ///
    /// <para><b>The test, and the trap in it.</b> A cue past the end of the file can never fire — but
    /// that alone is a SMELL, not proof: a tail written at the fade, or a duration measured a hair
    /// short, produces the same two or three seconds. Proof is a mismatch too large to explain that
    /// way, and it is proportional (<see cref="MusicLyricsFit.IsVersionMismatch"/>: past
    /// max(10 s, 5% of the track)). Only proof authorises a write here. The first run of this pass
    /// used the smell instead and cleared 72 good LRCs over 2-3 second tails — they came back out of
    /// the backup table, which is why that table is not optional.</para>
    ///
    /// <para><b>The repair.</b> <c>/api/search</c> returns every LRC LRCLIB holds for the track;
    /// this takes the closest-duration candidate whose cues actually FIT, and writes that. When
    /// nothing fits, it clears <c>SyncedLrc</c> and keeps <c>PlainText</c> — untimed words the pane
    /// creeps through beat confidently wrong timings. Nothing is ever deleted outright.</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run by default; <c>--apply</c> writes. TWO budgets, because
    /// the scan is cheap and the repair costs a throttled round trip: <c>--scan</c> rows examined and
    /// <c>--limit</c> rows repaired per run, resumable via <c>--after &lt;trackId&gt;</c>, printing
    /// <c>{ examined, bad, repaired, cleared, nextCursor }</c>. Re-running is idempotent: a row that
    /// now fits is no longer in the work set.</para>
    ///
    /// <para>⚠ The live DB is shared with prod. Back the rows up before <c>--apply</c>:
    /// <c>SELECT * INTO MusicTrackLyrics_bak_yyyyMMdd_refit FROM MusicTrackLyrics</c>.</para>
    /// </summary>
    [Command("music-lyrics-refit", Description = "Re-fetch or clear stored LRCs whose cues run past the end of the track (dry-run unless --apply).")]
    public class MusicLyricsRefitCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("scan", Description = "Max lyrics rows to EXAMINE this run (default 3000).")]
        public int Scan { get; set; } = 3000;

        [CommandOption("limit", Description = "Max bad rows to REPAIR this run — each costs an LRCLIB round trip (default 100).")]
        public int Limit { get; set; } = 100;

        [CommandOption("after", Description = "Resume cursor: skip tracks whose id is ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("verbose", Description = "Print a line per row examined, not just the bad ones.")]
        public bool Verbose { get; set; }

        /// <summary>~2 requests/second — LRCLIB publishes no hard limit but asks callers to be gentle.</summary>
        private const int ThrottleMs = 500;
        private const string UserAgent = "MovieTheater-music-lyrics-refit/1.0 (private home media library)";

        /// <summary>How far a candidate's duration may sit from ours. Wider than LRCLIB's own ±2 s
        /// matching on purpose: the whole point is that the metadata is unreliable, so the cue-fit
        /// test below is what actually decides — this only keeps a cover or a live cut out.</summary>
        private const double DurationSlackSec = 6.0;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public MusicLyricsRefitCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            // Only rows that HAVE timings and a duration to judge them against. The fit test itself
            // cannot be expressed in SQL, so the scan is the bounded part and the filter is local.
            var rows = await db.MusicTrackLyrics
                .Where(l => l.SyncedLrc != null && l.TrackId > After)
                .OrderBy(l => l.TrackId)
                .Take(Math.Max(1, Scan))
                .Join(db.MusicTracks.Include(t => t.Artist).Include(t => t.Album),
                      l => l.TrackId, t => t.Id, (l, t) => new { Lyrics = l, Track = t })
                .ToListAsync();

            // Two populations, and only one of them may be written to. `bad` is proof of a different
            // recording; `marginal` merely has a cue hanging off the end, which a fade or a rounded
            // duration explains — the first run of this pass treated the two alike and deleted 72 good
            // LRCs over 2-3 second tails. They are reported, never touched.
            var bad = rows
                .Where(r => MusicLyricsFit.IsVersionMismatch(r.Lyrics.SyncedLrc, r.Track.DurationSec))
                .ToList();
            var marginal = rows.Count(r => !MusicLyricsFit.CuesFit(r.Lyrics.SyncedLrc, r.Track.DurationSec))
                           - bad.Count;

            w.WriteLine($"examined {rows.Count} synced rows from id > {After}: {bad.Count} are a different "
                        + $"recording, {marginal} overhang by a hair (left alone).");

            int repaired = 0, cleared = 0, attempted = 0;
            using var http = CreateHttp();

            foreach (var row in bad)
            {
                if (attempted >= Math.Max(1, Limit)) break;
                attempted++;
                var track = row.Track;
                var duration = (double)track.DurationSec!;
                var was = MusicLyricsFit.LastCueSec(row.Lyrics.SyncedLrc) ?? 0;

                await Task.Delay(ThrottleMs);
                var fitted = await BestFittingAsync(http, track, duration);

                if (fitted != null)
                {
                    repaired++;
                    if (Apply)
                    {
                        row.Lyrics.SyncedLrc = fitted.Value.synced;
                        // Only fill a missing plain text — an existing one may have come from a better
                        // source than whatever this candidate carries.
                        if (string.IsNullOrWhiteSpace(row.Lyrics.PlainText) && fitted.Value.plain != null)
                            row.Lyrics.PlainText = fitted.Value.plain;
                        row.Lyrics.FetchedUtc = DateTime.UtcNow;
                    }
                    w.WriteLine($"  ↻ {track.Id} {track.Artist.Name} — {track.Title}: "
                                + $"{Fmt(was)} → {Fmt(MusicLyricsFit.LastCueSec(fitted.Value.synced) ?? 0)} "
                                + $"(track {Fmt(duration)})");
                }
                else
                {
                    cleared++;
                    if (Apply) row.Lyrics.SyncedLrc = null;   // PlainText survives: the pane creeps it
                    w.WriteLine($"  × {track.Id} {track.Artist.Name} — {track.Title}: "
                                + $"nothing on LRCLIB fits {Fmt(duration)} (had cues to {Fmt(was)})"
                                + (string.IsNullOrWhiteSpace(row.Lyrics.PlainText) ? " — NO plain text either" : ""));
                }
            }

            if (Apply) await db.SaveChangesAsync();

            var nextCursor = rows.Count > 0 ? rows[^1].Lyrics.TrackId : After;
            var scanDone = rows.Count < Math.Max(1, Scan);
            w.WriteLine();
            w.WriteLine($"{{ examined: {rows.Count}, bad: {bad.Count}, marginal: {marginal}, "
                        + $"repaired: {repaired}, cleared: {cleared}, nextCursor: {nextCursor} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply (back the table up first).");
            if (bad.Count > attempted) w.WriteLine($"{bad.Count - attempted} bad rows in THIS scan window were left for the next run.");
            if (!scanDone) w.WriteLine($"More to scan: re-run with --after {nextCursor}.");
            else w.WriteLine("Scan reached the end of the table.");
        }

        private static string Fmt(double sec) => TimeSpan.FromSeconds(sec).ToString(sec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return http;
        }

        /// <summary>
        /// The best LRC LRCLIB holds for this track that could actually BE this track: synced, cues
        /// inside the running time, duration in the same neighbourhood. Ranked by how close the
        /// candidate's duration is to ours, then by how many cues it carries — between two fitting
        /// candidates the denser one is the more complete transcription.
        /// </summary>
        private static async Task<(string synced, string? plain)?> BestFittingAsync(HttpClient http, MusicTrack track, double duration)
        {
            var query = "artist_name=" + Uri.EscapeDataString(track.Artist.Name)
                      + "&track_name=" + Uri.EscapeDataString(track.Title);
            string json;
            try
            {
                var response = await http.GetAsync("https://lrclib.net/api/search?" + query);
                if (!response.IsSuccessStatusCode) return null;
                json = await response.Content.ReadAsStringAsync();
            }
            catch
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

                var best = doc.RootElement.EnumerateArray()
                    .Select(e => new
                    {
                        Synced = Text(e, "syncedLyrics"),
                        Plain = Text(e, "plainLyrics"),
                        Duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                            ? d.GetDouble() : 0,
                    })
                    .Where(c => c.Synced != null
                                && c.Duration > 0
                                && Math.Abs(c.Duration - duration) <= DurationSlackSec
                                && MusicLyricsFit.CuesFit(c.Synced, duration))
                    .OrderBy(c => Math.Abs(c.Duration - duration))
                    .ThenByDescending(c => c.Synced!.Count(ch => ch == '['))
                    .FirstOrDefault();

                return best == null ? null : (best.Synced!, best.Plain);
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
